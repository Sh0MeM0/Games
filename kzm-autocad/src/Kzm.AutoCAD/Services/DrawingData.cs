using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    internal sealed class SurfaceSummary
    {
        internal SurfaceInfo Info { get; set; }
        internal int Points { get; set; }
        internal int BrokenPoints { get; set; }
        internal int PendingPairs { get; set; }
        internal bool IsCurrent { get; set; }
        /// <summary>Triangles are shown but the points changed since (MOVE/ERASE/COPY or attribute edit).</summary>
        internal bool TrianglesOutdated { get; set; }
    }

    internal sealed class CartogramSummary
    {
        internal CartogramInfo Info { get; set; }
        internal bool Outdated { get; set; }
    }

    internal sealed class DrawingSnapshot
    {
        internal KzmSettings Settings { get; set; } = new KzmSettings();
        internal List<SurfaceSummary> Surfaces { get; } = new List<SurfaceSummary>();
        internal List<CartogramSummary> Cartograms { get; } = new List<CartogramSummary>();
        internal string CurrentId { get; set; }
        internal string Error { get; set; }

        internal SurfaceSummary Find(string id) => Surfaces.FirstOrDefault(surface => surface.Info.Id == id);

        internal CartogramSummary FindCartogram(string id) => Cartograms.FirstOrDefault(c => c.Info.Id == id);
    }

    /// <summary>A pending pair of close points, both alive.</summary>
    internal sealed class PairInfo
    {
        internal StoredPair Pair { get; set; }
        internal SurfacePoint First { get; set; }
        internal SurfacePoint Second { get; set; }
        internal double Distance => First.Position.DistanceTo(Second.Position);
    }

    /// <summary>Read-only access for the panel. Reads lock the document (modeless context).</summary>
    internal static class DrawingData
    {
        internal static DrawingSnapshot Read(Document document)
        {
            var snapshot = new DrawingSnapshot();
            if (document == null) return snapshot;
            var database = document.Database;
            try
            {
                using (document.LockDocument(DocumentLockMode.Read, null, null, false))
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    snapshot.Settings = KzmStore.ReadSettings(database, transaction);
                    snapshot.CurrentId = KzmStore.ReadCurrentSurfaceId(database, transaction);
                    var points = PointBlocks.Read(database, transaction, snapshot.Settings);
                    var bySurface = points.GroupBy(point => point.Data.SurfaceId).ToDictionary(group => group.Key, group => group.ToList());
                    var pointSurface = points.ToDictionary(point => point.Id, point => point.Data.SurfaceId);
                    var pairs = KzmStore.ReadConflicts(database, transaction).Where(pair => pair.IsAlive).ToList();
                    var surfaces = KzmStore.ReadSurfaces(database, transaction);
                    foreach (var surface in surfaces)
                    {
                        bySurface.TryGetValue(surface.Id, out var own);
                        own = own ?? new List<SurfacePoint>();
                        bool trianglesOutdated = surface.ShowTriangles &&
                            surface.TrianglesFingerprint != PointBlocks.FingerprintOf(own) + "|" +
                                SurfaceModels.OutlineFingerprint(database, transaction, surface.BoundaryHandle);
                        snapshot.Surfaces.Add(new SurfaceSummary
                        {
                            Info = surface,
                            Points = own.Count,
                            BrokenPoints = own.Count(point => !point.Elevation.IsValid),
                            PendingPairs = pairs.Count(pair => Belongs(pair, surface.Id, pointSurface)),
                            IsCurrent = surface.Id == snapshot.CurrentId,
                            TrianglesOutdated = trianglesOutdated,
                        });
                    }
                    foreach (var cartogram in KzmStore.ReadCartograms(database, transaction))
                    {
                        bool outdated = cartogram.Built && cartogram.BuiltFingerprint != CartogramService.FingerprintOf(database, transaction,
                            cartogram, surfaces, id => bySurface.TryGetValue(id, out var list) ? list : new List<SurfacePoint>());
                        snapshot.Cartograms.Add(new CartogramSummary { Info = cartogram, Outdated = outdated });
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                snapshot.Error = exception.Message;
            }
            return snapshot;
        }

        private static bool Belongs(StoredPair pair, string surfaceId, Dictionary<ObjectId, string> pointSurface) =>
            (pointSurface.TryGetValue(pair.First, out var first) && first == surfaceId) ||
            (pointSurface.TryGetValue(pair.Second, out var second) && second == surfaceId);

        internal static List<SurfacePoint> ReadPoints(Document document, KzmSettings settings, string surfaceId)
        {
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var points = PointBlocks.Read(document.Database, transaction, settings, surfaceId);
                transaction.Commit();
                return points;
            }
        }

        internal static List<PairInfo> ReadPairs(Document document, KzmSettings settings, string surfaceId)
        {
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var points = PointBlocks.Read(document.Database, transaction, settings).ToDictionary(point => point.Id, point => point);
                var result = KzmStore.ReadConflicts(document.Database, transaction)
                    .Where(pair => pair.IsAlive && points.ContainsKey(pair.First) && points.ContainsKey(pair.Second))
                    .Select(pair => new PairInfo { Pair = pair, First = points[pair.First], Second = points[pair.Second] })
                    .Where(pair => surfaceId == null || pair.First.Data.SurfaceId == surfaceId || pair.Second.Data.SurfaceId == surfaceId)
                    .ToList();
                transaction.Commit();
                return result;
            }
        }

        /// <summary>Layers of the current space that hold TEXT/MTEXT, with counts.</summary>
        internal static List<KeyValuePair<string, int>> TextLayers(Document document)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass.DxfName != "TEXT" && id.ObjectClass.DxfName != "MTEXT") continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    counts.TryGetValue(entity.Layer, out int count);
                    counts[entity.Layer] = count + 1;
                }
                transaction.Commit();
            }
            return counts.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static List<ObjectId> TextsOnLayers(Document document, ICollection<string> layers)
        {
            var result = new List<ObjectId>();
            if (layers.Count == 0) return result;
            var wanted = new HashSet<string>(layers, StringComparer.OrdinalIgnoreCase);
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass.DxfName != "TEXT" && id.ObjectClass.DxfName != "MTEXT") continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (wanted.Contains(entity.Layer)) result.Add(id);
                }
                transaction.Commit();
            }
            return result;
        }

        internal static List<string> TextStyles(Document document)
        {
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (TextStyleTable)transaction.GetObject(document.Database.TextStyleTableId, OpenMode.ForRead);
                var names = table.Cast<ObjectId>()
                    .Select(id => (TextStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead))
                    .Where(style => !style.IsShapeFile && !style.Name.Contains("|") && style.Name.Length > 0)
                    .Select(style => style.Name)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
                transaction.Commit();
                return names;
            }
        }

        internal static List<string> Layers(Document document)
        {
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead);
                var names = table.Cast<ObjectId>()
                    .Select(id => ((LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name)
                    .Where(name => !name.Contains("|"))
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
                transaction.Commit();
                return names;
            }
        }
    }
}
