using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    internal sealed class CartogramComputation
    {
        internal CartogramInfo Info { get; set; }
        internal SurfaceModel Fact { get; set; }
        internal SurfaceModel Project { get; set; }
        internal List<XY> Boundary { get; set; }
        internal GridDefinition Grid { get; set; }
        internal CartogramResult Result { get; set; }
        /// <summary>Контрольный расчёт по наложению триангуляций (null — выключен или не выполнен).</summary>
        internal ControlVolumeResult Control { get; set; }
        internal string Fingerprint { get; set; }
        internal List<string> Problems { get; } = new List<string>();
        /// <summary>Places worth a look: squares and nodes without surface data.</summary>
        internal List<(string Text, XY At)> Marks { get; } = new List<(string, XY)>();
        /// <summary>Labels put back where the user had moved them (after a rebuild).</summary>
        internal int MovedLabels { get; set; }
        /// <summary>Elevations typed on the cartogram and written into the surfaces before this rebuild.</summary>
        internal List<string> Applied { get; } = new List<string>();
    }

    /// <summary>Where the user moved the labels and the table of the cartogram (kept on rebuild, задание п. 7).</summary>
    internal sealed class MovedLabels
    {
        /// <summary>Node or boundary point key → displacement of the working, project and fact labels.</summary>
        internal Dictionary<string, Vector3d[]> Nodes { get; } = new Dictionary<string, Vector3d[]>(StringComparer.Ordinal);
        /// <summary>Volume labels by key.</summary>
        internal Dictionary<string, Vector3d> Texts { get; } = new Dictionary<string, Vector3d>(StringComparer.Ordinal);
        internal Vector3d? Table { get; set; }
    }

    /// <summary>An elevation typed into a label of the cartogram (задание: правка отметок прямо на картограмме).</summary>
    internal sealed class LabelEdit
    {
        internal XY At { get; set; }
        /// <summary>true — проектная, false — фактическая.</summary>
        internal bool Project { get; set; }
        internal double Value { get; set; }
        internal double Before { get; set; }
    }

    /// <summary>
    /// Картограмма: расчёт по TIN фактической и проектной поверхностей и оформление на чертеже
    /// по СТБ 2073-2010 (п. 3.12, 7.1–7.3, рис. 8, пример Б.4). Все построенные объекты
    /// помечены XData «SH0MEM0_KZM_CART» с id картограммы; их список хранится в чертеже, поэтому
    /// пересчёт не перебирает весь чертёж. Ведомость объёмов при пересчёте не стирается:
    /// в ней обновляется только строка «Грунт планировки территории».
    /// </summary>
    internal static class CartogramService
    {
        internal const string App = "SH0MEM0_KZM_CART";
        private const string StatementMark = "STATEMENT";
        internal const string LabelMark = "L";
        internal const string NodeMark = "N";
        internal const string TableMark = "T";
        internal const string GridMark = "G";
        /// <summary>Boundary polyline drawn by points for this cartogram (not part of the drawing that is rebuilt).</summary>
        internal const string BoundaryMark = "BOUNDARY";
        private const string StatementsSuffix = ".statements";
        internal const string WorkingTag = "РАБОЧАЯ";
        internal const string ProjectTag = "ПРОЕКТНАЯ";
        internal const string FactTag = "ФАКТИЧЕСКАЯ";
        internal const string PlanningRow = "Грунт планировки территории";

        internal static GridDefinition GridFor(CartogramInfo c, IReadOnlyList<XY> boundary)
        {
            // The grid is parallel to the drawing axes (the user rotates the finished drawing if needed).
            const double angle = 0;
            if (c.OriginX.HasValue && c.OriginY.HasValue) return new GridDefinition(c.Step, new XY(c.OriginX.Value, c.OriginY.Value), angle);
            // Auto: the grid lines are multiples of the step in the rotated world axes.
            var provisional = new GridDefinition(c.Step, new XY(0, 0), angle);
            var local = boundary.Select(provisional.ToLocal).ToList();
            double u = Math.Floor(local.Min(p => p.U) / c.Step) * c.Step;
            double v = Math.Floor(local.Min(p => p.V) / c.Step) * c.Step;
            return new GridDefinition(c.Step, provisional.ToWorld(u, v), angle);
        }

        /// <summary>What the cartogram depends on: points, structure lines and boundaries of both surfaces, its boundary and parameters.</summary>
        internal static string FingerprintOf(Database database, Transaction transaction, CartogramInfo c, IReadOnlyList<SurfaceInfo> surfaces,
            Func<string, SurfaceSignature> signatureOf)
        {
            var fingerprint = new Fingerprint();
            foreach (var id in new[] { c.FactSurfaceId, c.ProjectSurfaceId })
            {
                var surface = surfaces.FirstOrDefault(s => s.Id == id);
                fingerprint.Add(id ?? "-");
                if (surface == null) continue;
                signatureOf(id).AddTo(fingerprint);
            }
            fingerprint.Add(SurfaceModels.OutlineFingerprint(database, transaction, c.BoundaryHandle));
            foreach (var pair in CartogramInfo.FingerprintPairs(c)) fingerprint.Add(pair.Key).Add(pair.Value);
            return fingerprint.ToString();
        }

        /// <summary>Calculation only (the drawing is not changed): used by «Проверить», the export and the build.</summary>
        internal static CartogramComputation Compute(Database database, Transaction transaction, CartogramInfo c, KzmSettings settings)
        {
            var problems = c.Validate();
            if (problems.Count > 0) throw new KzmException("Картограмма «" + c.Name + "»: " + string.Join("; ", problems) + ".");
            var surfaces = KzmStore.ReadSurfaces(database, transaction);
            var fact = surfaces.FirstOrDefault(s => s.Id == c.FactSurfaceId) ?? throw new KzmException("Фактическая поверхность картограммы удалена.");
            var project = surfaces.FirstOrDefault(s => s.Id == c.ProjectSurfaceId) ?? throw new KzmException("Проектная поверхность картограммы удалена.");
            var computation = new CartogramComputation
            {
                Info = c,
                Fact = SurfaceModels.Build(database, transaction, fact, settings),
                Project = SurfaceModels.Build(database, transaction, project, settings),
                Boundary = SurfaceModels.ReadOutline(database, transaction, c.BoundaryHandle, "Граница картограммы"),
            };
            if (computation.Fact.Tin.TriangleCount == 0) throw new KzmException("Фактическая поверхность «" + fact.Name + "» не триангулируется: мало точек.");
            if (computation.Project.Tin.TriangleCount == 0) throw new KzmException("Проектная поверхность «" + project.Name + "» не триангулируется: мало точек.");
            computation.Problems.AddRange(computation.Fact.Problems.Select(p => "«" + fact.Name + "»: " + p));
            computation.Problems.AddRange(computation.Project.Problems.Select(p => "«" + project.Name + "»: " + p));
            computation.Grid = GridFor(c, computation.Boundary);
            var result = CartogramCalculator.Compute(computation.Boundary, computation.Fact.Tin.ZAt, computation.Project.Tin.ZAt, computation.Grid);
            computation.Result = result;
            computation.Problems.AddRange(result.Problems);
            if (c.ControlCheck)
            {
                try
                {
                    computation.Control = ControlVolume.Compute(computation.Boundary, computation.Fact.Tin, computation.Project.Tin);
                    if (computation.Control.MissingArea > 0)
                        computation.Problems.Add("контрольный расчёт: на площади " + ElevationFormat.Format(computation.Control.MissingArea, 1) +
                                                 " м² внутри границы нет данных хотя бы одной поверхности");
                }
                catch (KzmException exception)
                {
                    computation.Problems.Add(exception.Message);
                }
            }
            foreach (var square in result.Squares.Where(s => !s.Computed))
                computation.Marks.Add(("квадрат у " + CartogramExport.Center(result, square.I, square.J) + ": " + square.Problem, square.LabelPoint));
            foreach (var node in result.Nodes.Values.Where(n => n.Used && n.Inside && !n.Working.HasValue))
                computation.Marks.Add(("узел без данных " + node.Position, node.Position));
            foreach (var point in result.BoundaryPoints.Where(b => !b.Working.HasValue))
                computation.Marks.Add(("точка границы без данных " + point.Position, point.Position));
            computation.Fingerprint = FingerprintOf(database, transaction, c, surfaces,
                id => id == fact.Id ? computation.Fact.Signature : computation.Project.Signature);
            return computation;
        }

        /// <summary>Several lines for the panel and the command line.</summary>
        internal static List<string> Summary(CartogramComputation computation)
        {
            var c = computation.Info;
            var r = computation.Result;
            var lines = new List<string>();
            double balance = r.TotalFill - r.TotalCut;
            int dec = c.VolumeDecimals;
            lines.Add("Насыпь " + Volume(r.TotalFill, dec, true) + " м³, выемка " + Volume(r.TotalCut, dec, false) + " м³; баланс (насыпь − выемка) " +
                      (Math.Abs(balance) < 0.5 * Math.Pow(10, -dec) ? "0 м³" : ElevationFormat.FormatSigned(balance, dec) + " м³ — " + (balance > 0 ? "недостаток грунта" : "избыток грунта")) + ".");
            lines.Add("Квадратов " + r.Squares.Count + " (полных " + r.Squares.Count(s => s.Full) + ", неполных " + r.Squares.Count(s => !s.Full) +
                      "); площадь " + ElevationFormat.Format(r.ComputedArea, 1) + " м² из " + ElevationFormat.Format(r.BoundaryArea, 1) + " м².");
            if (computation.Control != null)
            {
                var fill = ControlVolumeResult.Difference(r.TotalFill, computation.Control.Fill);
                var cut = ControlVolumeResult.Difference(r.TotalCut, computation.Control.Cut);
                string Percent(double? value) => value.HasValue ? " (" + ElevationFormat.Format(value.Value, 2) + " %)" : string.Empty;
                lines.Add("Контрольный расчёт (наложение поверхностей): насыпь " + ElevationFormat.Format(computation.Control.Fill, 1) + ", выемка " +
                          ElevationFormat.Format(computation.Control.Cut, 1) + " м³; расхождение насыпи " + ElevationFormat.FormatSigned(fill.Cubic, 1) + " м³" +
                          Percent(fill.Percent) + ", выемки " + ElevationFormat.FormatSigned(cut.Cubic, 1) + " м³" + Percent(cut.Percent) + ".");
            }
            lines.Add(r.Balanced ? "Проверка: Σ по столбцам = Σ по рядам = итог — сходится." : "ВНИМАНИЕ: суммы по столбцам и рядам не сходятся.");
            if (computation.MovedLabels > 0) lines.Add("Подписей, поставленных на место, сдвинутое вручную: " + computation.MovedLabels + ".");
            lines.AddRange(computation.Applied);
            return lines;
        }

        private static bool IsTagged(Entity entity, string cartogramId, out bool statement, out TypedValue[] values)
        {
            statement = false;
            values = null;
            using (var xdata = entity.GetXDataForApplication(App))
            {
                if (xdata == null) return false;
                values = xdata.AsArray();
                if (values.Length < 2 || !Equals(values[1].Value, cartogramId)) return false;
                statement = values.Length > 2 && Equals(values[2].Value, StatementMark);
                return true;
            }
        }

        /// <summary>Erases the drawing of the cartogram; the statement tables are kept (they hold the designer's rows).</summary>
        internal static int Erase(Database database, Transaction transaction, string cartogramId)
        {
            int count = 0;
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId);
            if (tracked != null)
            {
                var ours = new HashSet<string>(StringComparer.Ordinal) { cartogramId };
                foreach (var id in tracked)
                {
                    if (!id.IsValid || id.IsErased || !(transaction.GetObject(id, OpenMode.ForRead) is Entity entity) || entity is Table) continue;
                    bool tagged = IsTagged(entity, cartogramId, out bool statement, out var values);
                    if (statement || (values != null && values.Length > 2 && Equals(values[2].Value, BoundaryMark))) continue;
                    if (!tagged && !CheckMarks.Targets(entity, ours)) continue;
                    var definitionId = entity is BlockReference reference ? reference.BlockTableRecord : ObjectId.Null;
                    EraseEntity(database, transaction, entity);
                    EraseAnonymousDefinition(transaction, definitionId);
                    count++;
                }
                KzmStore.DeleteCartogramObjects(database, transaction, cartogramId);
                return count;
            }
            // Drawn by version 0.4: look through the drawing once.
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                if (!block.IsLayout) continue;
                foreach (ObjectId id in block)
                {
                    string type = id.ObjectClass.DxfName;
                    if (type != "LINE" && type != "TEXT" && type != "LWPOLYLINE" && type != "HATCH") continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (!IsTagged(entity, cartogramId, out bool statement, out _) || statement) continue;
                    EraseEntity(database, transaction, entity);
                    count++;
                }
            }
            count += CheckMarks.Clear(database, transaction, new HashSet<string>(StringComparer.Ordinal) { cartogramId });
            return count;
        }

        /// <summary>The grid and the table are anonymous blocks: the definition goes with its last reference.</summary>
        internal static void EraseAnonymousDefinition(Transaction transaction, ObjectId definitionId)
        {
            if (definitionId.IsNull || definitionId.IsErased) return;
            var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
            if (!definition.IsAnonymous || !definition.GetBlockReferenceIds(true, false).Cast<ObjectId>().All(r => r.IsErased)) return;
            definition.UpgradeOpen();
            definition.Erase();
        }

        private static void EraseEntity(Database database, Transaction transaction, Entity entity)
        {
            if (!Layers.IsUsable(database, transaction, entity.Layer, out string problem))
                throw new KzmException("картограмма: " + problem + ". Разблокируйте слой.");
            entity.UpgradeOpen();
            entity.Erase();
        }

        private static bool Straight(double rotation) => Math.Abs(Math.IEEERemainder(rotation, 2 * Math.PI)) < 1e-6;

        /// <summary>
        /// Labels and the table the user moved: displacement from where the module put them. Labels that were
        /// rotated (the whole cartogram turned by the user) do not count as moved.
        /// </summary>
        private static MovedLabels ReadMoved(Database database, Transaction transaction, string cartogramId)
        {
            var result = new MovedLabels();
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId);
            if (tracked == null) return result;
            foreach (var id in tracked)
            {
                if (!id.IsValid || id.IsErased || !(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) continue;
                if (!IsTagged(entity, cartogramId, out _, out var values) || values.Length < 3) continue;
                string mark = values[2].Value as string;
                if (entity is DBText text && mark == LabelMark && values.Length >= 5 && values[3].Value is string key && values[4].Value is Point3d placed)
                {
                    if (!Straight(text.Rotation)) continue;
                    var offset = Sources.TextAnchor(text) - placed;
                    if (offset.Length > 1e-6) result.Texts[key] = offset;
                }
                else if (entity is BlockReference block && mark == NodeMark && values.Length >= 11 && values[3].Value is string nodeKey)
                {
                    var attributes = Sources.Attributes(block, transaction);
                    var offsets = new Vector3d[3];
                    bool any = false;
                    string[] tags = { WorkingTag, ProjectTag, FactTag };
                    for (int k = 0; k < 3; k++)
                    {
                        if (!attributes.TryGetValue(tags[k], out var attribute) || !(values[8 + k].Value is Point3d put)) continue;
                        if (!Straight(attribute.Rotation)) { any = false; break; }
                        offsets[k] = Sources.TextAnchor(attribute) - put;
                        if (offsets[k].Length > 1e-6) any = true;
                    }
                    if (any) result.Nodes[nodeKey] = offsets;
                }
                else if (entity is BlockReference table && mark == TableMark)
                {
                    var offset = table.Position - Point3d.Origin;
                    if (offset.Length > 1e-6 && Straight(table.Rotation)) result.Table = offset;
                }
            }
            return result;
        }

        /// <summary>
        /// Elevations the user typed into the labels of the cartogram (Быстрые свойства / двойной щелчок):
        /// проектная и фактическая — для записи в поверхности; правка рабочей не принимается (она всегда
        /// Hпр − Hф) — в <paramref name="notes"/>.
        /// </summary>
        internal static List<LabelEdit> ReadLabelEdits(Database database, Transaction transaction, string cartogramId, List<string> notes)
        {
            var edits = new List<LabelEdit>();
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId);
            if (tracked == null) return edits;
            foreach (var id in tracked)
            {
                if (!id.IsValid || id.IsErased || id.ObjectClass.DxfName != "INSERT") continue;
                if (!(transaction.GetObject(id, OpenMode.ForRead) is BlockReference block)) continue;
                if (!IsTagged(block, cartogramId, out _, out var values) || values.Length < 8 || !Equals(values[2].Value, NodeMark)) continue;
                if (!(values[4].Value is double fact) || !(values[5].Value is double project) || !(values[6].Value is double working) ||
                    !(values[7].Value is Point3d anchor)) continue;
                var at = new XY(anchor.X, anchor.Y);
                var attributes = Sources.Attributes(block, transaction);
                foreach (var (tag, isProject, before) in new[] { (ProjectTag, true, project), (FactTag, false, fact) })
                {
                    if (!attributes.TryGetValue(tag, out var attribute)) continue;
                    string text = (attribute.TextString ?? string.Empty).Trim();
                    if (text == ElevationFormat.Format(before, 2)) continue;
                    if (ElevationFormat.TryParse(text, out double value))
                        edits.Add(new LabelEdit { At = at, Project = isProject, Value = value, Before = before });
                    else
                        notes.Add((isProject ? "проектная" : "фактическая") + " отметка «" + text + "» у точки " + at + " не читается — не учтена");
                }
                if (attributes.TryGetValue(WorkingTag, out var workingAttribute))
                {
                    string text = (workingAttribute.TextString ?? string.Empty).Trim();
                    if (text != ElevationFormat.FormatSigned(working, 2))
                        notes.Add("рабочая отметка у точки " + at + " всегда Hпр − Hф: правка «" + text + "» не принята, меняйте проектную или фактическую");
                }
            }
            return edits;
        }

        /// <summary>For «устарела»: the number of labels whose text the user changed.</summary>
        internal static int CountLabelEdits(Database database, Transaction transaction, string cartogramId)
        {
            var notes = new List<string>();
            return ReadLabelEdits(database, transaction, cartogramId, notes).Count + notes.Count;
        }

        /// <summary>
        /// Writes the typed elevations into the surfaces: the point of the surface standing at the place (0,01 m)
        /// gets the new elevation, otherwise a point without a visible label is created there. The triangles of the
        /// surface then pass through it, so the cartogram shows exactly the typed value.
        /// </summary>
        private static List<string> ApplyLabelEdits(Database database, Transaction transaction, CartogramInfo c, List<LabelEdit> edits, KzmSettings settings)
        {
            var messages = new List<string>();
            foreach (var group in edits.GroupBy(e => e.Project))
            {
                string surfaceId = group.Key ? c.ProjectSurfaceId : c.FactSurfaceId;
                var surface = EditServices.ReadSurface(database, transaction, surfaceId);
                var points = PointBlocks.Read(database, transaction, settings, surface.Id);
                var nearest = new NearestPoints(points);
                int number = PointNumbering.Next(points.Select(p => p.Number));
                var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                Layers.Ensure(database, transaction, surface.Layer);
                PointBlocks.EnsureRegApp(database, transaction, PointBlocks.AppName);
                var definition = PointBlocks.EnsureDefinition(database, transaction);
                if (!surface.Appearance.IsSet) surface.Appearance = EditServices.FromDrawing(database, transaction, surface.Appearance);
                int changed = 0, created = 0;
                foreach (var edit in group)
                {
                    var existing = nearest.Within(edit.At, KzmSettings.DuplicateTolerance);
                    if (existing != null)
                    {
                        var reference = (BlockReference)transaction.GetObject(existing.Id, OpenMode.ForRead);
                        if (!Layers.IsUsable(database, transaction, reference.Layer, out string problem))
                            throw new KzmException("точка " + existing.Handle + " поверхности «" + surface.Name + "»: " + problem + ".");
                        EditServices.WriteElevation(transaction, reference, existing.Data, edit.Value, settings.Decimals);
                        changed++;
                    }
                    else
                    {
                        if (!Layers.IsUsable(database, transaction, surface.Layer, out string problem)) throw new KzmException(problem + ".");
                        var data = new PointData
                        {
                            SurfaceId = surface.Id, Elevation = edit.Value, Source = SourceKind.Node, XyMode = XyMode.Manual,
                            Number = (number++).ToString(CultureInfo.InvariantCulture), HideLabel = true,
                        };
                        PointBlocks.Create(transaction, space, definition, edit.At, surface.Layer, data, surface.Appearance, settings.Decimals);
                        created++;
                    }
                }
                surface.Revision++;
                KzmStore.WriteSurface(database, transaction, surface);
                EditServices.RefreshTriangles(database, transaction, surface, settings);
                messages.Add("Отметки с картограммы записаны в поверхность «" + surface.Name + "»: " +
                             string.Join(", ", group.Select(e => ElevationFormat.Format(e.Before, 2) + " → " + ElevationFormat.Format(e.Value, 2))) +
                             " (изменено точек " + changed + ", новых " + created + ").");
            }
            return messages;
        }

        /// <summary>Расчёт и построение (пересчёт): старые объекты картограммы заменяются новыми. Одна отмена.</summary>
        internal static CartogramComputation Build(Document document, string cartogramId, KzmSettings settings)
        {
            var database = document.Database;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var c = KzmStore.ReadCartograms(database, transaction).FirstOrDefault(x => x.Id == cartogramId)
                        ?? throw new KzmException("Картограмма не найдена.");
                // Elevations typed on the cartogram go into the surfaces first, then everything is recalculated.
                var notes = new List<string>();
                var edits = ReadLabelEdits(database, transaction, c.Id, notes);
                var applied = edits.Count > 0 ? ApplyLabelEdits(database, transaction, c, edits, settings) : new List<string>();
                var computation = Compute(database, transaction, c, settings);
                computation.Applied.AddRange(applied);
                computation.Problems.AddRange(notes);
                var moved = c.KeepMovedLabels ? ReadMoved(database, transaction, c.Id) : new MovedLabels();
                Erase(database, transaction, c.Id);
                var painter = new CartogramPainter(database, transaction, computation, moved);
                painter.Draw();
                computation.MovedLabels = painter.Moved;
                KzmStore.WriteCartogramObjects(database, transaction, c.Id, painter.Created);
                c.Built = true;
                c.DrawVersion = CartogramInfo.CurrentDrawVersion;
                c.BuiltFingerprint = computation.Fingerprint;
                c.LastFill = computation.Result.TotalFill;
                c.LastCut = computation.Result.TotalCut;
                c.LastControlFill = computation.Control?.Fill;
                c.LastControlCut = computation.Control?.Cut;
                c.LastArea = computation.Result.ComputedArea;
                c.LastSquares = computation.Result.Squares.Count;
                c.BuiltAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                KzmStore.WriteCartogram(database, transaction, c);
                UpdateStatements(database, transaction, c, computation.Problems);
                transaction.Commit();
                return computation;
            }
        }

        /// <summary>What «Удалить картограмму» removes besides the drawing (for the question to the user).</summary>
        internal static (int Statements, bool Boundary) Extras(Database database, Transaction transaction, CartogramInfo c)
        {
            int statements = (KzmStore.ReadCartogramObjects(database, transaction, c.Id + StatementsSuffix) ?? new List<ObjectId>())
                .Count(id => id.IsValid && !id.IsErased);
            return (statements, ModuleBoundary(database, transaction, c) != ObjectId.Null);
        }

        /// <summary>The boundary polyline drawn by points for this cartogram (never a polyline the user drew).</summary>
        internal static ObjectId ModuleBoundary(Database database, Transaction transaction, CartogramInfo c)
        {
            var id = SurfaceModels.FromHandle(database, c.BoundaryHandle);
            if (id.IsNull || !(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) return ObjectId.Null;
            return IsTagged(entity, c.Id, out _, out var values) && values.Length > 2 && Equals(values[2].Value, BoundaryMark) ? id : ObjectId.Null;
        }

        /// <summary>
        /// «Удалить картограмму»: её построение, ведомости объёмов, граница, нарисованная по точкам, и параметры.
        /// Поверхности и их точки не трогаются. Одна отмена.
        /// </summary>
        internal static void Delete(Database database, string cartogramId)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var c = KzmStore.ReadCartograms(database, transaction).FirstOrDefault(x => x.Id == cartogramId);
                Erase(database, transaction, cartogramId);
                foreach (var id in KzmStore.ReadCartogramObjects(database, transaction, cartogramId + StatementsSuffix) ?? new List<ObjectId>())
                    if (id.IsValid && !id.IsErased && transaction.GetObject(id, OpenMode.ForRead) is Table table) EraseEntity(database, transaction, table);
                if (c != null)
                {
                    var boundary = ModuleBoundary(database, transaction, c);
                    if (!boundary.IsNull) EraseEntity(database, transaction, (Entity)transaction.GetObject(boundary, OpenMode.ForRead));
                }
                KzmStore.DeleteCartogramObjects(database, transaction, cartogramId);
                KzmStore.DeleteCartogramObjects(database, transaction, cartogramId + StatementsSuffix);
                KzmStore.DeleteCartogram(database, transaction, cartogramId);
                transaction.Commit();
            }
        }

        internal static void EraseDrawing(Database database, string cartogramId)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                Erase(database, transaction, cartogramId);
                var c = KzmStore.ReadCartograms(database, transaction).FirstOrDefault(x => x.Id == cartogramId);
                if (c != null)
                {
                    c.Built = false;
                    KzmStore.WriteCartogram(database, transaction, c);
                }
                transaction.Commit();
            }
        }

        /// <summary>Objects of the built cartogram (for «Показать на чертеже»).</summary>
        internal static List<ObjectId> ObjectsOf(Database database, Transaction transaction, string cartogramId)
        {
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId);
            if (tracked != null) return tracked.Where(id => id.IsValid && !id.IsErased).ToList();
            var result = new List<ObjectId>();
            var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (id.ObjectClass.DxfName != "LINE") continue;
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity && IsTagged(entity, cartogramId, out bool statement, out _) && !statement)
                    result.Add(id);
            }
            return result;
        }

        /// <summary>Выгрузка в Excel или CSV (задание п. 6): узлы, квадраты, итоги, контрольный расчёт.</summary>
        internal static CartogramComputation Export(Document document, string cartogramId, string path, KzmSettings settings)
        {
            CartogramComputation computation;
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var c = KzmStore.ReadCartograms(document.Database, transaction).FirstOrDefault(x => x.Id == cartogramId)
                        ?? throw new KzmException("Картограмма не найдена.");
                computation = Compute(document.Database, transaction, c, settings);
                transaction.Commit();
            }
            var report = new CartogramReport
            {
                Info = computation.Info,
                Result = computation.Result,
                FactName = computation.Fact.Surface.Name,
                ProjectName = computation.Project.Surface.Name,
                Control = computation.Control,
                ElevationDecimals = settings.Decimals,
                Problems = computation.Problems,
                Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
            var sheets = CartogramExport.Sheets(report);
            string temporary = path + ".tmp";
            using (var stream = new System.IO.FileStream(temporary, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            {
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) CsvWriter.Write(stream, sheets);
                else XlsxWriter.Write(stream, sheets);
            }
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(temporary, path);
            return computation;
        }

        /// <summary>After a rebuild: the planning-soil row of the statements of this cartogram gets the new volumes.</summary>
        private static void UpdateStatements(Database database, Transaction transaction, CartogramInfo c, List<string> problems)
        {
            var ids = KzmStore.ReadCartogramObjects(database, transaction, c.Id + StatementsSuffix);
            if (ids == null) return;
            var alive = new List<ObjectId>();
            foreach (var id in ids)
            {
                if (!id.IsValid || id.IsErased || !(transaction.GetObject(id, OpenMode.ForRead) is Table table)) continue;
                alive.Add(id);
                int row = -1;
                for (int r = 0; r < table.Rows.Count && row < 0; r++)
                    if (string.Equals((table.Cells[r, 0].TextString ?? string.Empty).Trim(), PlanningRow, StringComparison.OrdinalIgnoreCase)) row = r;
                if (row < 0 || table.Columns.Count < 3)
                {
                    problems.Add("ведомость объёмов (" + table.Handle + "): строка «" + PlanningRow + "» не найдена — объёмы в ней не обновлены");
                    continue;
                }
                if (!Layers.IsUsable(database, transaction, table.Layer, out string problem))
                {
                    problems.Add("ведомость объёмов (" + table.Handle + "): " + problem + " — объёмы не обновлены");
                    continue;
                }
                table.UpgradeOpen();
                table.Cells[row, 1].TextString = Volume(c.LastFill, c.VolumeDecimals, true).TrimStart('+');
                table.Cells[row, 2].TextString = Volume(c.LastCut, c.VolumeDecimals, false).TrimStart('-');
            }
            if (alive.Count != ids.Count) KzmStore.WriteCartogramObjects(database, transaction, c.Id + StatementsSuffix, alive);
        }

        internal static string Volume(double value, int decimals, bool fill)
        {
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            if (rounded == 0) return "0";
            return (fill ? "+" : "-") + ElevationFormat.Format(rounded, decimals);
        }

        /// <summary>
        /// Ведомость объёмов земляных масс по форме рис. 9 СТБ 2073-2010: строку «Грунт планировки
        /// территории» заполняет модуль, остальные строки — проектировщик (значения не подставляются).
        /// </summary>
        internal static void InsertStatement(Document document, string cartogramId)
        {
            var database = document.Database;
            CartogramInfo c;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                c = KzmStore.ReadCartograms(database, transaction).FirstOrDefault(x => x.Id == cartogramId) ?? throw new KzmException("Картограмма не найдена.");
                transaction.Commit();
            }
            if (!c.Built) throw new KzmException("Сначала постройте картограмму «" + c.Name + "».");
            var pick = document.Editor.GetPoint(new PromptPointOptions("\nУкажите левый верхний угол ведомости объёмов земляных масс: "));
            if (pick.Status != PromptStatus.OK) return;
            var at = pick.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                Layers.Ensure(database, transaction, Layers.Totals);
                PointBlocks.EnsureRegApp(database, transaction, App);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                double k = c.UnitsPerMm;
                string[] names =
                {
                    PlanningRow,
                    "Вытесненный грунт, всего",
                    "в том числе при устройстве:\nа) подземных частей зданий (сооружений)",
                    "б) автодорожных покрытий",
                    "в) железнодорожных путей",
                    "г) подземных сетей",
                    "д) водоотводных сооружений",
                    "е) плодородной почвы на участках озеленения",
                    "Грунт для засыпки существующих канав, котлованов, образуемых от разбираемых подземных частей зданий (сооружений)",
                    "Грунт для устройства земляного полотна автодорог, железнодорожных путей",
                    "Грунт для устройства высоких полов зданий и обвалований сооружений",
                    "Поправка на уплотнение",
                    "Пригодный грунт, всего",
                    "Недостаток (избыток) пригодного грунта",
                    "Грунт, непригодный для устройства насыпи оснований зданий (сооружений) и подлежащий удалению с территории",
                    "Плодородный грунт, всего",
                    "в том числе:\nа) используемый для озеленения территории",
                    "б) недостаток (избыток) плодородного грунта",
                    "Итого перерабатываемого грунта",
                };
                var table = new Table { TableStyle = database.Tablestyle, Position = new Point3d(at.X, at.Y, 0), Layer = Layers.Totals };
                // All rows are data rows: the head of the form is built by merging cells below.
#pragma warning disable CS0618 // obsolete since 2008, still supported by AutoCAD 2021–2026 (checked by compiling all six targets)
                table.IsTitleSuppressed = true;
                table.IsHeaderSuppressed = true;
#pragma warning restore CS0618
                table.SetSize(3 + names.Length, 4);
                double[] widths = { 85, 20, 20, 20 };
                for (int col = 0; col < 4; col++) table.Columns[col].Width = widths[col] * k;
                table.Rows[0].Height = 5 * k;
                table.Rows[1].Height = 10 * k;
                table.Rows[2].Height = 10 * k;
                for (int row = 3; row < table.Rows.Count; row++) table.Rows[row].Height = 8 * k;
                double text = 2.5 * k;
                for (int row = 0; row < table.Rows.Count; row++)
                for (int col = 0; col < 4; col++)
                {
                    table.Cells[row, col].TextHeight = text;
                    table.Cells[row, col].Alignment = row < 3 || col > 0 ? CellAlignment.MiddleCenter : CellAlignment.MiddleLeft;
                }
                table.MergeCells(CellRange.Create(table, 0, 0, 2, 0));
                table.MergeCells(CellRange.Create(table, 0, 1, 0, 2));
                table.MergeCells(CellRange.Create(table, 1, 1, 1, 2));
                table.MergeCells(CellRange.Create(table, 0, 3, 2, 3));
                table.Cells[0, 0].TextString = "Наименование грунта";
                table.Cells[0, 1].TextString = "Количество, м³";
                table.Cells[1, 1].TextString = c.Name;
                table.Cells[2, 1].TextString = "Насыпь\n(+)";
                table.Cells[2, 2].TextString = "Выемка\n(–)";
                table.Cells[0, 3].TextString = "Примечание";
                for (int i = 0; i < names.Length; i++) table.Cells[3 + i, 0].TextString = names[i];
                table.Cells[3, 1].TextString = Volume(c.LastFill, c.VolumeDecimals, true).TrimStart('+');
                table.Cells[3, 2].TextString = Volume(c.LastCut, c.VolumeDecimals, false).TrimStart('-');
                table.Cells[3, 3].TextString = "КЗМ «" + c.Name + "»";
                table.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, c.Id),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, StatementMark));
                space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                table.GenerateLayout();
                var statements = KzmStore.ReadCartogramObjects(database, transaction, c.Id + StatementsSuffix) ?? new List<ObjectId>();
                statements.Add(table.ObjectId);
                KzmStore.WriteCartogramObjects(database, transaction, c.Id + StatementsSuffix, statements.Where(id => id.IsValid && !id.IsErased));
                transaction.Commit();
            }
        }

        internal static ResultBuffer Tag(string cartogramId) => new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, cartogramId));
    }
}
