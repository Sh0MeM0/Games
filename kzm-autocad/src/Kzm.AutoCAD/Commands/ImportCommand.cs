using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.UI;
using Kzm.Core;

namespace Kzm.AutoCAD.Commands
{
    public sealed partial class KzmCommands
    {
        /// <summary>
        /// KZM_IMPORT: поверхность (слой) → тексты → способ XY → разбор → спорные вручную →
        /// запись одной транзакцией → отчёт, пометки и выделение проблемных объектов.
        /// </summary>
        [CommandMethod("KZM_IMPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Import() => Run("импорт", ImportCore);

        private static void ImportCore(Document document)
        {
            var editor = document.Editor;
            var database = document.Database;
            var settings = EnsureSettings(document, true);
            if (settings == null) return;
            var rules = settings.CreateRules();

            // 1. Surface and its layer first.
            var (surfaces, currentId, counts) = ReadSurfaceList(database, settings);
            SurfaceInfo surface;
            if (surfaces.Count == 0)
            {
                editor.WriteMessage("\nКЗМ: в чертеже нет поверхностей — создайте поверхность, в которую пойдут точки.");
                surface = AskNewSurface(document, surfaces);
            }
            else
            {
                PrintSurfaces(editor, surfaces, currentId, counts);
                surface = AskSurfaceNumber(editor, surfaces, currentId, "\nНомер поверхности для импорта", true, document);
            }
            if (surface == null) return;
            bool isNew = surfaces.All(item => item.Id != surface.Id);
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                if (!Layers.IsUsable(database, transaction, surface.Layer, out string problem))
                    throw new KzmException(problem + ". Разблокируйте/разморозьте слой или выберите другую поверхность.");
                if (!Layers.IsUsable(database, transaction, Layers.Check, out problem))
                    throw new KzmException(problem + ". На этом слое ставятся пометки проверки.");
                transaction.Commit();
            }
            editor.WriteMessage("\nИмпорт в поверхность " + surface.Title + ".");

            // 2. Texts.
            var textIds = SelectTexts(document, settings);
            if (textIds == null) return;
            if (textIds.Count == 0)
            {
                editor.WriteMessage("\nКЗМ: тексты не выбраны, импорт не выполнен.");
                return;
            }

            // 3. Where XY comes from.
            string xy = AskKeyword(editor, "\nОткуда брать XY точки", settings.LastXyMode == XyMode.Insertion ? "Insertion" : "Marker",
                ("Marker", "Маркер"), ("Insertion", "Вставка"));
            if (xy == null) return;
            var mode = xy == "Insertion" ? XyMode.Insertion : XyMode.Marker;
            var markers = new List<MarkerCandidate>();
            if (mode == XyMode.Marker)
            {
                markers = SelectMarkers(document);
                if (markers == null) return;
                while (!settings.MarkerRadius.HasValue)
                {
                    // A string prompt accepts both «1,5» and «1.5» (GetDistance would read the comma as a coordinate separator).
                    var radius = editor.GetString(new PromptStringOptions("\nРадиус поиска маркера R от точки вставки текста, м: ") { AllowSpaces = false });
                    if (radius.Status != PromptStatus.OK) return;
                    if (ElevationFormat.TryParse(radius.StringResult, out double value) && value > 0) settings.MarkerRadius = value;
                    else editor.WriteMessage("\nВведите положительное число, например 1,5.");
                }
                editor.WriteMessage("\nРадиус поиска маркера R = " + ElevationFormat.Format(settings.MarkerRadius.Value, settings.Decimals) +
                    " м (меняется в KZM_SETTINGS).");
            }
            settings.LastXyMode = mode;

            // 4. Read texts and the points already in the surface.
            var texts = new List<SourceText>();
            var textIdByHandle = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            List<SurfacePoint> existing;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in textIds)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) continue;
                    var text = Sources.ReadText(entity);
                    if (text == null) continue;
                    texts.Add(text);
                    textIdByHandle[text.Handle] = id;
                }
                existing = isNew ? new List<SurfacePoint>() : PointBlocks.Read(database, transaction, settings, surface.Id);
                transaction.Commit();
            }
            var existingPoints = existing.Select(point => new ExistingPoint(point.Handle, point.Position,
                point.Elevation.Value ?? double.NaN, point.Data.SourceHandle)).ToList();
            var existingIds = existing.ToDictionary(point => point.Handle, point => point.Id, StringComparer.OrdinalIgnoreCase);
            var brokenPoints = existing.Where(point => !point.Elevation.IsValid).ToList();

            var plan = ImportPlanner.Plan(texts, markers, existingPoints,
                new ImportOptions(rules, mode, settings.MarkerRadius, settings.DuplicateTolerance));

            // 5. Способ (в): disputed points by hand.
            var disputed = plan.Disputed.ToList();
            if (disputed.Count > 0)
            {
                editor.WriteMessage("\nСпорных отметок: " + disputed.Count + " (" + string.Join("; ", disputed
                    .GroupBy(item => item.Reason).Select(group => ImportReasons.Title(group.Key) + " — " + group.Count())) + ").");
                string answer = AskKeyword(editor, "\nРазобрать спорные вручную (указать положение/отметку)", "Yes", ("Yes", "Да"), ("No", "Нет"));
                if (answer == null) return;
                if (answer == "Yes" && !ResolveManually(document, disputed, textIdByHandle, settings))
                {
                    editor.WriteMessage("\nКЗМ: импорт отменён, чертёж не изменён.");
                    return;
                }
            }

            // 6. Points closer than the tolerance.
            var duplicates = ImportPlanner.FindDuplicates(plan, existingPoints);

            // 7. One write transaction.
            var created = new Dictionary<ImportItem, ObjectId>();
            var problemIds = new List<ObjectId>();
            var duplicateRows = new List<ReportRow>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
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
                    created[item] = PointBlocks.Create(transaction, space, definitionId, item.Position.Value, surface.Layer, data, settings);
                }

                foreach (var item in plan.Rejected.Concat(plan.Disputed))
                {
                    CheckMarks.Add(transaction, space, item.Source.Anchor, ImportReasons.ShortLabel(item.Reason), item.Source.Handle, settings);
                    problemIds.Add(textIdByHandle[item.Source.Handle]);
                }

                var pairs = KzmStore.ReadConflicts(database, transaction).Where(pair => pair.IsAlive).ToList();
                foreach (var duplicate in duplicates)
                {
                    var first = created[duplicate.First];
                    var second = duplicate.SecondNew != null ? created[duplicate.SecondNew] : existingIds[duplicate.SecondExisting.Handle];
                    var pair = new StoredPair(first, second, duplicate.Distance);
                    if (!pairs.Any(other => other.Same(pair))) pairs.Add(pair);
                    CheckMarks.Add(transaction, space, duplicate.First.Position.Value, "близко", first.Handle.ToString(), settings);
                    problemIds.Add(first);
                    problemIds.Add(second);
                    double secondElevation = duplicate.SecondNew?.Elevation ?? duplicate.SecondExisting.Elevation;
                    var row = new ReportRow
                    {
                        Result = "близкие точки",
                        Reason = "расстояние " + ElevationFormat.Format(duplicate.Distance, 3) + " м < " +
                                 ElevationFormat.Format(settings.DuplicateTolerance, Math.Max(2, settings.Decimals)) +
                                 " м — решите командой KZM_DUPLICATES" + (duplicate.SecondExisting != null ? " (вторая точка была в поверхности)" : string.Empty),
                        Text = OneLine(duplicate.First.Source.Plain),
                        Elevation = ElevationFormat.Format(duplicate.First.Elevation.Value, settings.Decimals) + " / " +
                                    (double.IsNaN(secondElevation) ? "?" : ElevationFormat.Format(secondElevation, settings.Decimals)),
                        X = ElevationFormat.Format(duplicate.First.Position.Value.X, 3),
                        Y = ElevationFormat.Format(duplicate.First.Position.Value.Y, 3),
                        XySource = XyModes.Title(duplicate.First.PositionMode),
                        Handle = first.Handle + " / " + second.Handle,
                        Problem = true,
                    };
                    row.Targets.Add(first);
                    row.Targets.Add(second);
                    duplicateRows.Add(row);
                }
                KzmStore.WriteConflicts(database, transaction, pairs);

                if (created.Count > 0) surface.Revision++;
                if (isNew || created.Count > 0) KzmStore.WriteSurface(database, transaction, surface);
                KzmStore.WriteCurrentSurfaceId(database, transaction, surface.Id);
                KzmStore.WriteSettings(database, transaction, settings);
                transaction.Commit();
            }

            // 8. Report.
            var report = BuildReport(plan, surface, settings, created, textIdByHandle, existingIds, duplicateRows, brokenPoints, duplicates.Count);
            foreach (var line in report.Summary) editor.WriteMessage("\n" + line);
            foreach (var point in brokenPoints) problemIds.Add(point.Id);
            var selection = problemIds.Where(id => id.IsValid && !id.IsErased).Distinct().ToArray();
            if (selection.Length > 0) editor.SetImpliedSelection(selection);
            Reports.Show(document, report);
        }

        private static Report BuildReport(ImportPlan plan, SurfaceInfo surface, KzmSettings settings, Dictionary<ImportItem, ObjectId> created,
            Dictionary<string, ObjectId> textIds, Dictionary<string, ObjectId> existingIds, List<ReportRow> duplicateRows,
            List<SurfacePoint> brokenPoints, int duplicateCount)
        {
            int decimals = settings.Decimals;
            var report = new Report
            {
                Title = "импорт в «" + surface.Name + "», " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
                ZoomSize = ZoomSize(settings),
            };
            report.Summary.Add("Импорт в поверхность " + surface.Title + ". Шаблон: " + plan.Options.Rules.Pattern +
                               "; диапазон " + plan.Options.Rules.RangeText + " м.");
            report.Summary.AddRange(ImportSummary.Lines(plan, duplicateCount, created.Count));
            if (brokenPoints.Count > 0)
                report.Summary.Add("Точки поверхности с нечитаемой отметкой в атрибуте: " + brokenPoints.Count + " — исправьте атрибут " + PointBlocks.Tag + ".");
            bool anyProblem = plan.Rejected.Any() || plan.Disputed.Any() || duplicateCount > 0 || brokenPoints.Count > 0;
            report.Summary.Add(anyProblem
                ? "Проблемные объекты выделены и помечены на слое " + Layers.Check + " (не печатается; убрать — KZM_CLEARMARKS). Двойной щелчок по строке — показать объект."
                : "Проблем не найдено.");

            foreach (var item in plan.Items)
            {
                var row = new ReportRow
                {
                    Text = OneLine(item.Source.Plain),
                    Problem = item.Status == ImportStatus.Rejected || item.Status == ImportStatus.Disputed,
                };
                textIds.TryGetValue(item.Source.Handle, out var textId);
                switch (item.Status)
                {
                    case ImportStatus.Accepted:
                        row.Result = "принято";
                        row.Reason = item.Message;
                        row.Elevation = ElevationFormat.Format(item.Elevation.Value, decimals);
                        row.X = ElevationFormat.Format(item.Position.Value.X, 3);
                        row.Y = ElevationFormat.Format(item.Position.Value.Y, 3);
                        row.XySource = XyModes.Title(item.PositionMode) +
                                       (item.MarkerDistance.HasValue ? " (" + ElevationFormat.Format(item.MarkerDistance.Value, 2) + " м)" : string.Empty);
                        if (created.TryGetValue(item, out var pointId)) { row.Handle = pointId.Handle.ToString(); row.Targets.Add(pointId); }
                        break;
                    case ImportStatus.Rejected:
                        row.Result = "отброшено";
                        row.Reason = item.Message;
                        break;
                    case ImportStatus.Disputed:
                        row.Result = "спорная";
                        row.Reason = item.Message;
                        row.Elevation = item.Elevation.HasValue
                            ? ElevationFormat.Format(item.Elevation.Value, decimals)
                            : string.Join("; ", item.Candidates.Select(value => ElevationFormat.Format(value, decimals)));
                        break;
                    default:
                        row.Result = "пропущено";
                        row.Reason = item.Message;
                        if (item.ExistingPointHandle != null && existingIds.TryGetValue(item.ExistingPointHandle, out var existingId))
                            row.Targets.Add(existingId);
                        break;
                }
                if (item.Status != ImportStatus.Accepted)
                {
                    row.X = ElevationFormat.Format(item.Source.Anchor.X, 3);
                    row.Y = ElevationFormat.Format(item.Source.Anchor.Y, 3);
                    row.XySource = "вставка текста";
                    row.Handle = item.Source.Handle;
                }
                if (textId.IsValid) row.Targets.Add(textId);
                report.Rows.Add(row);
            }
            report.Rows.AddRange(duplicateRows);
            foreach (var point in brokenPoints)
            {
                var row = new ReportRow
                {
                    Result = "ошибка точки",
                    Reason = point.Elevation.Problem,
                    X = ElevationFormat.Format(point.Position.X, 3),
                    Y = ElevationFormat.Format(point.Position.Y, 3),
                    Handle = point.Handle,
                    Problem = true,
                };
                row.Targets.Add(point.Id);
                report.Rows.Add(row);
            }
            return report;
        }

        // ---------- selection of texts and markers ----------

        private static List<ObjectId> SelectTexts(Document document, KzmSettings settings)
        {
            var editor = document.Editor;
            var database = document.Database;
            string last = settings.LastSelectionMode == "LAYERS" ? "Layers" : settings.LastSelectionMode == "SIMILAR" ? "Similar" : "Window";
            string mode = AskKeyword(editor, "\nВыбор текстов отметок", last, ("Window", "Рамка"), ("Layers", "Слои"), ("Similar", "Подобные"));
            if (mode == null) return null;
            settings.LastSelectionMode = mode.ToUpperInvariant();

            if (mode == "Window")
            {
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "TEXT,MTEXT") });
                var selection = editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nВыберите тексты отметок (TEXT, MTEXT)" }, filter);
                if (selection.Status == PromptStatus.Cancel) return null;
                return selection.Status == PromptStatus.OK ? selection.Value.GetObjectIds().ToList() : new List<ObjectId>();
            }

            if (mode == "Layers")
            {
                var patterns = new List<string>();
                var names = new List<string>();
                while (true)
                {
                    var options = new PromptEntityOptions(string.Empty) { AllowNone = true };
                    options.SetMessageAndKeywords("\nУкажите объект на слое с отметками или [Имена] <закончить>: ", "Names");
                    var pick = editor.GetEntity(options);
                    if (pick.Status == PromptStatus.Cancel) return null;
                    if (pick.Status == PromptStatus.None) break;
                    if (pick.Status == PromptStatus.Keyword)
                    {
                        var typed = editor.GetString(new PromptStringOptions("\nИмена слоёв через запятую (можно * и ?): ") { AllowSpaces = true });
                        if (typed.Status != PromptStatus.OK) return null;
                        foreach (var part in typed.StringResult.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0))
                        {
                            patterns.Add(part);
                            names.Add(part);
                        }
                        break;
                    }
                    if (pick.Status != PromptStatus.OK) continue;
                    using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        string layer = ((Entity)transaction.GetObject(pick.ObjectId, OpenMode.ForRead)).Layer;
                        transaction.Commit();
                        if (!names.Contains(layer, StringComparer.OrdinalIgnoreCase))
                        {
                            names.Add(layer);
                            patterns.Add(Sources.EscapeWildcards(layer));
                        }
                        editor.WriteMessage("\nСлои: " + string.Join(", ", names));
                    }
                }
                if (patterns.Count == 0) return new List<ObjectId>();
                var ids = Sources.SelectAllInCurrentSpace(editor, database, "TEXT,MTEXT", string.Join(",", patterns));
                editor.WriteMessage("\nНа слоях " + string.Join(", ", names) + " найдено текстов TEXT/MTEXT: " + ids.Count + ".");
                return ids;
            }

            // Similar: same type, layer, text style and height as the sample.
            var sampleOptions = new PromptEntityOptions("\nУкажите образец текста отметки: ");
            sampleOptions.SetRejectMessage("\nНужен однострочный (TEXT) или многострочный (MTEXT) текст.");
            sampleOptions.AddAllowedClass(typeof(DBText), true);
            sampleOptions.AddAllowedClass(typeof(MText), true);
            var sample = editor.GetEntity(sampleOptions);
            if (sample.Status != PromptStatus.OK) return null;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                var entity = (Entity)transaction.GetObject(sample.ObjectId, OpenMode.ForRead);
                bool isMText = entity is MText;
                ObjectId style = isMText ? ((MText)entity).TextStyleId : ((DBText)entity).TextStyleId;
                double height = isMText ? ((MText)entity).TextHeight : ((DBText)entity).Height;
                string layer = entity.Layer;
                string styleName = ((TextStyleTableRecord)transaction.GetObject(style, OpenMode.ForRead)).Name;
                var candidates = Sources.SelectAllInCurrentSpace(editor, database, isMText ? "MTEXT" : "TEXT", Sources.EscapeWildcards(layer));
                var result = candidates.Where(id =>
                {
                    var other = transaction.GetObject(id, OpenMode.ForRead);
                    if (other is MText m && isMText) return m.TextStyleId == style && Sources.SameHeight(m.TextHeight, height);
                    if (other is DBText t && !isMText && !(other is AttributeDefinition))
                        return t.TextStyleId == style && Sources.SameHeight(t.Height, height);
                    return false;
                }).ToList();
                transaction.Commit();
                editor.WriteMessage("\nПодобные образцу: " + (isMText ? "MTEXT" : "TEXT") + ", слой «" + layer + "», стиль «" + styleName +
                    "», высота " + ElevationFormat.Format(height, 3) + " — найдено " + result.Count + ".");
                return result;
            }
        }

        /// <summary>Способ (б): the user shows one marker; all similar markers in the current space are used.</summary>
        private static List<MarkerCandidate> SelectMarkers(Document document)
        {
            var editor = document.Editor;
            var database = document.Database;
            var options = new PromptEntityOptions("\nУкажите образец маркера точки съёмки (POINT, CIRCLE или блок): ");
            options.SetRejectMessage("\nНужен объект POINT, CIRCLE или вхождение блока.");
            options.AddAllowedClass(typeof(DBPoint), true);
            options.AddAllowedClass(typeof(Circle), true);
            options.AddAllowedClass(typeof(BlockReference), true);
            var pick = editor.GetEntity(options);
            if (pick.Status != PromptStatus.OK) return null;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                var sample = (Entity)transaction.GetObject(pick.ObjectId, OpenMode.ForRead);
                string layer = sample.Layer;
                if (string.Equals(layer, Layers.Check, StringComparison.OrdinalIgnoreCase))
                    throw new KzmException("Образец лежит на слое пометок " + Layers.Check + ". Укажите маркер съёмки.");
                string dxf;
                string blockName = null;
                double radius = 0;
                string description;
                switch (sample)
                {
                    case DBPoint _:
                        dxf = "POINT";
                        description = "POINT";
                        break;
                    case Circle circle:
                        dxf = "CIRCLE";
                        radius = circle.Radius;
                        description = "CIRCLE радиусом " + ElevationFormat.Format(radius, 3);
                        break;
                    default:
                        dxf = "INSERT";
                        blockName = Sources.EffectiveName((BlockReference)sample, transaction);
                        if (string.Equals(blockName, PointBlocks.BlockName, StringComparison.OrdinalIgnoreCase))
                            throw new KzmException("Образец — точка модуля («" + PointBlocks.BlockName + "»). Укажите маркер съёмки.");
                        description = "блок «" + blockName + "»";
                        break;
                }
                var result = new List<MarkerCandidate>();
                foreach (var id in Sources.SelectAllInCurrentSpace(editor, database, dxf, Sources.EscapeWildcards(layer)))
                {
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (entity is Circle circle && !Sources.SameHeight(circle.Radius, radius)) continue;
                    if (entity is BlockReference block && !string.Equals(Sources.EffectiveName(block, transaction), blockName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var position = Sources.MarkerPosition(entity);
                    if (position.HasValue) result.Add(new MarkerCandidate(entity.Handle.ToString(), position.Value));
                }
                transaction.Commit();
                editor.WriteMessage("\nМаркеры, подобные образцу (" + description + ", слой «" + layer + "»): " + result.Count + ".");
                return result;
            }
        }

        // ---------- способ (в) ----------

        /// <summary>Returns false when the user pressed Esc (the whole import is cancelled).</summary>
        private static bool ResolveManually(Document document, List<ImportItem> disputed, Dictionary<string, ObjectId> textIds, KzmSettings settings)
        {
            var editor = document.Editor;
            var database = document.Database;
            int decimals = settings.Decimals;
            double size = ZoomSize(settings);
            var keywords = new[] { ("Insertion", "Вставка"), ("Skip", "Пропустить"), ("Stop", "Стоп") };
            for (int index = 0; index < disputed.Count; index++)
            {
                var item = disputed[index];
                var textId = textIds[item.Source.Handle];
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    Views.ZoomToObjects(editor, transaction, new[] { textId }, size);
                    transaction.Commit();
                }
                Highlight(database, new[] { textId }, true);
                try
                {
                    editor.WriteMessage("\nСпорная " + (index + 1) + " из " + disputed.Count + ": «" + OneLine(item.Source.Plain) + "» — " + item.Message + ".");
                    double elevation;
                    if (item.Elevation.HasValue)
                    {
                        elevation = item.Elevation.Value;
                    }
                    else
                    {
                        string variants = string.Join("; ", item.Candidates.Select(value => ElevationFormat.Format(value, decimals)));
                        double? typed = null;
                        while (true)
                        {
                            var answer = editor.GetString(new PromptStringOptions(
                                "\nОтметка этой точки (в тексте: " + variants + "); Enter — пропустить, * — закончить разбор: ") { AllowSpaces = false });
                            if (answer.Status != PromptStatus.OK) return false;
                            string text = answer.StringResult.Trim();
                            if (text.Length == 0) break;
                            if (text == "*") return true;
                            if (ElevationFormat.TryParse(text, out double value)) { typed = value; break; }
                            editor.WriteMessage("\nВведите одно число, например 145,32.");
                        }
                        if (!typed.HasValue)
                        {
                            item.SkipManually();
                            continue;
                        }
                        elevation = typed.Value;
                    }

                    var options = new PromptPointOptions(string.Empty) { AllowNone = true };
                    options.SetMessageAndKeywords("\nУкажите положение точки с отметкой " + ElevationFormat.Format(elevation, decimals) +
                        " или [Вставка/Пропустить/Стоп] <Пропустить>: ", "Insertion Skip Stop");
                    var point = editor.GetPoint(options);
                    if (point.Status == PromptStatus.Cancel) return false;
                    string keyword = point.Status == PromptStatus.Keyword ? Normalize(point.StringResult, keywords) : null;
                    if (point.Status == PromptStatus.None || keyword == "Skip")
                    {
                        item.SkipManually();
                        continue;
                    }
                    if (keyword == "Stop") return true;
                    if (keyword == "Insertion")
                    {
                        item.ResolveManually(elevation, item.Source.Anchor, XyMode.Insertion);
                        continue;
                    }
                    if (point.Status != PromptStatus.OK) continue;
                    var world = point.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                    item.ResolveManually(elevation, new XY(world.X, world.Y), XyMode.Manual);
                }
                finally
                {
                    Highlight(database, new[] { textId }, false);
                }
            }
            return true;
        }
    }
}
