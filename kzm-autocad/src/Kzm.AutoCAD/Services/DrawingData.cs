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
        internal int ExcludedPoints { get; set; }
        internal List<StructureLine> Lines { get; set; } = new List<StructureLine>();
        internal int StructureLines => Lines.Count(line => line.Kind == StructureLineKind.Line);
        internal int Flips => Lines.Count(line => line.Kind == StructureLineKind.Flip);
    }

    internal sealed class CartogramSummary
    {
        internal CartogramInfo Info { get; set; }
        internal bool Outdated { get; set; }
        /// <summary>Labels whose elevations the user typed on the drawing (taken into the surfaces by «Пересчитать»).</summary>
        internal int LabelEdits { get; set; }
    }

    internal sealed class DrawingSnapshot
    {
        internal KzmSettings Settings { get; set; } = new KzmSettings();
        internal List<SurfaceSummary> Surfaces { get; } = new List<SurfaceSummary>();
        internal List<CartogramSummary> Cartograms { get; } = new List<CartogramSummary>();
        internal string CurrentId { get; set; }
        internal string Error { get; set; }
        internal List<ProjectInfo> Projects { get; } = new List<ProjectInfo>();
        /// <summary>The open project (null — none: the panel offers to create or open one).</summary>
        internal ProjectInfo Project { get; set; }

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
                    snapshot.Projects.AddRange(KzmStore.ReadProjects(database, transaction));
                    string projectId = KzmStore.ReadCurrentProjectId(database, transaction);
                    snapshot.Project = snapshot.Projects.FirstOrDefault(project => project.Id == projectId);
                    snapshot.CurrentId = KzmStore.ReadCurrentSurfaceId(database, transaction);
                    var points = PointBlocks.Read(database, transaction, snapshot.Settings);
                    var bySurface = points.GroupBy(point => point.Data.SurfaceId).ToDictionary(group => group.Key, group => group.ToList());
                    var pointSurface = points.ToDictionary(point => point.Id, point => point.Data.SurfaceId);
                    var pairs = KzmStore.ReadConflicts(database, transaction).Where(pair => pair.IsAlive).ToList();
                    // Only the open project is shown in the panel.
                    var surfaces = KzmStore.ReadSurfaces(database, transaction)
                        .Where(surface => snapshot.Project != null && ProjectInfo.Of(surface.ProjectId) == snapshot.Project.Id).ToList();
                    var lines = KzmStore.ReadAllLines(database, transaction);
                    // Each surface is fingerprinted once and reused by all cartograms.
                    var signatures = new Dictionary<string, SurfaceSignature>(StringComparer.Ordinal);
                    foreach (var surface in surfaces)
                    {
                        bySurface.TryGetValue(surface.Id, out var own);
                        own = own ?? new List<SurfacePoint>();
                        lines.TryGetValue(surface.Id, out var ownLines);
                        ownLines = ownLines ?? new List<StructureLine>();
                        var signature = SurfaceModels.SignatureOf(database, transaction, surface, own, ownLines);
                        signatures[surface.Id] = signature;
                        snapshot.Surfaces.Add(new SurfaceSummary
                        {
                            Info = surface,
                            Points = own.Count,
                            BrokenPoints = own.Count(point => !point.Elevation.IsValid),
                            ExcludedPoints = own.Count(point => point.Data.Excluded),
                            PendingPairs = pairs.Count(pair => Belongs(pair, surface.Id, pointSurface)),
                            IsCurrent = surface.Id == snapshot.CurrentId,
                            TrianglesOutdated = surface.ShowTriangles && surface.TrianglesFingerprint != signature.ForTriangles(),
                            Lines = ownLines,
                        });
                    }
                    foreach (var cartogram in KzmStore.ReadCartograms(database, transaction)
                                 .Where(c => snapshot.Project != null && ProjectInfo.Of(c.ProjectId) == snapshot.Project.Id))
                    {
                        int edits = cartogram.Built ? CartogramService.CountLabelEdits(database, transaction, cartogram.Id) : 0;
                        bool outdated = cartogram.Built && (edits > 0 || cartogram.OldDrawing || cartogram.BuiltFingerprint != CartogramService.FingerprintOf(database, transaction,
                            cartogram, surfaces, id => signatures[id]));
                        snapshot.Cartograms.Add(new CartogramSummary { Info = cartogram, Outdated = outdated, LabelEdits = edits });
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

        internal static readonly string[] TextTypes = { "TEXT", "MTEXT" };
        internal static readonly string[] PointTypes = { "POINT" };
        internal static readonly string[] BlockTypes = { "INSERT" };

        /// <summary>Layers of the current space that hold objects of the given types, with counts (module points are not counted).</summary>
        internal static List<KeyValuePair<string, int>> EntityLayers(Document document, ICollection<string> types)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (!types.Contains(id.ObjectClass.DxfName)) continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (entity is BlockReference && PointBlocks.Decode(entity) != null) continue;
                    counts.TryGetValue(entity.Layer, out int count);
                    counts[entity.Layer] = count + 1;
                }
                transaction.Commit();
            }
            return counts.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static List<KeyValuePair<string, int>> TextLayers(Document document) => EntityLayers(document, TextTypes);

        internal static List<ObjectId> EntitiesOnLayers(Document document, ICollection<string> layers, ICollection<string> types)
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
                    if (!types.Contains(id.ObjectClass.DxfName)) continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (!wanted.Contains(entity.Layer)) continue;
                    if (entity is BlockReference && PointBlocks.Decode(entity) != null) continue;
                    result.Add(id);
                }
                transaction.Commit();
            }
            return result;
        }

        internal static List<ObjectId> TextsOnLayers(Document document, ICollection<string> layers) => EntitiesOnLayers(document, layers, TextTypes);

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
