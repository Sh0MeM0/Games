using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.Core;

namespace Kzm.AutoCAD.Drawing
{
    /// <summary>
    /// Файл списка проектов всех чертежей: %APPDATA%\Sh0MeM0\Kzm\projects.txt (замечание 35). Чертёж записывает сюда свои
    /// проекты, когда его сохраняют или открывают; в любом другом чертеже, в том числе новом, список виден на странице
    /// «Проекты». Сами данные проектов остаются в их DWG.
    /// </summary>
    internal static class ProjectCatalogFile
    {
        internal static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sh0MeM0", "Kzm", "projects.txt");

        private static List<CatalogEntry> cached;
        private static DateTime stamp;

        /// <summary>The list as it is in the file (read again only when the file changed, e.g. by another AutoCAD).</summary>
        internal static List<CatalogEntry> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return cached = new List<CatalogEntry>();
                var written = File.GetLastWriteTimeUtc(FilePath);
                if (cached != null && written == stamp) return cached;
                cached = ProjectCatalog.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                stamp = written;
                return cached;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return cached ?? new List<CatalogEntry>();
            }
        }

        private static void Save(List<CatalogEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, ProjectCatalog.Format(entries), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(temporary, FilePath);
                cached = entries;
                stamp = File.GetLastWriteTimeUtc(FilePath);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // The list is a convenience: the drawing itself keeps its projects.
            }
        }

        /// <summary>A saved DWG file (not a template of a new drawing, not a drawing that was never saved).</summary>
        internal static bool IsDrawingFile(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Projects of a drawing with the number of surfaces and cartograms (read inside an open transaction).</summary>
        internal static List<CatalogEntry> ProjectsOf(Database database, Transaction transaction)
        {
            var surfaces = KzmStore.ReadSurfaces(database, transaction);
            var cartograms = KzmStore.ReadCartograms(database, transaction);
            return KzmStore.ReadProjects(database, transaction).Select(p => new CatalogEntry
            {
                ProjectId = p.Id,
                Name = p.Name,
                Surfaces = surfaces.Count(s => ProjectInfo.Of(s.ProjectId) == p.Id),
                Cartograms = cartograms.Count(c => ProjectInfo.Of(c.ProjectId) == p.Id),
            }).ToList();
        }

        /// <summary>Writes the projects of the drawing saved or opened at <paramref name="path"/> into the list.</summary>
        internal static void Register(string path, IEnumerable<CatalogEntry> projects)
        {
            if (!IsDrawingFile(path)) return;
            var entries = Load().Select(e => e.Clone()).ToList();
            if (ProjectCatalog.Update(entries, Path.GetFullPath(path), projects, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
                Save(entries);
        }

        internal static void Register(Database database, string path)
        {
            if (!IsDrawingFile(path)) return;
            try
            {
                List<CatalogEntry> projects;
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    projects = ProjectsOf(database, transaction);
                    transaction.Commit();
                }
                Register(path, projects);
            }
            catch (Exception exception) when (exception is KzmException || exception is Autodesk.AutoCAD.Runtime.Exception)
            {
                // A drawing whose module data cannot be read is simply not listed.
            }
        }

        /// <summary>«Убрать из списка»: the entry goes (the drawing and its project stay as they are).</summary>
        internal static void Forget(CatalogEntry entry)
        {
            var entries = Load().Select(e => e.Clone()).ToList();
            int removed = entries.RemoveAll(e => e.ProjectId == entry.ProjectId && ProjectCatalog.SamePath(e.Path, entry.Path));
            if (removed > 0) Save(entries);
        }
    }
}
