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
    /// <summary>Everything the import form collected; the same request drives the preview and the import.</summary>
    internal sealed class ImportRequest
    {
        internal string SurfaceId { get; set; }
        internal List<ObjectId> TextIds { get; set; } = new List<ObjectId>();
        internal List<MarkerCandidate> Markers { get; set; } = new List<MarkerCandidate>();
        internal XyMode Mode { get; set; }
        internal KzmSettings Settings { get; set; }
        internal Dictionary<string, ImportOverride> Overrides { get; set; } = new Dictionary<string, ImportOverride>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class ImportResult
    {
        internal SurfaceInfo Surface { get; set; }
        internal ImportPlan Plan { get; set; }
        internal IReadOnlyList<ImportDuplicate> Duplicates { get; set; }
        internal List<SurfacePoint> BrokenPoints { get; set; } = new List<SurfacePoint>();
        internal Dictionary<string, ObjectId> TextIds { get; } = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, ObjectId> ExistingIds { get; } = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Filled only by the import itself (not by the preview).</summary>
        internal Dictionary<ImportItem, ObjectId> Created { get; } = new Dictionary<ImportItem, ObjectId>();
        internal bool Committed { get; set; }
        internal List<string> Summary { get; } = new List<string>();
    }

    internal static class ImportService
    {
        /// <summary>Reads texts and surface points and computes the plan; the drawing is not changed.</summary>
        internal static ImportResult Prepare(Database database, ImportRequest request)
        {
            var settings = request.Settings;
            var rules = settings.CreateRules();
            var result = new ImportResult();
            var texts = new List<SourceText>();
            List<SurfacePoint> existing;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                result.Surface = KzmStore.ReadSurfaces(database, transaction).FirstOrDefault(surface => surface.Id == request.SurfaceId)
                                 ?? throw new KzmException("Поверхность для импорта не найдена в чертеже. Выберите её в списке.");
                foreach (var id in request.TextIds.Where(id => id.IsValid && !id.IsErased))
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) continue;
                    var text = Sources.ReadText(entity);
                    if (text == null) continue;
                    texts.Add(text);
                    result.TextIds[text.Handle] = id;
                }
                existing = PointBlocks.Read(database, transaction, settings, request.SurfaceId);
                transaction.Commit();
            }
            foreach (var point in existing) result.ExistingIds[point.Handle] = point.Id;
            result.BrokenPoints = existing.Where(point => !point.Elevation.IsValid).ToList();
            var existingPoints = existing.Select(point => new ExistingPoint(point.Handle, point.Position,
                point.Elevation.Value ?? double.NaN, point.Data.SourceHandle)).ToList();

            var markers = request.Mode == XyMode.Marker ? request.Markers : new List<MarkerCandidate>();
            result.Plan = ImportPlanner.Plan(texts, markers, existingPoints,
                new ImportOptions(rules, request.Mode, settings.MarkerRadius, settings.DuplicateTolerance));
            ImportPlanner.ApplyOverrides(result.Plan, request.Overrides);
            result.Duplicates = ImportPlanner.FindDuplicates(result.Plan, existingPoints);
            result.Summary.AddRange(ImportSummary.Lines(result.Plan, result.Duplicates.Count, result.Plan.Accepted.Count(), true));
            if (result.BrokenPoints.Count > 0)
                result.Summary.Add("Точки поверхности с нечитаемой отметкой: " + result.BrokenPoints.Count + " (см. «Точки»).");
            return result;
        }

        /// <summary>Import: one transaction inside the KZM_RUN command (one UNDO step).</summary>
        internal static ImportResult Commit(Document document, ImportRequest request)
        {
            var database = document.Database;
            var settings = request.Settings;
            var problems = settings.ValidateForImport();
            if (problems.Count > 0) throw new KzmException("Не хватает настроек: " + string.Join("; ", problems) + ".");
            if (request.Mode == XyMode.Marker && !settings.MarkerRadius.HasValue) throw new KzmException("Не задан радиус поиска маркера R.");
            var result = Prepare(database, request);
            var surface = result.Surface;
            var plan = result.Plan;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                if (!Layers.IsUsable(database, transaction, surface.Layer, out string problem))
                    throw new KzmException(problem + ". Разблокируйте/разморозьте слой точек.");
                if (!Layers.IsUsable(database, transaction, Layers.Check, out problem))
                    throw new KzmException(problem + ". На этом слое ставятся пометки проверки.");
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                Layers.Ensure(database, transaction, surface.Layer);
                Layers.Ensure(database, transaction, Layers.Check, 1, false);
                PointBlocks.EnsureRegApp(database, transaction, PointBlocks.AppName);
                PointBlocks.EnsureRegApp(database, transaction, CheckMarks.AppName);
                var definitionId = PointBlocks.EnsureDefinition(database, transaction, settings);

                foreach (var item in plan.Accepted)
                {
                    var data = new PointData
                    {
                        SurfaceId = surface.Id,
                        Elevation = item.Elevation.Value,
                        Source = item.Source.Kind,
                        SourceHandle = item.Source.Handle,
                        XyMode = item.PositionMode,
                        MarkerHandle = item.MarkerHandle,
                    };
                    result.Created[item] = PointBlocks.Create(transaction, space, definitionId, item.Position.Value, surface.Layer, data, settings);
                }

                // Old marks of these texts are replaced by the marks of this check.
                CheckMarks.Clear(database, transaction, new HashSet<string>(result.TextIds.Keys, StringComparer.OrdinalIgnoreCase));
                foreach (var item in plan.Rejected.Concat(plan.Disputed))
                    CheckMarks.Add(transaction, space, item.Source.Anchor, ImportReasons.ShortLabel(item.Reason), item.Source.Handle, settings);

                var pairs = KzmStore.ReadConflicts(database, transaction).Where(pair => pair.IsAlive).ToList();
                foreach (var duplicate in result.Duplicates)
                {
                    var first = result.Created[duplicate.First];
                    var second = duplicate.SecondNew != null ? result.Created[duplicate.SecondNew] : result.ExistingIds[duplicate.SecondExisting.Handle];
                    var pair = new StoredPair(first, second, duplicate.Distance);
                    if (!pairs.Any(other => other.Same(pair))) pairs.Add(pair);
                    CheckMarks.Add(transaction, space, duplicate.First.Position.Value, "близко", first.Handle.ToString(), settings);
                }
                KzmStore.WriteConflicts(database, transaction, pairs);

                if (result.Created.Count > 0)
                {
                    surface.Revision++;
                    KzmStore.WriteSurface(database, transaction, surface);
                }
                KzmStore.WriteCurrentSurfaceId(database, transaction, surface.Id);
                KzmStore.WriteSettings(database, transaction, settings);
                transaction.Commit();
            }
            result.Committed = true;
            result.Summary.Clear();
            result.Summary.Add("Импорт в поверхность " + surface.Title + " выполнен.");
            result.Summary.AddRange(ImportSummary.Lines(plan, result.Duplicates.Count, result.Created.Count));
            if (plan.Rejected.Any() || plan.Disputed.Any() || result.Duplicates.Count > 0)
                result.Summary.Add("Проблемные места помечены на слое " + Layers.Check + " (не печатается).");
            return result;
        }

        // ---------- picking in the drawing (inside KZM_PICK) ----------

        internal static List<ObjectId> PickTexts(Editor editor)
        {
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "TEXT,MTEXT") });
            var selection = editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nВыберите тексты отметок (TEXT, MTEXT), Enter — готово" }, filter);
            return selection.Status == PromptStatus.OK ? selection.Value.GetObjectIds().ToList() : null;
        }

        /// <summary>All TEXT/MTEXT of the same type, layer, style and height as the picked sample.</summary>
        internal static (List<ObjectId> Ids, string Description) PickSimilarTexts(Document document)
        {
            var editor = document.Editor;
            var options = new PromptEntityOptions("\nУкажите образец текста отметки: ");
            options.SetRejectMessage("\nНужен TEXT или MTEXT.");
            options.AddAllowedClass(typeof(DBText), true);
            options.AddAllowedClass(typeof(MText), true);
            var pick = editor.GetEntity(options);
            if (pick.Status != PromptStatus.OK) return (null, null);
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var sample = (Entity)transaction.GetObject(pick.ObjectId, OpenMode.ForRead);
                bool isMText = sample is MText;
                ObjectId style = isMText ? ((MText)sample).TextStyleId : ((DBText)sample).TextStyleId;
                double height = isMText ? ((MText)sample).TextHeight : ((DBText)sample).Height;
                string styleName = ((TextStyleTableRecord)transaction.GetObject(style, OpenMode.ForRead)).Name;
                var space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead);
                var ids = new List<ObjectId>();
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass.DxfName != (isMText ? "MTEXT" : "TEXT")) continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (!string.Equals(entity.Layer, sample.Layer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entity is MText m && m.TextStyleId == style && Sources.SameHeight(m.TextHeight, height)) ids.Add(id);
                    else if (entity is DBText t && t.TextStyleId == style && Sources.SameHeight(t.Height, height)) ids.Add(id);
                }
                transaction.Commit();
                return (ids, (isMText ? "MTEXT" : "TEXT") + ", слой «" + sample.Layer + "», стиль «" + styleName +
                    "», высота " + ElevationFormat.Format(height, 3));
            }
        }

        /// <summary>Способ (б): one sample marker; all similar markers of the current space.</summary>
        internal static (List<MarkerCandidate> Markers, string Description) PickMarkers(Document document)
        {
            var editor = document.Editor;
            var options = new PromptEntityOptions("\nУкажите образец маркера точки съёмки (POINT, CIRCLE или блок): ");
            options.SetRejectMessage("\nНужен POINT, CIRCLE или блок.");
            options.AddAllowedClass(typeof(DBPoint), true);
            options.AddAllowedClass(typeof(Circle), true);
            options.AddAllowedClass(typeof(BlockReference), true);
            var pick = editor.GetEntity(options);
            if (pick.Status != PromptStatus.OK) return (null, null);
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var sample = (Entity)transaction.GetObject(pick.ObjectId, OpenMode.ForRead);
                if (string.Equals(sample.Layer, Layers.Check, StringComparison.OrdinalIgnoreCase))
                    throw new KzmException("Образец лежит на слое пометок " + Layers.Check + ". Укажите маркер съёмки.");
                string dxf = pick.ObjectId.ObjectClass.DxfName;
                string blockName = sample is BlockReference block ? Sources.EffectiveName(block, transaction) : null;
                if (string.Equals(blockName, PointBlocks.BlockName, StringComparison.OrdinalIgnoreCase))
                    throw new KzmException("Образец — точка модуля («" + PointBlocks.BlockName + "»). Укажите маркер съёмки.");
                double radius = sample is Circle circle ? circle.Radius : 0;
                var space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead);
                var markers = new List<MarkerCandidate>();
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass.DxfName != dxf) continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (!string.Equals(entity.Layer, sample.Layer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entity is Circle c && !Sources.SameHeight(c.Radius, radius)) continue;
                    if (entity is BlockReference b && !string.Equals(Sources.EffectiveName(b, transaction), blockName, StringComparison.OrdinalIgnoreCase)) continue;
                    var position = Sources.MarkerPosition(entity);
                    if (position.HasValue) markers.Add(new MarkerCandidate(entity.Handle.ToString(), position.Value));
                }
                transaction.Commit();
                string what = dxf == "POINT" ? "POINT" : dxf == "CIRCLE" ? "CIRCLE R=" + ElevationFormat.Format(radius, 3) : "блок «" + blockName + "»";
                return (markers, what + ", слой «" + sample.Layer + "»");
            }
        }

        internal static XY? PickPoint(Editor editor, string prompt)
        {
            var result = editor.GetPoint(new PromptPointOptions("\n" + prompt + ": "));
            if (result.Status != PromptStatus.OK) return null;
            var world = result.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            return new XY(world.X, world.Y);
        }

        internal static string CountTexts(Database database, IEnumerable<ObjectId> ids)
        {
            int text = 0, mtext = 0;
            foreach (var id in ids)
            {
                if (id.ObjectClass.DxfName == "MTEXT") mtext++;
                else text++;
            }
            return (text + mtext).ToString(CultureInfo.InvariantCulture) + " (TEXT " + text + ", MTEXT " + mtext + ")";
        }
    }
}
