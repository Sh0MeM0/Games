using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Commands
{
    public sealed partial class KzmCommands
    {
        private enum PairDecision { Skip, KeepFirst, KeepSecond, KeepBoth, DeleteBoth }

        /// <summary>
        /// KZM_DUPLICATES: по каждой паре точек ближе допуска пользователь решает, какую
        /// оставить. Решения применяются одной транзакцией в конце (одна отмена).
        /// </summary>
        [CommandMethod("KZM_DUPLICATES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Duplicates() => Run("близкие точки", document =>
        {
            var editor = document.Editor;
            var database = document.Database;
            var settings = EnsureSettings(document, false);
            if (settings == null) return;

            List<StoredPair> pairs;
            Dictionary<ObjectId, SurfacePoint> points;
            Dictionary<string, SurfaceInfo> surfaces;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                pairs = KzmStore.ReadConflicts(database, transaction).Where(pair => pair.IsAlive).ToList();
                points = PointBlocks.Read(database, transaction, settings).ToDictionary(point => point.Id, point => point);
                surfaces = KzmStore.ReadSurfaces(database, transaction).ToDictionary(surface => surface.Id, surface => surface);
                transaction.Commit();
            }
            pairs = pairs.Where(pair => points.ContainsKey(pair.First) && points.ContainsKey(pair.Second)).ToList();
            if (pairs.Count == 0)
            {
                editor.WriteMessage("\nКЗМ: пар близких точек, ожидающих решения, нет.");
                return;
            }

            var keywords = new[] { ("First", "Первая"), ("Second", "Вторая"), ("Both", "Обе"), ("Delete", "Удалить"), ("Skip", "Пропустить"), ("Exit", "Выход") };
            var decisions = new List<(StoredPair Pair, PairDecision Decision)>();
            double size = ZoomSize(settings);
            for (int index = 0; index < pairs.Count; index++)
            {
                var pair = pairs[index];
                var first = points[pair.First];
                var second = points[pair.Second];
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    Views.ZoomToObjects(editor, transaction, new[] { pair.First, pair.Second }, size);
                    transaction.Commit();
                }
                var labels = new List<DBText>
                {
                    TransientLabel("1", first.Position, settings, -2.0, 0.5),
                    TransientLabel("2", second.Position, settings, 0.7, -1.7),
                };
                var manager = TransientManager.CurrentTransientManager;
                foreach (var label in labels)
                    manager.AddTransient(label, TransientDrawingMode.DirectTopmost, 128, new IntegerCollection());
                Highlight(database, new[] { pair.First, pair.Second }, true);
                string keyword;
                try
                {
                    editor.WriteMessage("\nПара " + (index + 1) + " из " + pairs.Count + ": расстояние " +
                        ElevationFormat.Format(first.Position.DistanceTo(second.Position), 3) + " м.");
                    editor.WriteMessage("\n  1 — " + Describe(first, surfaces, settings));
                    editor.WriteMessage("\n  2 — " + Describe(second, surfaces, settings));
                    var options = new PromptKeywordOptions(string.Empty) { AllowNone = true };
                    options.SetMessageAndKeywords("\nКакую точку оставить? «Удалить» — удалить обе " +
                        "[Первая/Вторая/Обе/Удалить/Пропустить/Выход] <Пропустить>: ", "First Second Both Delete Skip Exit");
                    var result = editor.GetKeywords(options);
                    if (result.Status == PromptStatus.Cancel)
                    {
                        editor.WriteMessage("\nКЗМ: отменено, точки не изменены.");
                        return;
                    }
                    keyword = result.Status == PromptStatus.None ? "Skip" : Normalize(result.StringResult, keywords) ?? "Skip";
                }
                finally
                {
                    foreach (var label in labels)
                    {
                        manager.EraseTransient(label, new IntegerCollection());
                        label.Dispose();
                    }
                    Highlight(database, new[] { pair.First, pair.Second }, false);
                }
                if (keyword == "Exit") break;
                decisions.Add((pair, keyword == "First" ? PairDecision.KeepFirst : keyword == "Second" ? PairDecision.KeepSecond
                    : keyword == "Both" ? PairDecision.KeepBoth : keyword == "Delete" ? PairDecision.DeleteBoth : PairDecision.Skip));
            }

            var applied = decisions.Where(item => item.Decision != PairDecision.Skip).ToList();
            if (applied.Count == 0)
            {
                editor.WriteMessage("\nКЗМ: решений не принято, точки не изменены.");
                return;
            }

            int erased = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var toErase = new HashSet<ObjectId>();
                var kept = KzmStore.ReadKept(database, transaction).Where(pair => pair.IsAlive).ToList();
                foreach (var (pair, decision) in applied)
                {
                    if (decision == PairDecision.KeepFirst || decision == PairDecision.DeleteBoth) toErase.Add(pair.Second);
                    if (decision == PairDecision.KeepSecond || decision == PairDecision.DeleteBoth) toErase.Add(pair.First);
                    if (decision == PairDecision.KeepBoth && !kept.Any(other => other.Same(pair))) kept.Add(pair);
                }
                var touched = new HashSet<string>();
                foreach (var id in toErase)
                {
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (!Layers.IsUsable(database, transaction, entity.Layer, out string problem))
                        throw new KzmException("Точка " + entity.Handle + ": " + problem + ".");
                    touched.Add(points[id].Data.SurfaceId);
                    entity.UpgradeOpen();
                    entity.Erase();
                    erased++;
                }
                var resolved = applied.Select(item => item.Pair).ToList();
                var remaining = KzmStore.ReadConflicts(database, transaction)
                    .Where(pair => pair.IsAlive && !resolved.Any(done => done.Same(pair))).ToList();
                KzmStore.WriteConflicts(database, transaction, remaining);
                KzmStore.WriteKept(database, transaction, kept);
                var handles = new HashSet<string>(resolved.SelectMany(pair => new[] { pair.First.Handle.ToString(), pair.Second.Handle.ToString() }),
                    StringComparer.OrdinalIgnoreCase);
                CheckMarks.Clear(database, transaction, handles);
                foreach (var surfaceId in touched) KzmStore.TouchSurface(database, transaction, surfaceId);
                transaction.Commit();
            }
            int left = pairs.Count - applied.Count;
            editor.WriteMessage("\nКЗМ: решено пар: " + applied.Count + ", удалено точек: " + erased +
                (left > 0 ? ", осталось нерешённых: " + left + " (повторите KZM_DUPLICATES)." : "."));
        });

        private static string Describe(SurfacePoint point, Dictionary<string, SurfaceInfo> surfaces, KzmSettings settings)
        {
            string elevation = point.Elevation.IsValid ? ElevationFormat.Format(point.Elevation.Value.Value, settings.Decimals) : "отметка не читается";
            string surface = surfaces.TryGetValue(point.Data.SurfaceId, out var info) ? "«" + info.Name + "»" : "поверхность не найдена";
            string source = SourceKinds.Code(point.Data.Source) + (string.IsNullOrEmpty(point.Data.SourceHandle) ? string.Empty : " " + point.Data.SourceHandle);
            return "отметка " + elevation + ", " + surface + ", источник " + source + ", XY: " + XyModes.Title(point.Data.XyMode) + ", точка " + point.Handle;
        }

        private static DBText TransientLabel(string text, XY at, KzmSettings settings, double dx, double dy)
        {
            double height = settings.LabelHeightMm.Value * settings.UnitsPerPaperMm * 1.5;
            return new DBText
            {
                TextString = text,
                Height = height,
                // Labels are offset to opposite sides: the two points are almost at one place.
                Position = new Point3d(at.X + height * dx, at.Y + height * dy, 0),
                Color = Color.FromColorIndex(ColorMethod.ByAci, 2),
            };
        }
    }
}
