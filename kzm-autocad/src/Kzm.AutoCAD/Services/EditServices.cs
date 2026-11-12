using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    internal enum PairDecision { KeepFirst, KeepSecond, KeepBoth, DeleteBoth }

    /// <summary>
    /// Changes of surfaces, points and pairs. Every method is called inside one
    /// KZM_RUN command (one UNDO step) and uses one transaction.
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

        /// <summary>Rename / change type / change layer; optionally moves existing points to the new layer.</summary>
        internal static int UpdateSurface(Database database, SurfaceInfo surface, bool movePoints, KzmSettings settings)
        {
            int moved = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
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

        /// <summary>Deletes the surface record and all its points.</summary>
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

        /// <summary>Writes the elevation into the attribute and XData of one point.</summary>
        internal static void SetElevation(Database database, ObjectId pointId, double elevation, KzmSettings settings)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var reference = (BlockReference)transaction.GetObject(pointId, OpenMode.ForRead);
                var data = PointBlocks.Decode(reference) ?? throw new KzmException("Объект не является точкой модуля.");
                if (!Layers.IsUsable(database, transaction, reference.Layer, out string problem)) throw new KzmException(problem + ".");
                reference.UpgradeOpen();
                data.Elevation = elevation;
                reference.XData = PointBlocks.Encode(data);
                foreach (ObjectId id in reference.AttributeCollection)
                {
                    var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForRead);
                    if (!string.Equals(attribute.Tag, PointBlocks.Tag, StringComparison.OrdinalIgnoreCase)) continue;
                    attribute.UpgradeOpen();
                    attribute.TextString = ElevationFormat.Format(elevation, settings.Decimals);
                }
                KzmStore.TouchSurface(database, transaction, data.SurfaceId);
                transaction.Commit();
            }
        }

        internal static int ErasePoints(Database database, IEnumerable<ObjectId> ids)
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
                foreach (var surfaceId in touched) KzmStore.TouchSurface(database, transaction, surfaceId);
                KzmStore.WriteConflicts(database, transaction, KzmStore.ReadConflicts(database, transaction));
                transaction.Commit();
            }
            return erased;
        }

        /// <summary>Applies the user's decision to one pair of close points.</summary>
        internal static int ResolvePair(Database database, StoredPair pair, PairDecision decision)
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
                foreach (var surfaceId in touched) KzmStore.TouchSurface(database, transaction, surfaceId);
                transaction.Commit();
            }
            return erased;
        }

        internal static int SaveSettings(Database database, KzmSettings previous, KzmSettings updated)
        {
            bool sizesChanged = updated.ScaleDenominator != previous.ScaleDenominator ||
                                updated.LabelHeightMm != previous.LabelHeightMm || updated.PointSizeMm != previous.PointSizeMm ||
                                updated.Decimals != previous.Decimals;
            int resized = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                KzmStore.WriteSettings(database, transaction, updated);
                if (sizesChanged && updated.ValidateDrawing().Count == 0 && PointBlocks.Read(database, transaction, updated).Count > 0)
                    resized = PointBlocks.ApplySizes(database, transaction, updated, previous.Decimals);
                transaction.Commit();
            }
            return resized;
        }
    }
}
