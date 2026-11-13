using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    /// <summary>What the triangulation of a surface depends on (to tell that triangles or cartograms are outdated).</summary>
    internal sealed class SurfaceSignature
    {
        internal string Points { get; set; }
        internal string Outline { get; set; }
        /// <summary>Empty when the surface has no structure lines.</summary>
        internal string Lines { get; set; } = string.Empty;
        internal bool HasBoundary { get; set; }

        /// <summary>
        /// Same text as in version 0.4 for a surface without structure lines and boundary, so that
        /// triangles drawn by 0.4 are not reported outdated. With a boundary the mark «mask» is added:
        /// version 0.5 clips the triangles by the boundary exactly (0.4 dropped whole triangles).
        /// </summary>
        internal string ForTriangles() =>
            Points + "|" + Outline + (Lines.Length > 0 ? "|L:" + Lines : string.Empty) + (HasBoundary ? "|mask" : string.Empty);

        internal void AddTo(Fingerprint fingerprint)
        {
            fingerprint.Add(Points);
            fingerprint.Add(Outline);
            if (Lines.Length > 0) fingerprint.Add("lines:" + Lines);
            if (HasBoundary) fingerprint.Add("mask:exact");
        }
    }

    /// <summary>Triangulated surface built from the point blocks of one surface.</summary>
    internal sealed class SurfaceModel
    {
        internal SurfaceInfo Surface { get; set; }
        internal List<SurfacePoint> Points { get; set; }
        internal List<SurfacePoint> Used { get; set; }
        internal List<StructureLine> Lines { get; set; } = new List<StructureLine>();
        internal Tin Tin { get; set; }
        internal SurfaceSignature Signature { get; set; }
        internal string Fingerprint => Signature.ForTriangles();
        internal List<string> Problems { get; } = new List<string>();
    }

    /// <summary>
    /// Триангуляция поверхности: точки с читаемой отметкой (кроме исключённых) → TIN Делоне с
    /// рёбрами структурных линий; внешняя граница (если задана) — точная маска. Треугольники
    /// выводятся одним блоком на слой КЗМ_ТРЕУГ (рёбра структурных линий — красным).
    /// </summary>
    internal static class SurfaceModels
    {
        internal const string TinApp = "SH0MEM0_KZM_TIN";

        internal static ObjectId FromHandle(Database database, string handle)
        {
            if (string.IsNullOrEmpty(handle)) return ObjectId.Null;
            if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)) return ObjectId.Null;
            try
            {
                var id = database.GetObjectId(false, new Handle(value), 0);
                return id.IsValid && !id.IsErased ? id : ObjectId.Null;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return ObjectId.Null;
            }
        }

        internal static List<XY> ReadOutline(Database database, Transaction transaction, string handle, string what)
        {
            var id = FromHandle(database, handle);
            if (id.IsNull) throw new KzmException(what + ": полилиния не найдена в чертеже (удалена?). Укажите её заново.");
            return Sources.Outline((Entity)transaction.GetObject(id, OpenMode.ForRead), transaction);
        }

        internal static string OutlineFingerprint(Database database, Transaction transaction, string handle)
        {
            var id = FromHandle(database, handle);
            if (id.IsNull) return "none";
            var fingerprint = new Fingerprint();
            try
            {
                foreach (var p in Sources.Outline((Entity)transaction.GetObject(id, OpenMode.ForRead), transaction)) fingerprint.Add(p.X).Add(p.Y);
            }
            catch (KzmException exception)
            {
                fingerprint.Add(exception.Message);
            }
            return fingerprint.ToString();
        }

        internal static SurfaceSignature SignatureOf(Database database, Transaction transaction, SurfaceInfo surface,
            IEnumerable<SurfacePoint> points, IEnumerable<StructureLine> lines) => new SurfaceSignature
        {
            Points = PointBlocks.FingerprintOf(points),
            Outline = OutlineFingerprint(database, transaction, surface.BoundaryHandle),
            Lines = StructureLine.FingerprintOf(lines),
            HasBoundary = !string.IsNullOrEmpty(surface.BoundaryHandle),
        };

        internal static SurfaceModel Build(Database database, Transaction transaction, SurfaceInfo surface, KzmSettings settings)
        {
            var model = new SurfaceModel
            {
                Surface = surface,
                Points = PointBlocks.Read(database, transaction, settings, surface.Id),
                Lines = KzmStore.ReadLines(database, transaction, surface.Id),
            };
            int broken = model.Points.Count(p => !p.Elevation.IsValid);
            if (broken > 0) model.Problems.Add("точек с нечитаемой отметкой: " + broken + " — в триангуляцию не вошли");
            int excluded = model.Points.Count(p => p.Data.Excluded);
            if (excluded > 0) model.Problems.Add("исключено из триангуляции точек: " + excluded);
            model.Used = model.Points.Where(p => p.Elevation.IsValid && !p.Data.Excluded).ToList();
            List<XY> boundary = null;
            if (!string.IsNullOrEmpty(surface.BoundaryHandle))
                boundary = ReadOutline(database, transaction, surface.BoundaryHandle, "Граница поверхности «" + surface.Name + "»");

            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < model.Used.Count; i++) index[model.Used[i].Handle] = i;
            var constraints = new List<(int, int)>();
            foreach (var line in model.Lines)
            {
                int missing = line.PointHandles.Count(h => !index.ContainsKey(h));
                if (missing > 0)
                    model.Problems.Add((line.Kind == StructureLineKind.Flip ? "диагональ «" : "структурная линия «") + line.Name + "»: точек нет или не в расчёте: " +
                                       missing + " — линия проходит по оставшимся");
                foreach (var (a, b) in line.Segments(index.ContainsKey)) constraints.Add((index[a], index[b]));
            }
            string Describe(int i) => "т." + (string.IsNullOrEmpty(model.Used[i].Number) ? model.Used[i].Handle : model.Used[i].Number);
            model.Tin = Tin.Build(model.Used.Select(p => p.Position).ToList(), model.Used.Select(p => p.Elevation.Value.Value).ToList(),
                model.Used.Select(p => p.Handle).ToList(), boundary, constraints, Describe);
            if (model.Tin.Duplicates.Count > 0)
                model.Problems.Add("точек в одном месте с другой или на одной прямой с соседними (пропущены): " + model.Tin.Duplicates.Count);
            if (model.Tin.TriangleCount == 0)
                model.Problems.Add("треугольников нет: нужно не меньше 3 точек не на одной прямой");
            model.Problems.AddRange(model.Tin.ConstraintProblems);
            model.Signature = SignatureOf(database, transaction, surface, model.Points, model.Lines);
            return model;
        }

        /// <summary>Erases the triangles of the surface (the block of version 0.5 and the separate lines of version 0.4).</summary>
        internal static int EraseTriangles(Database database, Transaction transaction, SurfaceInfo surface)
        {
            int count = 0;
            var id = FromHandle(database, surface.TrianglesBlockHandle);
            if (!id.IsNull && transaction.GetObject(id, OpenMode.ForRead) is BlockReference reference)
            {
                if (!Layers.IsUsable(database, transaction, reference.Layer, out string problem))
                    throw new KzmException("треугольники поверхности: " + problem + ".");
                var definitionId = reference.BlockTableRecord;
                reference.UpgradeOpen();
                reference.Erase();
                count++;
                var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
                if (definition.IsAnonymous && definition.GetBlockReferenceIds(true, false).Cast<ObjectId>().All(r => r.IsErased))
                {
                    definition.UpgradeOpen();
                    definition.Erase();
                }
            }
            surface.TrianglesBlockHandle = null;
            // Version 0.4 drew every edge as a separate LINE: looked for only if such triangles may exist.
            if (id.IsNull && surface.TrianglesFingerprint != null) count += EraseLegacyLines(database, transaction, surface.Id);
            return count;
        }

        private static int EraseLegacyLines(Database database, Transaction transaction, string surfaceId)
        {
            int count = 0;
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                if (!block.IsLayout) continue;
                foreach (ObjectId id in block)
                {
                    if (id.ObjectClass.DxfName != "LINE") continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    using (var xdata = entity.GetXDataForApplication(TinApp))
                    {
                        if (xdata == null) continue;
                        var values = xdata.AsArray();
                        if (values.Length < 2 || !Equals(values[1].Value, surfaceId)) continue;
                    }
                    if (!Layers.IsUsable(database, transaction, entity.Layer, out string problem))
                        throw new KzmException("треугольники поверхности: " + problem + ".");
                    entity.UpgradeOpen();
                    entity.Erase();
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Draws the edges of the TIN (clipped by the boundary) into one anonymous block and inserts it
        /// once in model space (layer КЗМ_ТРЕУГ). Edges of structure lines are red. Object snaps work on
        /// the edges inside the block. The handle of the insert is kept in the surface record.
        /// </summary>
        internal static int DrawTriangles(Database database, Transaction transaction, SurfaceModel model)
        {
            EraseTriangles(database, transaction, model.Surface);
            Layers.Ensure(database, transaction, Layers.Triangles, 8);
            PointBlocks.EnsureRegApp(database, transaction, TinApp);
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite);
            var definition = new BlockTableRecord
            {
                Name = "*U",
                Origin = Point3d.Origin,
                Comments = "Треугольники поверхности «" + model.Surface.Name + "» (модуль КЗМ).",
            };
            var definitionId = blocks.Add(definition);
            transaction.AddNewlyCreatedDBObject(definition, true);
            int count = 0;
            var byBlock = Color.FromColorIndex(ColorMethod.ByBlock, 0);
            var red = Color.FromColorIndex(ColorMethod.ByAci, 1);
            var edges = model.Tin.DisplayEdges().ToList();
            using (var meter = Progress.Start("КЗМ: треугольники «" + model.Surface.Name + "»", edges.Count))
            {
                foreach (var (a, b, constrained) in edges)
                {
                    var line = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0)) { Layer = "0", Color = constrained ? red : byBlock };
                    definition.AppendEntity(line);
                    transaction.AddNewlyCreatedDBObject(line, true);
                    count++;
                    meter.Step();
                }
            }
            var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
            var reference = new BlockReference(Point3d.Origin, definitionId)
            {
                Layer = Layers.Triangles,
                XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, TinApp),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, model.Surface.Id)),
            };
            space.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
            // Behind the points and texts of the drawing.
            try
            {
                var order = (DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
                order.MoveToBottom(new ObjectIdCollection(new[] { reference.ObjectId }));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { /* draw order is cosmetic */ }
            model.Surface.TrianglesBlockHandle = reference.Handle.ToString();
            return count;
        }
    }

    /// <summary>AutoCAD progress bar for long loops; a no-op when it cannot be shown.</summary>
    internal sealed class Progress : IDisposable
    {
        private readonly Autodesk.AutoCAD.Runtime.ProgressMeter meter;
        private readonly int every;
        private int done;

        private Progress(string title, int count)
        {
            if (count < 2000) return;
            every = Math.Max(1, count / 200);
            try
            {
                meter = new Autodesk.AutoCAD.Runtime.ProgressMeter();
                meter.SetLimit(count / every + 1);
                meter.Start(title);
            }
            catch (System.Exception)
            {
                meter = null;
            }
        }

        internal static Progress Start(string title, int count) => new Progress(title, count);

        internal void Step()
        {
            if (meter == null || ++done % every != 0) return;
            try { meter.MeterProgress(); }
            catch (System.Exception) { /* cosmetic */ }
        }

        public void Dispose()
        {
            if (meter == null) return;
            try
            {
                meter.Stop();
                meter.Dispose();
            }
            catch (System.Exception) { /* cosmetic */ }
        }
    }
}
