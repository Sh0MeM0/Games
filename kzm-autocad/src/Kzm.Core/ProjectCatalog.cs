using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>Проект в каком-то чертеже (для списка «Проекты в других чертежах»).</summary>
    public sealed class CatalogEntry
    {
        public string ProjectId { get; set; }
        public string Name { get; set; }
        /// <summary>Full path of the DWG file.</summary>
        public string Path { get; set; }
        /// <summary>When the drawing was saved or read last, «yyyy-MM-dd HH:mm».</summary>
        public string Seen { get; set; }
        public int Surfaces { get; set; }
        public int Cartograms { get; set; }

        public CatalogEntry Clone() => (CatalogEntry)MemberwiseClone();
    }

    /// <summary>
    /// Список проектов всех чертежей пользователя (замечание 35): проекты хранятся в своих DWG, а этот список —
    /// в файле пользователя, поэтому их видно и в новом чертеже, и любой из них можно перенести туда. Список
    /// пополняется, когда чертёж с проектами сохраняют или открывают. Одна строка — один проект одного чертежа:
    /// id, название, путь, дата, число поверхностей и картограмм через табуляцию.
    /// </summary>
    public static class ProjectCatalog
    {
        private const string Header = "# КЗМ: проекты в чертежах (обновляется модулем)";

        public static List<CatalogEntry> Parse(string text)
        {
            var result = new List<CatalogEntry>();
            foreach (var raw in (text ?? string.Empty).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3 || parts[0].Length == 0 || parts[2].Length == 0) continue;
                result.Add(new CatalogEntry
                {
                    ProjectId = parts[0],
                    Name = parts[1],
                    Path = parts[2],
                    Seen = parts.Length > 3 ? parts[3] : string.Empty,
                    Surfaces = parts.Length > 4 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 0,
                    Cartograms = parts.Length > 5 && int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 0,
                });
            }
            return result;
        }

        public static string Format(IEnumerable<CatalogEntry> entries)
        {
            var lines = new List<string> { Header };
            foreach (var e in entries)
                lines.Add(string.Join("\t", Clean(e.ProjectId), Clean(e.Name), Clean(e.Path), Clean(e.Seen),
                    e.Surfaces.ToString(CultureInfo.InvariantCulture), e.Cartograms.ToString(CultureInfo.InvariantCulture)));
            return string.Join("\n", lines) + "\n";
        }

        private static string Clean(string value) => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        public static bool SamePath(string a, string b) =>
            string.Equals((a ?? string.Empty).Replace('/', '\\').TrimEnd('\\'), (b ?? string.Empty).Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The entries of one drawing become its current projects (removed projects go, new ones come); entries of other
        /// drawings stay. Returns true when something changed (only the date — counts as no change).
        /// </summary>
        public static bool Update(List<CatalogEntry> entries, string path, IEnumerable<CatalogEntry> projects, string seen)
        {
            var now = projects.Select(p =>
            {
                var entry = p.Clone();
                entry.Path = path;
                entry.Seen = seen;
                return entry;
            }).ToList();
            var before = entries.Where(e => SamePath(e.Path, path)).ToList();
            bool same = before.Count == now.Count && now.All(n => before.Any(b => b.ProjectId == n.ProjectId && b.Name == n.Name &&
                                                                              b.Surfaces == n.Surfaces && b.Cartograms == n.Cartograms));
            if (same) return false;
            entries.RemoveAll(e => SamePath(e.Path, path));
            entries.AddRange(now);
            return true;
        }

        /// <summary>Projects of other drawings, newest first (the same project in several files is shown for each file).</summary>
        public static List<CatalogEntry> Others(IEnumerable<CatalogEntry> entries, string currentPath) =>
            entries.Where(e => !SamePath(e.Path, currentPath))
                .OrderByDescending(e => e.Seen, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
