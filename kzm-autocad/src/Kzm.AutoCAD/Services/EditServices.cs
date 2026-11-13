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
    internal enum PairDecision { KeepFirst, KeepSecond, KeepBoth, DeleteBoth }

    /// <summary>
    /// Changes of surfaces, points and pairs. Every method is called inside one
    /// KZM_RUN command (one UNDO step). After a change of points the triangles of the
    /// surface are redrawn if they are shown (автоматическая перетриангуляция).
    /// </summary>
    internal static class EditServices
    {
        internal static void CreateSurface(Database database, SurfaceInfo surface, bool makeCurrent)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                Layers.Ensure(database, transaction, surface.Layer);
                KzmStore.WriteSurface(database, transaction, surface);
                if (makeCurrent) KzmStore.WriteCurrentSurfaceId(database, transaction, surface.Id);
                transaction.Commit();
            }
        }

        internal static SurfaceInfo ReadSurface(Database database, Transaction transaction, string surfaceId) =>
            KzmStore.ReadSurfaces(database, transaction).FirstOrDefault(surface => surface.Id == surfaceId)
            ?? throw new KzmException("Поверхность не найдена в чертеже.");

        /// <summary>Rename / change type / change layer; optionally moves existing points to the new layer.</summary>
        internal static int UpdateSurface(Database database, SurfaceInfo changes, bool movePoints, KzmSettings settings)
        {
            int moved = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surface = ReadSurface(database, transaction, changes.Id);
                surface.Name = changes.Name;
                surface.Kind = changes.Kind;
                surface.Layer = changes.Layer;
                Layers.Ensure(database, transaction, surface.Layer);
                if (!Layers.IsUsable(database, transaction, surface.Layer, out string problem)) throw new KzmException(problem + ".");
                if (movePoints)
                {
                    foreach (var point in PointBlocks.Read(database, transaction, settings, surface.Id))
                    {
                        var entity = (Entity)transaction.GetObject(point.Id, OpenMode.ForRead);
                        if (string.Equals(entity.Layer, surface.Layer, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Layers.IsUsable(database, transaction, entity.Layer, out problem))
                            throw new KzmException("Точка " + point.Handle + ": " + problem + ".");
                        entity.UpgradeOpen();
                        entity.Layer = surface.Layer;
                        moved++;
                    }
                }
                surface.Revision++;
                KzmStore.WriteSurface(database, transaction, surface);
                transaction.Commit();
            }
            return moved;
        }

        /// <summary>Saves the appearance of the surface; with <paramref name="applyToPoints"/> redraws all its points.</summary>
        internal static int SaveAppearance(Database database, string surfaceId, PointAppearance appearance, bool applyToPoints, KzmSettings settings)
        {
            int count = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surface = ReadSurface(database, transaction, surfaceId);
                surface.Appearance = appearance.Clone();
                KzmStore.WriteSurface(database, transaction, surface);
                if (applyToPoints) count = PointBlocks.ApplyAppearance(database, transaction, surface, settings, settings.Decimals);
                transaction.Commit();
            }
            return count;
        }

        /// <summary>Внешняя граница триангуляции (null — без границы).</summary>
        internal static void SetBoundary(Database database, string surfaceId, string handle, KzmSettings settings)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surface = ReadSurface(database, transaction, surfaceId);
                if (handle != null) SurfaceModels.ReadOutline(database, transaction, handle, "Граница поверхности");
                surface.BoundaryHandle = handle;
                surface.Revision++;
                KzmStore.WriteSurface(database, transaction, surface);
                RefreshTriangles(database, transaction, surface, settings);
                transaction.Commit();
            }
        }

        /// <summary>Shows (rebuilds) or hides the triangles of the surface.</summary>
        internal static SurfaceModel ShowTriangles(Database database, string surfaceId, bool show, KzmSettings settings)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surface = ReadSurface(database, transaction, surfaceId);
                surface.ShowTriangles = show;
                SurfaceModel model = null;
                if (show)
                {
                    model = SurfaceModels.Build(database, transaction, surface, settings);
                    SurfaceModels.DrawTriangles(database, transaction, model);
                    surface.TrianglesFingerprint = model.Fingerprint;
                }
                else
                {
                    SurfaceModels.EraseTriangles(database, transaction, surface);
                    surface.TrianglesFingerprint = null;
                }
                KzmStore.WriteSurface(database, transaction, surface);
                transaction.Commit();
                return model;
            }
        }

        /// <summary>After a change of points: redraw the triangles if they are shown.</summary>
        internal static void RefreshTriangles(Database database, Transaction transaction, SurfaceInfo surface, KzmSettings settings)
        {
            if (!surface.ShowTriangles) return;
            var model = SurfaceModels.Build(database, transaction, surface, settings);
            SurfaceModels.DrawTriangles(database, transaction, model);
            surface.TrianglesFingerprint = model.Fingerprint;
            KzmStore.WriteSurface(database, transaction, surface);
        }

        private static void RefreshTriangles(Database database, Transaction transaction, IEnumerable<string> surfaceIds, KzmSettings settings)
        {
            var surfaces = KzmStore.ReadSurfaces(database, transaction);
            foreach (var id in surfaceIds.Distinct())
            {
                var surface = surfaces.FirstOrDefault(s => s.Id == id);
                if (surface == null) continue;
                surface.Revision++;
                KzmStore.WriteSurface(database, transaction, surface);
                RefreshTriangles(database, transaction, surface, settings);
            }
        }

        /// <summary>Deletes the surface record, its points and its triangles.</summary>
        internal static int DeleteSurface(Database database, string surfaceId, KzmSettings settings)
        {
            int erased = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var point in PointBlocks.Read(database, transaction, settings, surfaceId))
                {
                    var entity = (Entity)transaction.GetObject(point.Id, OpenMode.ForRead);
                    if (!Layers.IsUsable(database, transaction, entity.Layer, out string problem))
                        throw new KzmException("Точка " + point.Handle + ": " + problem + ". Поверхность не удалена.");
                    entity.UpgradeOpen();
                    entity.Erase();
                    erased++;
                }
                SurfaceModels.EraseTriangles(database, transaction, ReadSurface(database, transaction, surfaceId));
                KzmStore.DeleteLines(database, transaction, surfaceId);
                KzmStore.DeleteSurface(database, transaction, surfaceId);
                KzmStore.WriteConflicts(database, transaction, KzmStore.ReadConflicts(database, transaction));
                transaction.Commit();
            }
            return erased;
        }

        internal static void SetCurrent(Database database, string surfaceId)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                KzmStore.WriteCurrentSurfaceId(database, transaction, surfaceId);
                transaction.Commit();
            }
        }

        /// <summary>Writes the elevation and/or the number of one point (attributes and XData).</summary>
        internal static void SetPoint(Database database, ObjectId pointId, double? elevation, string number, KzmSettings settings)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var reference = (BlockReference)transaction.GetObject(pointId, OpenMode.ForRead);
                var data = PointBlocks.Decode(reference) ?? throw new KzmException("Объект не является точкой модуля.");
                if (!Layers.IsUsable(database, transaction, reference.Layer, out string problem)) throw new KzmException(problem + ".");
                reference.UpgradeOpen();
                if (elevation.HasValue) data.Elevation = elevation.Value;
                if (number != null) data.Number = number.Trim();
                reference.XData = PointBlocks.Encode(data);
                var surface = KzmStore.ReadSurfaces(database, transaction).FirstOrDefault(s => s.Id == data.SurfaceId);
                foreach (ObjectId id in reference.AttributeCollection)
                {
                    var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForWrite);
                    if (elevation.HasValue && string.Equals(attribute.Tag, PointBlocks.Tag, StringComparison.OrdinalIgnoreCase))
                        attribute.TextString = ElevationFormat.Format(elevation.Value, settings.Decimals);
                    if (number != null && string.Equals(attribute.Tag, PointBlocks.NumberTag, StringComparison.OrdinalIgnoreCase))
                    {
                        attribute.TextString = data.Number;
                        attribute.Invisible = string.IsNullOrEmpty(data.Number) || (surface != null && !surface.Appearance.ShowNumber);
                    }
                }
                if (number != null && surface != null)
                    PointBlocks.Apply(transaction, reference, data, surface.Appearance, settings.Decimals, false);
                RefreshTriangles(database, transaction, new[] { data.SurfaceId }, settings);
                transaction.Commit();
            }
        }

        /// <summary>Exact elevation into XData and the formatted one into the «ОТМЕТКА» attribute.</summary>
        internal static void WriteElevation(Transaction transaction, BlockReference reference, PointData data, double elevation, int decimals)
        {
            if (!reference.IsWriteEnabled) reference.UpgradeOpen();
            data.Elevation = elevation;
            reference.XData = PointBlocks.Encode(data);
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForWrite);
                if (string.Equals(attribute.Tag, PointBlocks.Tag, StringComparison.OrdinalIgnoreCase))
                    attribute.TextString = ElevationFormat.Format(elevation, decimals);
            }
        }

        /// <summary>Задание п. 3: временно исключить точки из триангуляции (или вернуть). Исключённые — серые.</summary>
        internal static int SetExcluded(Database database, IEnumerable<ObjectId> ids, bool excluded, KzmSettings settings)
        {
            int changed = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surfaces = KzmStore.ReadSurfaces(database, transaction);
                var touched = new HashSet<string>();
                foreach (var id in ids.Where(id => id.IsValid && !id.IsErased).Distinct())
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is BlockReference reference)) continue;
                    var data = PointBlocks.Decode(reference);
                    if (data == null || data.Excluded == excluded) continue;
                    if (!Layers.IsUsable(database, transaction, reference.Layer, out string problem))
                        throw new KzmException("Точка " + reference.Handle + ": " + problem + ".");
                    var surface = surfaces.FirstOrDefault(s => s.Id == data.SurfaceId);
                    // The elevation typed into the attribute (if any) is kept as the exact value.
                    var state = PointElevation.Resolve(data.Elevation, PointBlocks.ReadLabel(reference, transaction), settings.Decimals);
                    if (state.IsValid) data.Elevation = state.Value.Value;
                    data.Excluded = excluded;
                    reference.UpgradeOpen();
                    reference.XData = PointBlocks.Encode(data);
                    if (surface != null) PointBlocks.Apply(transaction, reference, data, surface.Appearance, settings.Decimals, false);
                    touched.Add(data.SurfaceId);
                    changed++;
                }
                RefreshTriangles(database, transaction, touched, settings);
                transaction.Commit();
            }
            return changed;
        }

        /// <summary>Сдвинуть отметки выбранных точек на Δ (например, перенос в другую систему высот участка).</summary>
        internal static int ShiftElevations(Database database, IEnumerable<ObjectId> ids, double delta, KzmSettings settings)
        {
            if (!Guard(delta)) throw new KzmException("Сдвиг отметок должен быть числом.");
            int changed = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var touched = new HashSet<string>();
                foreach (var id in ids.Where(id => id.IsValid && !id.IsErased).Distinct())
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is BlockReference reference)) continue;
                    var data = PointBlocks.Decode(reference);
                    if (data == null) continue;
                    var state = PointElevation.Resolve(data.Elevation, PointBlocks.ReadLabel(reference, transaction), settings.Decimals);
                    if (!state.IsValid) throw new KzmException("Точка " + reference.Handle + ": " + state.Problem + ". Исправьте отметку, затем сдвигайте.");
                    if (!Layers.IsUsable(database, transaction, reference.Layer, out string problem))
                        throw new KzmException("Точка " + reference.Handle + ": " + problem + ".");
                    WriteElevation(transaction, reference, data, state.Value.Value + delta, settings.Decimals);
                    touched.Add(data.SurfaceId);
                    changed++;
                }
                RefreshTriangles(database, transaction, touched, settings);
                transaction.Commit();
            }
            return changed;
        }

        private static bool Guard(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static int ErasePoints(Database database, IEnumerable<ObjectId> ids, KzmSettings settings)
        {
            int erased = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var touched = new HashSet<string>();
                foreach (var id in ids.Where(id => id.IsValid && !id.IsErased).Distinct())
                {
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    var data = PointBlocks.Decode(entity);
                    if (data == null) continue;
                    if (!Layers.IsUsable(database, transaction, entity.Layer, out string problem))
                        throw new KzmException("Точка " + entity.Handle + ": " + problem + ". Ничего не удалено.");
                    entity.UpgradeOpen();
                    entity.Erase();
                    touched.Add(data.SurfaceId);
                    erased++;
                }
                KzmStore.WriteConflicts(database, transaction, KzmStore.ReadConflicts(database, transaction));
                RefreshTriangles(database, transaction, touched, settings);
                transaction.Commit();
            }
            return erased;
        }

        /// <summary>Applies the user's decision to one pair of close points.</summary>
        internal static int ResolvePair(Database database, StoredPair pair, PairDecision decision, KzmSettings settings)
        {
            int erased = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var toErase = new List<ObjectId>();
                if (decision == PairDecision.KeepFirst || decision == PairDecision.DeleteBoth) toErase.Add(pair.Second);
                if (decision == PairDecision.KeepSecond || decision == PairDecision.DeleteBoth) toErase.Add(pair.First);
                var touched = new HashSet<string>();
                foreach (var id in toErase.Where(id => id.IsValid && !id.IsErased))
                {
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (!Layers.IsUsable(database, transaction, entity.Layer, out string problem))
                        throw new KzmException("Точка " + entity.Handle + ": " + problem + ".");
                    var data = PointBlocks.Decode(entity);
                    if (data != null) touched.Add(data.SurfaceId);
                    entity.UpgradeOpen();
                    entity.Erase();
                    erased++;
                }
                var remaining = KzmStore.ReadConflicts(database, transaction).Where(other => other.IsAlive && !other.Same(pair)).ToList();
                KzmStore.WriteConflicts(database, transaction, remaining);
                if (decision == PairDecision.KeepBoth)
                {
                    var kept = KzmStore.ReadKept(database, transaction).Where(other => other.IsAlive).ToList();
                    if (!kept.Any(other => other.Same(pair))) kept.Add(pair);
                    KzmStore.WriteKept(database, transaction, kept);
                }
                CheckMarks.Clear(database, transaction, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { pair.First.Handle.ToString(), pair.Second.Handle.ToString() });
                RefreshTriangles(database, transaction, touched, settings);
                transaction.Commit();
            }
            return erased;
        }

        internal static int SaveSettings(Database database, KzmSettings previous, KzmSettings updated)
        {
            int changed = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                KzmStore.WriteSettings(database, transaction, updated);
                if (updated.Decimals != previous.Decimals)
                    foreach (var surface in KzmStore.ReadSurfaces(database, transaction).Where(s => s.Appearance.IsSet))
                        changed += PointBlocks.ApplyAppearance(database, transaction, surface, updated, previous.Decimals);
                transaction.Commit();
            }
            return changed;
        }

        /// <summary>Appearance from a sample text: its style and height (the colours are kept).</summary>
        internal static PointAppearance FromSample(Entity sample, PointAppearance current)
        {
            var appearance = (current ?? new PointAppearance()).Clone();
            if (sample is MText mtext)
            {
                appearance.TextStyle = mtext.TextStyleName;
                appearance.TextHeight = mtext.TextHeight;
            }
            else if (sample is DBText text)
            {
                appearance.TextStyle = text.TextStyleName;
                appearance.TextHeight = text.Height;
            }
            else throw new KzmException("Образец оформления должен быть текстом (TEXT или MTEXT).");
            appearance.NumberHeight = null;
            appearance.SignSize = null;
            return appearance;
        }

        /// <summary>When nothing else is known: the current text style and TEXTSIZE of the drawing.</summary>
        internal static PointAppearance FromDrawing(Database database, Transaction transaction, PointAppearance current)
        {
            var appearance = (current ?? new PointAppearance()).Clone();
            var style = (TextStyleTableRecord)transaction.GetObject(database.Textstyle, OpenMode.ForRead);
            appearance.TextStyle = style.Name;
            appearance.TextHeight = style.TextSize > 0 ? style.TextSize : database.Textsize;
            return appearance;
        }

        /// <summary>
        /// Ручной ввод точек (задание п. 2.2): точка с привязками → отметка в командной строке →
        /// повтор до Esc; Enter без ввода — предыдущая отметка. Весь сеанс — одна отмена.
        /// </summary>
        internal static int CreatePointsByHand(Document document, string surfaceId, KzmSettings settings)
        {
            var database = document.Database;
            var editor = document.Editor;
            SurfaceInfo surface;
            List<SurfacePoint> existing;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                surface = ReadSurface(database, transaction, surfaceId);
                existing = PointBlocks.Read(database, transaction, settings, surfaceId);
                transaction.Commit();
            }
            if (!surface.Appearance.IsSet)
            {
                var options = new PromptEntityOptions("\nОформление подписей: укажите текст-образец (стиль и высота) или Enter — стиль чертежа: ")
                {
                    AllowNone = true,
                };
                options.SetRejectMessage("\nНужен TEXT или MTEXT.");
                options.AddAllowedClass(typeof(DBText), true);
                options.AddAllowedClass(typeof(MText), true);
                var sample = editor.GetEntity(options);
                if (sample.Status == PromptStatus.Cancel) return 0;
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    surface.Appearance = sample.Status == PromptStatus.OK
                        ? FromSample((Entity)transaction.GetObject(sample.ObjectId, OpenMode.ForRead), surface.Appearance)
                        : FromDrawing(database, transaction, surface.Appearance);
                    KzmStore.WriteSurface(database, transaction, surface);
                    transaction.Commit();
                }
            }
            int number = PointNumbering.Next(existing.Select(p => p.Number));
            double? last = null;
            int created = 0;
            editor.WriteMessage("\nКЗМ: точки в поверхность " + surface.Title + ". Esc — закончить.");
            while (true)
            {
                var pick = editor.GetPoint(new PromptPointOptions("\nТочка " + number + " — укажите положение (Esc — закончить): "));
                if (pick.Status != PromptStatus.OK) break;
                var world = pick.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                double elevation;
                while (true)
                {
                    string prompt = "\nОтметка точки " + number + (last.HasValue ? " <" + ElevationFormat.Format(last.Value, settings.Decimals) + ">" : string.Empty) + ": ";
                    var answer = editor.GetString(new PromptStringOptions(prompt) { AllowSpaces = false });
                    if (answer.Status != PromptStatus.OK) return Finish(document, surface, settings, created);
                    string text = answer.StringResult.Trim();
                    if (text.Length == 0 && last.HasValue) { elevation = last.Value; break; }
                    if (ElevationFormat.TryParse(text, out elevation)) break;
                    editor.WriteMessage("\nВведите отметку числом, например 145,32 (Enter — повторить предыдущую).");
                }
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                    Layers.Ensure(database, transaction, surface.Layer);
                    PointBlocks.EnsureRegApp(database, transaction, PointBlocks.AppName);
                    var definition = PointBlocks.EnsureDefinition(database, transaction);
                    var data = new PointData
                    {
                        SurfaceId = surface.Id, Elevation = elevation, Source = SourceKind.Manual, XyMode = XyMode.Manual,
                        Number = number.ToString(CultureInfo.InvariantCulture),
                    };
                    PointBlocks.Create(transaction, space, definition, new XY(world.X, world.Y), surface.Layer, data, surface.Appearance, settings.Decimals);
                    transaction.Commit();
                }
                last = elevation;
                number++;
                created++;
            }
            return Finish(document, surface, settings, created);
        }

        private static int Finish(Document document, SurfaceInfo surface, KzmSettings settings, int created)
        {
            if (created == 0) return 0;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                RefreshTriangles(document.Database, transaction, new[] { surface.Id }, settings);
                transaction.Commit();
            }
            document.Editor.WriteMessage("\nКЗМ: создано точек: " + created + ". Отменить все — U.");
            return created;
        }
    }
}
