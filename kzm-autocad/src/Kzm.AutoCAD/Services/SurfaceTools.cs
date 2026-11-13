using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    /// <summary>One vertex of a structure line being prepared in the panel.</summary>
    internal sealed class LineVertex
    {
        internal XY Position { get; set; }
        /// <summary>Distance along the line from its start, m (for interpolation by length).</summary>
        internal double Station { get; set; }
        internal double? Elevation { get; set; }
        internal string Origin { get; set; } = string.Empty;
        /// <summary>Point of the surface standing at this vertex (within 0,01 m), if any.</summary>
        internal string ExistingHandle { get; set; }
        internal double? ExistingElevation { get; set; }
        internal string ExistingNumber { get; set; }
        /// <summary>Intermediate point of an arc segment (arcs are followed every 5°).</summary>
        internal bool OnArc { get; set; }
    }

    internal sealed class LineDraft
    {
        internal string SurfaceId { get; set; }
        internal string SourceHandle { get; set; }
        internal string SourceDescription { get; set; }
        internal bool Closed { get; set; }
        internal string Name { get; set; }
        internal List<LineVertex> Vertices { get; } = new List<LineVertex>();
    }

    /// <summary>
    /// Структурные линии (задание п. 1, 2.3), замена диагонали и плоскости (п. 2.3, 3).
    /// Вершины структурной линии — обычные точки поверхности (блоки «КЗМ_ТОЧКА»): их можно
    /// двигать и править как остальные; линия хранит их порядок, рёбра между соседними
    /// точками триангуляция сохраняет.
    /// </summary>
    internal static class SurfaceTools
    {
        // ---------- structure lines ----------

        /// <summary>Vertices of a polyline (LWPOLYLINE with arcs, 2D/3D polyline, line) with the elevations it already gives.</summary>
        internal static LineDraft ReadPolyline(Database database, Transaction transaction, Entity entity, string surfaceId, KzmSettings settings)
        {
            var draft = new LineDraft { SurfaceId = surfaceId, SourceHandle = entity.Handle.ToString() };
            var raw = new List<(XY P, double? Z, bool Arc)>();
            string zOrigin = null;
            switch (entity)
            {
                case Polyline polyline:
                {
                    int n = polyline.NumberOfVertices;
                    draft.Closed = polyline.Closed;
                    double? z = Math.Abs(polyline.Elevation) > 1e-9 ? polyline.Elevation : (double?)null;
                    if (z.HasValue) zOrigin = "отметка (Elevation) полилинии";
                    for (int i = 0; i < n; i++)
                    {
                        var a = polyline.GetPoint2dAt(i);
                        raw.Add((new XY(a.X, a.Y), z, false));
                        if (!draft.Closed && i == n - 1) break;
                        double bulge = polyline.GetBulgeAt(i);
                        if (Math.Abs(bulge) < 1e-9) continue;
                        var b = polyline.GetPoint2dAt((i + 1) % n);
                        foreach (var q in ArcPoints(new XY(a.X, a.Y), new XY(b.X, b.Y), bulge)) raw.Add((q, z, true));
                    }
                    draft.SourceDescription = "полилиния " + entity.Handle + ", вершин " + n;
                    break;
                }
                case Polyline3d polyline3d:
                {
                    draft.Closed = polyline3d.Closed;
                    foreach (ObjectId id in polyline3d)
                    {
                        var vertex = (PolylineVertex3d)transaction.GetObject(id, OpenMode.ForRead);
                        if (vertex.VertexType == Vertex3dType.ControlVertex) continue;
                        raw.Add((new XY(vertex.Position.X, vertex.Position.Y), vertex.Position.Z, false));
                    }
                    if (raw.Any(v => Math.Abs(v.Z.Value) > 1e-9)) zOrigin = "3D-полилиния"; else raw = raw.Select(v => (v.P, (double?)null, v.Arc)).ToList();
                    draft.SourceDescription = "3D-полилиния " + entity.Handle + ", вершин " + raw.Count;
                    break;
                }
                case Polyline2d polyline2d:
                {
                    draft.Closed = polyline2d.Closed;
                    double? z = Math.Abs(polyline2d.Elevation) > 1e-9 ? polyline2d.Elevation : (double?)null;
                    if (z.HasValue) zOrigin = "отметка (Elevation) полилинии";
                    foreach (ObjectId id in polyline2d)
                    {
                        var vertex = (Vertex2d)transaction.GetObject(id, OpenMode.ForRead);
                        if (vertex.VertexType == Vertex2dType.SplineControlVertex) continue;
                        raw.Add((new XY(vertex.Position.X, vertex.Position.Y), z, false));
                    }
                    draft.SourceDescription = "2D-полилиния " + entity.Handle + ", вершин " + raw.Count;
                    break;
                }
                case Line line:
                {
                    raw.Add((new XY(line.StartPoint.X, line.StartPoint.Y), line.StartPoint.Z, false));
                    raw.Add((new XY(line.EndPoint.X, line.EndPoint.Y), line.EndPoint.Z, false));
                    if (raw.Any(v => Math.Abs(v.Z.Value) > 1e-9)) zOrigin = "Z отрезка"; else raw = raw.Select(v => (v.P, (double?)null, v.Arc)).ToList();
                    draft.SourceDescription = "отрезок " + entity.Handle;
                    break;
                }
                default:
                    throw new KzmException("Структурная линия строится по полилинии (LWPOLYLINE, 2D или 3D) или отрезку.");
            }
            // Repeated vertices are dropped; a closing vertex equal to the first makes the line closed.
            var cleaned = new List<(XY P, double? Z, bool Arc)>();
            foreach (var v in raw)
                if (cleaned.Count == 0 || cleaned[cleaned.Count - 1].P.DistanceTo(v.P) > 1e-6) cleaned.Add(v);
            if (cleaned.Count > 2 && cleaned[0].P.DistanceTo(cleaned[cleaned.Count - 1].P) <= 1e-6)
            {
                cleaned.RemoveAt(cleaned.Count - 1);
                draft.Closed = true;
            }
            if (cleaned.Count < 2) throw new KzmException("В линии меньше двух различных вершин.");

            var points = PointBlocks.Read(database, transaction, settings, surfaceId);
            var index = new NearestPoints(points);
            double station = 0;
            for (int i = 0; i < cleaned.Count; i++)
            {
                if (i > 0) station += cleaned[i - 1].P.DistanceTo(cleaned[i].P);
                var vertex = new LineVertex { Position = cleaned[i].P, Station = station, OnArc = cleaned[i].Arc, Elevation = cleaned[i].Z };
                if (vertex.Elevation.HasValue) vertex.Origin = zOrigin;
                var existing = index.Within(vertex.Position, KzmSettings.DuplicateTolerance);
                if (existing != null)
                {
                    vertex.ExistingHandle = existing.Handle;
                    vertex.ExistingNumber = existing.Number;
                    if (existing.Elevation.IsValid)
                    {
                        vertex.ExistingElevation = existing.Elevation.Value;
                        if (!vertex.Elevation.HasValue)
                        {
                            vertex.Elevation = existing.Elevation.Value;
                            vertex.Origin = "точка поверхности";
                        }
                    }
                }
                draft.Vertices.Add(vertex);
            }
            draft.Name = "Линия " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return draft;
        }

        /// <summary>Points of an arc segment between a and b (without a and b), every 5° at most.</summary>
        private static IEnumerable<XY> ArcPoints(XY a, XY b, double bulge)
        {
            double angle = 4 * Math.Atan(bulge);
            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(angle) / (Math.PI / 36)));
            double chord = a.DistanceTo(b);
            if (chord < 1e-12) yield break;
            double radius = chord / (2 * Math.Sin(Math.Abs(angle) / 2));
            double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
            double d = Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4));
            double nx = -(b.Y - a.Y) / chord, ny = (b.X - a.X) / chord;
            double side = (bulge > 0) == (Math.Abs(angle) < Math.PI) ? 1 : -1;
            double cx = mx + side * nx * d, cy = my + side * ny * d;
            double start = Math.Atan2(a.Y - cy, a.X - cx);
            for (int k = 1; k < steps; k++)
            {
                double t = start + angle * k / steps;
                yield return new XY(cx + radius * Math.Cos(t), cy + radius * Math.Sin(t));
            }
        }

        /// <summary>Empty elevations between two known ones — linear by length (задание п. 2.3). Returns how many were filled.</summary>
        internal static int FillGaps(LineDraft draft)
        {
            int filled = 0;
            var vertices = draft.Vertices;
            for (int i = 0; i < vertices.Count; i++)
            {
                if (vertices[i].Elevation.HasValue) continue;
                int before = i - 1, after = i + 1;
                while (before >= 0 && !vertices[before].Elevation.HasValue) before--;
                while (after < vertices.Count && !vertices[after].Elevation.HasValue) after++;
                if (before < 0 || after >= vertices.Count) continue;
                double s0 = vertices[before].Station, s1 = vertices[after].Station;
                double t = s1 > s0 ? (vertices[i].Station - s0) / (s1 - s0) : 0;
                vertices[i].Elevation = vertices[before].Elevation.Value + t * (vertices[after].Elevation.Value - vertices[before].Elevation.Value);
                vertices[i].Origin = "интерполяция по длине";
                filled++;
            }
            return filled;
        }

        /// <summary>«Отметки начала и конца»: all vertices get the linear elevation by length between them.</summary>
        internal static void Interpolate(LineDraft draft, double start, double end)
        {
            var vertices = draft.Vertices;
            double length = vertices[vertices.Count - 1].Station;
            for (int i = 0; i < vertices.Count; i++)
            {
                double t = length > 0 ? vertices[i].Station / length : 0;
                vertices[i].Elevation = start + t * (end - start);
                vertices[i].Origin = i == 0 ? "начало" : i == vertices.Count - 1 ? "конец" : "интерполяция по длине";
            }
        }

        /// <summary>
        /// Creates the structure line: new points at vertices without a point of the surface, the elevation of
        /// existing points set to the value in the table; the triangles are rebuilt. One transaction.
        /// </summary>
        internal static (int Created, int Changed) CreateLine(Database database, LineDraft draft, KzmSettings settings)
        {
            if (draft.Vertices.Any(v => !v.Elevation.HasValue))
                throw new KzmException("У вершин " + string.Join(", ", draft.Vertices.Select((v, i) => (v, i)).Where(x => !x.v.Elevation.HasValue).Take(8).Select(x => (x.i + 1).ToString(CultureInfo.InvariantCulture))) +
                                       " нет отметки. Введите отметки или нажмите «Заполнить пустые по длине».");
            int created = 0, changed = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surface = EditServices.ReadSurface(database, transaction, draft.SurfaceId);
                var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                Layers.Ensure(database, transaction, surface.Layer);
                if (!Layers.IsUsable(database, transaction, surface.Layer, out string problem)) throw new KzmException(problem + ".");
                PointBlocks.EnsureRegApp(database, transaction, PointBlocks.AppName);
                var definition = PointBlocks.EnsureDefinition(database, transaction);
                if (!surface.Appearance.IsSet) surface.Appearance = EditServices.FromDrawing(database, transaction, surface.Appearance);
                var existing = PointBlocks.Read(database, transaction, settings, surface.Id);
                var byHandle = existing.ToDictionary(p => p.Handle, p => p, StringComparer.OrdinalIgnoreCase);
                int number = PointNumbering.Next(existing.Select(p => p.Number));
                var line = new StructureLine
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(draft.Name) ? "Линия" : draft.Name.Trim(),
                    Kind = StructureLineKind.Line,
                    Closed = draft.Closed,
                    SourceHandle = draft.SourceHandle,
                };
                foreach (var vertex in draft.Vertices)
                {
                    string handle;
                    if (vertex.ExistingHandle != null && byHandle.TryGetValue(vertex.ExistingHandle, out var point))
                    {
                        var reference = (BlockReference)transaction.GetObject(point.Id, OpenMode.ForRead);
                        if (!point.Elevation.IsValid || Math.Abs(point.Elevation.Value.Value - vertex.Elevation.Value) > 1e-9)
                        {
                            if (!Layers.IsUsable(database, transaction, reference.Layer, out problem)) throw new KzmException("Точка " + point.Handle + ": " + problem + ".");
                            EditServices.WriteElevation(transaction, reference, point.Data, vertex.Elevation.Value, settings.Decimals);
                            changed++;
                        }
                        handle = point.Handle;
                    }
                    else
                    {
                        var data = new PointData
                        {
                            SurfaceId = surface.Id, Elevation = vertex.Elevation.Value, Source = SourceKind.Line, XyMode = XyMode.Manual,
                            SourceHandle = draft.SourceHandle, Number = (number++).ToString(CultureInfo.InvariantCulture),
                        };
                        var id = PointBlocks.Create(transaction, space, definition, vertex.Position, surface.Layer, data, surface.Appearance, settings.Decimals);
                        handle = id.Handle.ToString();
                        created++;
                    }
                    if (line.PointHandles.Count == 0 || !string.Equals(line.PointHandles[line.PointHandles.Count - 1], handle, StringComparison.OrdinalIgnoreCase))
                        line.PointHandles.Add(handle);
                }
                if (line.PointHandles.Count < 2) throw new KzmException("У линии меньше двух различных точек.");
                KzmStore.WriteLine(database, transaction, surface.Id, line);
                surface.Revision++;
                KzmStore.WriteSurface(database, transaction, surface);
                EditServices.RefreshTriangles(database, transaction, surface, settings);
                transaction.Commit();
            }
            return (created, changed);
        }

        /// <summary>Removes a structure line (or a fixed diagonal); with <paramref name="erasePoints"/> also its points that no other line uses.</summary>
        internal static int DeleteLine(Database database, string surfaceId, string lineId, bool erasePoints, KzmSettings settings)
        {
            int erased = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surface = EditServices.ReadSurface(database, transaction, surfaceId);
                var lines = KzmStore.ReadLines(database, transaction, surfaceId);
                var line = lines.FirstOrDefault(l => l.Id == lineId) ?? throw new KzmException("Структурная линия не найдена.");
                KzmStore.DeleteLine(database, transaction, surfaceId, lineId);
                if (erasePoints)
                {
                    var used = new HashSet<string>(lines.Where(l => l.Id != lineId).SelectMany(l => l.PointHandles), StringComparer.OrdinalIgnoreCase);
                    foreach (var handle in line.PointHandles.Where(h => !used.Contains(h)))
                    {
                        var id = SurfaceModels.FromHandle(database, handle);
                        if (id.IsNull || !(transaction.GetObject(id, OpenMode.ForRead) is BlockReference reference)) continue;
                        var data = PointBlocks.Decode(reference);
                        if (data == null || data.Source != SourceKind.Line) continue; // only points the line itself created
                        if (!Layers.IsUsable(database, transaction, reference.Layer, out string problem)) throw new KzmException("Точка " + handle + ": " + problem + ".");
                        reference.UpgradeOpen();
                        reference.Erase();
                        erased++;
                    }
                }
                surface.Revision++;
                KzmStore.WriteSurface(database, transaction, surface);
                EditServices.RefreshTriangles(database, transaction, surface, settings);
                transaction.Commit();
            }
            return erased;
        }

        internal static List<ObjectId> PointsOf(Database database, StructureLine line) =>
            line.PointHandles.Select(h => SurfaceModels.FromHandle(database, h)).Where(id => !id.IsNull).ToList();

        // ---------- «поменять диагональ» ----------

        /// <summary>
        /// Задание п. 3: в паре треугольников указанная диагональ меняется на другую; новая диагональ
        /// запоминается как ребро-ограничение («Диагональ …»). Указание на заданную ранее диагональ
        /// снимает её. Повтор до Esc; весь сеанс — одна отмена.
        /// </summary>
        internal static int FlipDiagonals(Document document, string surfaceId, KzmSettings settings)
        {
            var database = document.Database;
            var editor = document.Editor;
            int done = 0;
            editor.WriteMessage("\nКЗМ: замена диагонали. Укажите точку внутри треугольника рядом с ребром, которое нужно поменять. Esc — закончить.");
            while (true)
            {
                SurfaceModel model;
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var surface = EditServices.ReadSurface(database, transaction, surfaceId);
                    model = SurfaceModels.Build(database, transaction, surface, settings);
                    if (!surface.ShowTriangles || surface.TrianglesFingerprint != model.Fingerprint)
                    {
                        surface.ShowTriangles = true;
                        SurfaceModels.DrawTriangles(database, transaction, model);
                        surface.TrianglesFingerprint = model.Fingerprint;
                        KzmStore.WriteSurface(database, transaction, surface);
                    }
                    transaction.Commit();
                }
                if (model.Tin.TriangleCount == 0) throw new KzmException("У поверхности нет треугольников.");
                var pick = editor.GetPoint(new PromptPointOptions("\nТочка у ребра, которое поменять (Esc — закончить): "));
                if (pick.Status != PromptStatus.OK) break;
                var world = pick.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                var p = new XY(world.X, world.Y);
                var tin = model.Tin;
                int t = tin.Locate(p);
                if (t < 0) { editor.WriteMessage("\nКЗМ: точка вне треугольников поверхности."); continue; }
                var (ta, tb, tc) = tin.Triangle(t);
                var edges = new[] { (ta, tb, tc), (tb, tc, ta), (tc, ta, tb) };
                var (e1, e2, apex) = edges.OrderBy(e => Geometry.DistanceToSegment(p, tin.Point(e.Item1), tin.Point(e.Item2))).First();
                int other = -1;
                for (int u = 0; u < tin.TriangleCount && other < 0; u++)
                {
                    if (u == t) continue;
                    var (ua, ub, uc) = tin.Triangle(u);
                    var set = new[] { ua, ub, uc };
                    if (set.Contains(e1) && set.Contains(e2)) other = set.First(i => i != e1 && i != e2);
                }
                if (other < 0) { editor.WriteMessage("\nКЗМ: ребро на краю поверхности — второго треугольника нет."); continue; }
                string h1 = tin.Id(e1), h2 = tin.Id(e2), ha = tin.Id(apex), hb = tin.Id(other);
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var surface = EditServices.ReadSurface(database, transaction, surfaceId);
                    var lines = KzmStore.ReadLines(database, transaction, surfaceId);
                    if (tin.IsConstrained(e1, e2))
                    {
                        var flip = lines.FirstOrDefault(l => l.Kind == StructureLineKind.Flip && l.PointHandles.Count == 2 &&
                                                             l.PointHandles.Contains(h1, StringComparer.OrdinalIgnoreCase) &&
                                                             l.PointHandles.Contains(h2, StringComparer.OrdinalIgnoreCase));
                        if (flip == null)
                        {
                            editor.WriteMessage("\nКЗМ: это ребро структурной линии — его диагональ не меняется. Удалите линию, если нужно.");
                            transaction.Commit();
                            continue;
                        }
                        KzmStore.DeleteLine(database, transaction, surfaceId, flip.Id);
                        editor.WriteMessage("\nКЗМ: заданная ранее диагональ снята — ребро снова по правилу Делоне.");
                    }
                    else
                    {
                        XY a = tin.Point(e1), b = tin.Point(e2), c = tin.Point(apex), d = tin.Point(other);
                        bool crosses = Geometry.Orient(a, b, c) * Geometry.Orient(a, b, d) < 0 && Geometry.Orient(c, d, a) * Geometry.Orient(c, d, b) < 0;
                        if (!crosses)
                        {
                            editor.WriteMessage("\nКЗМ: четырёхугольник из двух треугольников невыпуклый — диагональ поменять нельзя.");
                            transaction.Commit();
                            continue;
                        }
                        var line = new StructureLine
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Kind = StructureLineKind.Flip,
                            Name = "Диагональ " + Label(model, apex) + " — " + Label(model, other),
                        };
                        line.PointHandles.Add(ha);
                        line.PointHandles.Add(hb);
                        KzmStore.WriteLine(database, transaction, surfaceId, line);
                        editor.WriteMessage("\nКЗМ: диагональ " + Label(model, e1) + " — " + Label(model, e2) + " заменена на " + line.Name.Substring(10) + ".");
                    }
                    surface.Revision++;
                    KzmStore.WriteSurface(database, transaction, surface);
                    transaction.Commit();
                }
                done++;
            }
            if (done > 0) editor.WriteMessage("\nКЗМ: изменено диагоналей: " + done + ". Отменить всё — U.");
            return done;
        }

        private static string Label(SurfaceModel model, int index)
        {
            var point = model.Used[index];
            return "т." + (string.IsNullOrEmpty(point.Number) ? point.Handle : point.Number);
        }

        // ---------- planes ----------

        /// <summary>
        /// A point with an elevation for a plane: the elevation of a module point standing there (any surface,
        /// within 0,01 m — use the «Insertion» object snap), otherwise typed in the command line.
        /// </summary>
        internal static (XY Point, double Z)? PickElevatedPoint(Document document, string prompt, KzmSettings settings)
        {
            var editor = document.Editor;
            var pick = editor.GetPoint(new PromptPointOptions("\n" + prompt + ": "));
            if (pick.Status != PromptStatus.OK) return null;
            var world = pick.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            var p = new XY(world.X, world.Y);
            List<SurfacePoint> points;
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                points = PointBlocks.Read(document.Database, transaction, settings);
                transaction.Commit();
            }
            var found = new NearestPoints(points).Within(p, KzmSettings.DuplicateTolerance);
            if (found != null && found.Elevation.IsValid)
            {
                editor.WriteMessage("\nКЗМ: взята отметка точки " + (found.Number ?? found.Handle) + ": " + ElevationFormat.Format(found.Elevation.Value.Value, settings.Decimals) + ".");
                return (found.Position, found.Elevation.Value.Value);
            }
            while (true)
            {
                var answer = editor.GetString(new PromptStringOptions("\nОтметка в этой точке: ") { AllowSpaces = false });
                if (answer.Status != PromptStatus.OK) return null;
                if (ElevationFormat.TryParse(answer.StringResult, out double z)) return (p, z);
                editor.WriteMessage("\nВведите отметку числом, например 145,32.");
            }
        }

        /// <summary>Points on the plane where the user points (повтор до Esc). One UNDO for the session.</summary>
        internal static int CreatePlanePoints(Document document, string surfaceId, PlaneDefinition plane, KzmSettings settings)
        {
            var database = document.Database;
            var editor = document.Editor;
            int created = 0;
            editor.WriteMessage("\nКЗМ: точки на плоскости (" + plane.Describe(settings.Decimals) + "). Esc — закончить.");
            while (true)
            {
                var pick = editor.GetPoint(new PromptPointOptions("\nПоложение точки на плоскости (Esc — закончить): "));
                if (pick.Status != PromptStatus.OK) break;
                var world = pick.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                var p = new XY(world.X, world.Y);
                double z = plane.ZAt(p);
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var surface = EditServices.ReadSurface(database, transaction, surfaceId);
                    PlacePoint(database, transaction, surface, p, z, settings, null);
                    transaction.Commit();
                }
                editor.WriteMessage("\nКЗМ: точка " + p + ", отметка " + ElevationFormat.Format(z, settings.Decimals) + ".");
                created++;
            }
            if (created > 0)
            {
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var surface = EditServices.ReadSurface(database, transaction, surfaceId);
                    surface.Revision++;
                    KzmStore.WriteSurface(database, transaction, surface);
                    EditServices.RefreshTriangles(database, transaction, surface, settings);
                    transaction.Commit();
                }
                editor.WriteMessage("\nКЗМ: создано точек на плоскости: " + created + ". Отменить все — U.");
            }
            return created;
        }

        /// <summary>
        /// Points on the plane in the vertices of a polyline; a point of the surface already standing at a vertex
        /// gets the elevation of the plane. With <paramref name="makeLine"/> the polyline becomes a structure line,
        /// so the platform stays exactly planar inside its contour.
        /// </summary>
        internal static (int Created, int Changed) PlanePointsOnPolyline(Database database, string surfaceId, PlaneDefinition plane,
            ObjectId polylineId, bool makeLine, KzmSettings settings)
        {
            LineDraft draft;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                draft = ReadPolyline(database, transaction, (Entity)transaction.GetObject(polylineId, OpenMode.ForRead), surfaceId, settings);
                transaction.Commit();
            }
            foreach (var vertex in draft.Vertices)
            {
                vertex.Elevation = plane.ZAt(vertex.Position);
                vertex.Origin = "плоскость";
            }
            draft.Name = "Контур плоскости";
            if (makeLine) return CreateLine(database, draft, settings);
            int created = 0, changed = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surface = EditServices.ReadSurface(database, transaction, surfaceId);
                foreach (var vertex in draft.Vertices)
                {
                    if (PlacePoint(database, transaction, surface, vertex.Position, vertex.Elevation.Value, settings, vertex.ExistingHandle)) created++;
                    else changed++;
                }
                surface.Revision++;
                KzmStore.WriteSurface(database, transaction, surface);
                EditServices.RefreshTriangles(database, transaction, surface, settings);
                transaction.Commit();
            }
            return (created, changed);
        }

        /// <summary>Creates a plane point (true) or sets the elevation of the existing point (false).</summary>
        private static bool PlacePoint(Database database, Transaction transaction, SurfaceInfo surface, XY p, double z, KzmSettings settings, string existingHandle)
        {
            if (existingHandle != null)
            {
                var id = SurfaceModels.FromHandle(database, existingHandle);
                if (!id.IsNull && transaction.GetObject(id, OpenMode.ForRead) is BlockReference reference && PointBlocks.Decode(reference) is PointData data)
                {
                    if (!Layers.IsUsable(database, transaction, reference.Layer, out string problem)) throw new KzmException("Точка " + existingHandle + ": " + problem + ".");
                    EditServices.WriteElevation(transaction, reference, data, z, settings.Decimals);
                    return false;
                }
            }
            var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
            Layers.Ensure(database, transaction, surface.Layer);
            if (!Layers.IsUsable(database, transaction, surface.Layer, out string layerProblem)) throw new KzmException(layerProblem + ".");
            PointBlocks.EnsureRegApp(database, transaction, PointBlocks.AppName);
            var definition = PointBlocks.EnsureDefinition(database, transaction);
            if (!surface.Appearance.IsSet)
            {
                surface.Appearance = EditServices.FromDrawing(database, transaction, surface.Appearance);
                KzmStore.WriteSurface(database, transaction, surface);
            }
            var existing = PointBlocks.Read(database, transaction, settings, surface.Id);
            var pointData = new PointData
            {
                SurfaceId = surface.Id, Elevation = z, Source = SourceKind.Plane, XyMode = XyMode.Manual,
                Number = PointNumbering.Next(existing.Select(q => q.Number)).ToString(CultureInfo.InvariantCulture),
            };
            PointBlocks.Create(transaction, space, definition, p, surface.Layer, pointData, surface.Appearance, settings.Decimals);
            return true;
        }
    }

    /// <summary>Nearest module point within a small distance (grid of 1 m cells).</summary>
    internal sealed class NearestPoints
    {
        private readonly Dictionary<(long, long), List<SurfacePoint>> cells = new Dictionary<(long, long), List<SurfacePoint>>();

        internal NearestPoints(IEnumerable<SurfacePoint> points)
        {
            foreach (var point in points)
            {
                var key = Key(point.Position);
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<SurfacePoint>();
                list.Add(point);
            }
        }

        private static (long, long) Key(XY p) => ((long)Math.Floor(p.X), (long)Math.Floor(p.Y));

        internal SurfacePoint Within(XY p, double distance)
        {
            var (cx, cy) = Key(p);
            SurfacePoint best = null;
            double bestDistance = distance;
            for (long x = cx - 1; x <= cx + 1; x++)
            for (long y = cy - 1; y <= cy + 1; y++)
            {
                if (!cells.TryGetValue((x, y), out var list)) continue;
                foreach (var point in list)
                {
                    double d = point.Position.DistanceTo(p);
                    if (d <= bestDistance) { best = point; bestDistance = d; }
                }
            }
            return best;
        }
    }
}
