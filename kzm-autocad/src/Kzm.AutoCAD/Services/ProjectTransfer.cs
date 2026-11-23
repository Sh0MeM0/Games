using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    /// <summary>
    /// Перенос проекта из другого чертежа (замечание 35). Из сохранённого файла в текущий чертёж копируется всё, что
    /// относится к проекту: точки поверхностей, границы поверхностей и картограмм, блок треугольников, построенные
    /// картограммы (сетка, отметки, выноски, объёмы, штриховка, таблицы, площади) и ведомости, а также записи модуля —
    /// проект, поверхности, структурные линии, картограммы. Объекты копирует AutoCAD (WblockCloneObjects), у копий новые
    /// ручки, поэтому все ссылки записей на ручки переписываются. Исходный чертёж не меняется. Одна команда — одна отмена.
    /// </summary>
    internal static class ProjectTransfer
    {
        /// <summary>Projects in a saved DWG (with the number of surfaces and cartograms).</summary>
        internal static List<CatalogEntry> ReadProjects(string path)
        {
            using (var source = OpenFile(path))
            using (var transaction = source.TransactionManager.StartOpenCloseTransaction())
            {
                var projects = ProjectCatalogFile.ProjectsOf(source, transaction);
                transaction.Commit();
                return projects;
            }
        }

        private static Database OpenFile(string path)
        {
            if (!File.Exists(path)) throw new KzmException("Чертёж «" + path + "» не найден (перемещён или удалён?).");
            var database = new Database(false, true);
            try
            {
                database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                database.CloseInput(true);
                return database;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception exception)
            {
                database.Dispose();
                throw new KzmException("Чертёж «" + Path.GetFileName(path) + "» не открывается для чтения: " + exception.Message + ".");
            }
        }

        /// <summary>Copies the project into the drawing of <paramref name="destination"/>; returns what was copied (for the message).</summary>
        internal static string Import(Document destination, string path, string projectId)
        {
            var target = destination.Database;
            if (ProjectCatalogFile.IsDrawingFile(target.Filename) && ProjectCatalog.SamePath(Path.GetFullPath(target.Filename), Path.GetFullPath(path)))
                throw new KzmException("Это и есть текущий чертёж: проект уже здесь, откройте его в списке «Проекты чертежа».");
            using (var source = OpenFile(path))
            {
                // ---- 1. The project in the source drawing ----
                ProjectInfo project;
                List<SurfaceInfo> surfaces;
                List<CartogramInfo> cartograms;
                var lines = new Dictionary<string, List<StructureLine>>(StringComparer.Ordinal);
                var tracked = new Dictionary<string, List<ObjectId>>(StringComparer.Ordinal);
                var ids = new HashSet<ObjectId>();
                int points = 0, elsewhere = 0;
                using (var transaction = source.TransactionManager.StartOpenCloseTransaction())
                {
                    project = KzmStore.ReadProjects(source, transaction).FirstOrDefault(p => p.Id == projectId)
                              ?? throw new KzmException("В чертеже «" + Path.GetFileName(path) + "» такого проекта больше нет (удалён или переименован и не сохранён?).");
                    var settings = KzmStore.ReadSettings(source, transaction);
                    surfaces = KzmStore.ReadSurfaces(source, transaction).Where(s => ProjectInfo.Of(s.ProjectId) == project.Id).ToList();
                    cartograms = KzmStore.ReadCartograms(source, transaction).Where(c => ProjectInfo.Of(c.ProjectId) == project.Id).ToList();
                    foreach (var surface in surfaces) lines[surface.Id] = KzmStore.ReadLines(source, transaction, surface.Id);
                    var modelSpace = SymbolUtilityServices.GetBlockModelSpaceId(source);
                    // Everything of the project lies in model space (one owner is what WblockCloneObjects needs); objects put on a
                    // layout (e.g. a statement on a sheet) are not copied and are counted for the message.
                    void Take(ObjectId id)
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased) return;
                        if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity && entity.OwnerId == modelSpace) ids.Add(id);
                        else elsewhere++;
                    }
                    var surfaceIds = new HashSet<string>(surfaces.Select(s => s.Id), StringComparer.Ordinal);
                    foreach (var point in PointBlocks.Read(source, transaction, settings, id => surfaceIds.Contains(id)))
                    {
                        Take(point.Id);
                        points++;
                    }
                    foreach (var surface in surfaces)
                    {
                        foreach (var handle in surface.BoundaryHandles) Take(SurfaceModels.FromHandle(source, handle));
                        Take(SurfaceModels.FromHandle(source, surface.TrianglesBlockHandle));
                    }
                    foreach (var cartogram in cartograms)
                    {
                        foreach (var handle in cartogram.BoundaryHandles) Take(SurfaceModels.FromHandle(source, handle));
                        foreach (var key in new[] { cartogram.Id, cartogram.Id + CartogramService.StatementsSuffix })
                        {
                            var list = KzmStore.ReadCartogramObjects(source, transaction, key);
                            if (list == null) continue;
                            tracked[key] = list;
                            foreach (var id in list) Take(id);
                        }
                    }
                    transaction.Commit();
                }

                // ---- 2. Not twice into the same drawing ----
                string newId = project.Id == ProjectInfo.LegacyId ? Guid.NewGuid().ToString("N") : project.Id;
                string name = project.Name;
                using (var transaction = target.TransactionManager.StartOpenCloseTransaction())
                {
                    var existing = KzmStore.ReadProjects(target, transaction);
                    if (existing.Any(p => p.Id == newId))
                        throw new KzmException("Проект «" + project.Name + "» уже есть в этом чертеже (его уже переносили) — откройте его в списке «Проекты чертежа».");
                    var surfaceIds = new HashSet<string>(KzmStore.ReadSurfaces(target, transaction).Select(s => s.Id), StringComparer.Ordinal);
                    var cartogramIds = new HashSet<string>(KzmStore.ReadCartograms(target, transaction).Select(c => c.Id), StringComparer.Ordinal);
                    if (surfaces.Any(s => surfaceIds.Contains(s.Id)) || cartograms.Any(c => cartogramIds.Contains(c.Id)))
                        throw new KzmException("Поверхности или картограммы проекта «" + project.Name + "» уже есть в этом чертеже (проект уже переносили).");
                    for (int n = 2; existing.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)); n++)
                        name = project.Name + " (" + n + ")";
                    transaction.Commit();
                }

                // ---- 3. The objects ----
                // The copies are not edits of the user: the watcher of the node labels stays quiet until the records are written
                // (as when a cartogram is drawn), so it does not go through every copied node afterwards.
                using var quiet = NodeSync.Quiet();
                var mapping = new IdMapping();
                if (ids.Count > 0)
                    source.WblockCloneObjects(new ObjectIdCollection(ids.ToArray()), SymbolUtilityServices.GetBlockModelSpaceId(target), mapping,
                        DuplicateRecordCloning.Ignore, false);
                var copies = new Dictionary<ObjectId, ObjectId>();
                var handles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (IdPair pair in mapping)
                {
                    if (!pair.IsCloned || pair.Value.IsNull) continue;
                    copies[pair.Key] = pair.Value;
                    handles[pair.Key.Handle.ToString()] = pair.Value.Handle.ToString();
                }
                string Map(string handle) => !string.IsNullOrEmpty(handle) && handles.TryGetValue(handle, out var copy) ? copy : null;

                // ---- 4. The records, with the handles of the copies ----
                using (var transaction = target.TransactionManager.StartTransaction())
                {
                    KzmStore.WriteProject(target, transaction, new ProjectInfo { Id = newId, Name = name, Created = project.Created });
                    foreach (var surface in surfaces)
                    {
                        if (project.Id == ProjectInfo.LegacyId) surface.ProjectId = newId;
                        surface.BoundaryHandles = surface.BoundaryHandles.Select(Map).Where(h => h != null).ToList();
                        surface.TrianglesBlockHandle = Map(surface.TrianglesBlockHandle);
                        KzmStore.WriteSurface(target, transaction, surface);
                        foreach (var line in lines[surface.Id])
                        {
                            var mapped = line.PointHandles.Select(Map).Where(h => h != null).ToList();
                            line.PointHandles.Clear();
                            line.PointHandles.AddRange(mapped);
                            line.SourceHandle = Map(line.SourceHandle);
                            KzmStore.WriteLine(target, transaction, surface.Id, line);
                        }
                    }
                    foreach (var cartogram in cartograms)
                    {
                        if (project.Id == ProjectInfo.LegacyId) cartogram.ProjectId = newId;
                        cartogram.BoundaryHandles = cartogram.BoundaryHandles.Select(Map).Where(h => h != null).ToList();
                        KzmStore.WriteCartogram(target, transaction, cartogram);
                        foreach (var key in new[] { cartogram.Id, cartogram.Id + CartogramService.StatementsSuffix })
                            if (tracked.TryGetValue(key, out var list))
                                KzmStore.WriteCartogramObjects(target, transaction, key, list.Where(copies.ContainsKey).Select(id => copies[id]).ToList());
                    }
                    // A node block keeps the handle of its leader as text: it now points to the copy of the leader.
                    foreach (var copy in tracked.Where(t => !t.Key.EndsWith(CartogramService.StatementsSuffix, StringComparison.Ordinal))
                                 .SelectMany(t => t.Value).Where(copies.ContainsKey).Select(id => copies[id]))
                        if (copy.ObjectClass.DxfName == "INSERT" && transaction.GetObject(copy, OpenMode.ForRead) is BlockReference block) RemapLeader(block, Map);
                    KzmStore.WriteCurrentProjectId(target, transaction, newId);
                    if (surfaces.Count > 0) KzmStore.WriteCurrentSurfaceId(target, transaction, surfaces[0].Id);
                    transaction.Commit();
                }

                string message = "Проект «" + name + "» перенесён из «" + Path.GetFileName(path) + "»: поверхностей " + surfaces.Count + ", точек " + points +
                                 ", картограмм " + cartograms.Count + ".";
                if (elsewhere > 0) message += " Не перенесено объектов вне пространства модели (например, ведомость на листе): " + elsewhere + ".";
                if (cartograms.Any(c => c.Built)) message += " Картограммы перенесены вместе с оформлением; если панель пометит их устаревшими, " +
                                                            "постройте заново — сдвинутые вами подписи и выноски останутся на месте.";
                return message;
            }
        }

        private static void RemapLeader(BlockReference block, Func<string, string> map)
        {
            TypedValue[] values;
            using (var xdata = block.GetXDataForApplication(CartogramService.App)) values = xdata?.AsArray();
            if (values == null || values.Length <= NodeLeader.LeaderIndex || !(values[NodeLeader.LeaderIndex].Value is string handle) || handle.Length == 0) return;
            values[NodeLeader.LeaderIndex] = new TypedValue((int)DxfCode.ExtendedDataAsciiString, map(handle) ?? string.Empty);
            block.UpgradeOpen();
            block.XData = new ResultBuffer(values);
        }
    }
}
