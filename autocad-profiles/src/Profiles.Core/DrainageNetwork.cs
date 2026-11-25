using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Profiles.Core
{
    /// <summary>
    /// Collectors and drains of one project that flow into each other. A label in
    /// «Впадает коллектор» of a drainage profile that names another drainage profile
    /// of the project — by its number («3-2» for «К-р 3-2'») or full name — makes it a
    /// tributary at that row's station. The tributary then starts at the main
    /// collector: its outlet (its first ordinate) takes the main collector's drain
    /// elevation there — its whole design line moves with it, slopes kept — and
    /// the surface there, which is the same point of the ground.
    /// </summary>
    public static class DrainageNetwork
    {
        public sealed class Link
        {
            public string MainProfileId { get; internal set; }
            public string MainName { get; internal set; }
            public string MainNumber { get; internal set; }
            public double Station { get; internal set; }
            public string TributaryProfileId { get; internal set; }
            public string TributaryName { get; internal set; }
            public string TributaryNumber { get; internal set; }
        }

        /// <summary>What one synchronisation changed in a tributary, for the editor's status line.</summary>
        public sealed class Change
        {
            public string ProfileId { get; internal set; }
            public string Name { get; internal set; }
            public string MainName { get; internal set; }
            public double Station { get; internal set; }
            /// <summary>Outlet drain elevation after the change; null when it was not changed.</summary>
            public double? Outlet { get; internal set; }
            /// <summary>How far the existing drain elevations moved, m; null when none moved.</summary>
            public double? Shift { get; internal set; }
            /// <summary>Outlet surface after the change and before it; null when it was not changed.</summary>
            public double? Ground { get; internal set; }
            public double? PreviousGround { get; internal set; }
            public bool RowAdded { get; internal set; }
            public bool SubtitleChanged { get; internal set; }

            public string Text
            {
                get
                {
                    var parts = new List<string>();
                    if (RowAdded) parts.Add("добавлена ордината 0");
                    if (Outlet.HasValue)
                        parts.Add("дрена в устье " + F(Outlet.Value) + (Shift.HasValue
                            ? " (отметки дрены смещены на " + Shift.Value.ToString("+0.00#;-0.00#", CultureInfo.InvariantCulture) + " м, уклоны сохранены)" : ""));
                    if (Ground.HasValue) parts.Add("поверхность в устье " + F(Ground.Value) + (PreviousGround.HasValue ? " (было " + F(PreviousGround.Value) + ")" : ""));
                    if (SubtitleChanged) parts.Add("вторая строка заголовка обновлена");
                    return "«" + Name + "» перестроен по «" + MainName + "» на ПК " + Picket(Station) + ": " + string.Join(", ", parts) + ".";
                }
            }

            private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>A drainage profile of the project as the «Впадает коллектор» list shows it.</summary>
        public sealed class Member
        {
            internal string TabName;
            public string ProfileId { get; internal set; }
            public string Name { get; internal set; }
            public string Number { get; internal set; }
            /// <summary>What to write in «Впадает коллектор»: the number, or the full name when the number is shared.</summary>
            public string Label { get; internal set; }
            /// <summary>Where it flows in now, or null.</summary>
            public Link Receiver { get; internal set; }

            /// <summary>True when one label of a «Впадает коллектор» cell names this profile.</summary>
            public bool Matches(string token) => Same(token, Number) || Same(token, Name) || Same(token, TabName);
        }

        private sealed class Collector
        {
            internal string Id, TabName;
            internal ProfileTemplate Template;
            internal ProfileSettings Settings;
            internal List<LongitudinalPoint> Rows;
            internal string Name => string.IsNullOrWhiteSpace(Settings?.Title) ? TabName : Settings.Title.Trim();
            internal string Number => DrainageNetwork.Number(Name);
        }

        // A prefix is a whole word: «Кр-1», «К-р 3», «Др.5», but not the start of «Кривой» or «Дренажная».
        private static readonly Regex NamePrefix = new Regex(@"^\s*(коллектор|дренаж|дрена|д-на|к-р|кр|др)(?![а-яёa-z])\.?\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>«К-р 3-2'» → «3-2», «Дрена 12» → «12»; a name without a known prefix is kept as written.</summary>
        public static string Number(string name)
        {
            string value = (name ?? "").Trim();
            var match = NamePrefix.Match(value);
            if (match.Success && match.Length < value.Length) value = value.Substring(match.Length).TrimStart('-', '–', '.', ' ');
            return value.Trim().TrimEnd(Marks).Trim();
        }

        private static readonly char[] Marks = { '\'', '’', '′', '"', '`' };

        private static bool Same(string first, string second) => string.Equals((first ?? "").Trim(), (second ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>The prefix written before a receiving collector in a tributary's second title line: «к-р», «дрена»…</summary>
        public static string Prefix(string name)
        {
            var match = NamePrefix.Match(name ?? "");
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "к-р";
        }

        /// <summary>The labels of one «Впадает коллектор» cell: «25, 25-1» names two collectors.</summary>
        public static List<string> Tokens(string label) =>
            (label ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(token => token.Trim()).Where(token => token.Length > 0).ToList();

        public static List<Link> Links(ProfileProject project, List<string> warnings = null)
        {
            if (project == null) return new List<Link>();
            return Links(Collectors(project), warnings ?? new List<string>());
        }

        /// <summary>The drainage profiles of the project in tab order, with their labels and receivers.</summary>
        public static List<Member> Members(ProfileProject project)
        {
            if (project == null) return new List<Member>();
            var order = ProfileCollection.Describe(project).Select(profile => profile.ProfileId).ToList();
            var collectors = Collectors(project).OrderBy(item => order.IndexOf(item.Id)).ToList();
            var links = Links(collectors, new List<string>());
            return collectors.Select(item => new Member
            {
                ProfileId = item.Id, Name = item.Name, Number = item.Number, TabName = item.TabName,
                // A number shared with another profile would link both; the full name then.
                Label = collectors.Any(other => other != item && (Same(other.Number, item.Number) || Same(other.Name, item.Number) || Same(other.TabName, item.Number)))
                    ? item.Name : item.Number,
                Receiver = links.FirstOrDefault(link => Same(link.TributaryProfileId, item.Id))
            }).ToList();
        }

        /// <summary>
        /// A free name for a new tributary of a collector: «К-р 3-1'» for «К-р 3'»
        /// (prefix and trailing mark of the collector, the first unused number).
        /// </summary>
        public static string TributaryName(ProfileProject project, string mainProfileId)
        {
            var profiles = ProfileCollection.Describe(project);
            var collectors = project == null ? new List<Collector>() : Collectors(project);
            var main = collectors.FirstOrDefault(item => Same(item.Id, mainProfileId));
            string name = (main?.Name ?? "К-р 1").Trim();
            var match = NamePrefix.Match(name);
            string prefix = match.Success && match.Length < name.Length ? name.Substring(0, match.Length).Trim() : "К-р";
            string core = name.TrimEnd(Marks);
            string mark = name.Substring(core.Length);
            string number = Number(name);
            for (int index = 1; ; index++)
            {
                string candidate = prefix + " " + number + "-" + index.ToString(CultureInfo.InvariantCulture) + mark;
                if (!collectors.Any(item => Same(item.Number, Number(candidate)) || Same(item.Name, candidate)) &&
                    !profiles.Any(profile => Same(profile.Name, candidate))) return candidate;
            }
        }

        /// <summary>The collector the active profile flows into, or null.</summary>
        public static Link Receiver(ProfileProject project) =>
            Links(project).FirstOrDefault(link => string.Equals(link.TributaryProfileId, project.ProfileId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// A copy in which every tributary starts at its main collector. The outlet
        /// row (the first by station) gets the main drain elevation at the junction,
        /// and all drain elevations of the tributary move by the same amount, so its
        /// slopes stay; the outlet surface is the main collector's surface there.
        /// The receiver line is written as «к-р 3 пк0+38 к-л К-2» when it is empty or
        /// still names another collector of the project; when it names this collector
        /// at another picket, only the picket is corrected. Chains are processed from
        /// the main collector down; nothing else is changed.
        /// </summary>
        public static ProfileProject Synchronize(ProfileProject source, List<Change> changes = null, List<string> warnings = null)
        {
            var result = ProjectFile.Clone(source);
            var collectors = Collectors(result);
            var links = Links(collectors, warnings ?? new List<string>());
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Func<string, int> level = null;
            level = id =>
            {
                if (depth.TryGetValue(id, out int known)) return known;
                depth[id] = 0;
                var parent = links.FirstOrDefault(link => string.Equals(link.TributaryProfileId, id, StringComparison.OrdinalIgnoreCase));
                return depth[id] = parent == null ? 0 : level(parent.MainProfileId) + 1;
            };
            foreach (var link in links.OrderBy(link => level(link.MainProfileId)).ThenBy(link => link.Station))
            {
                var main = collectors.First(item => item.Id == link.MainProfileId);
                var tributary = collectors.First(item => item.Id == link.TributaryProfileId);
                LongitudinalPoint junction;
                try { junction = ProfileInterpolation.SampleLongitudinal(main.Rows, link.Station); }
                catch (ProfileValidationException error)
                {
                    warnings?.Add("«" + link.TributaryName + "» не связан с «" + link.MainName + "»: " + error.Message);
                    continue;
                }
                var change = new Change { ProfileId = tributary.Id, Name = tributary.Name, MainName = main.Name, Station = link.Station };
                bool changed = false;
                if (tributary.Rows.Count == 0) { tributary.Rows.Add(new LongitudinalPoint { Station = 0 }); change.RowAdded = changed = true; }
                var outlet = tributary.Rows.OrderBy(row => row.Station).First();
                if (junction.ProjectBottom.HasValue)
                {
                    // Design elevations are written to the millimetre at most.
                    double target = Math.Round(junction.ProjectBottom.Value, 3);
                    if (!outlet.ProjectBottom.HasValue) { outlet.ProjectBottom = target; change.Outlet = target; changed = true; }
                    else
                    {
                        double shift = Math.Round(target - outlet.ProjectBottom.Value, 6);
                        if (Math.Abs(shift) > 1e-9)
                        {
                            foreach (var row in tributary.Rows.Where(row => row.ProjectBottom.HasValue))
                            {
                                row.ProjectBottom = row == outlet ? target : Math.Round(row.ProjectBottom.Value + shift, 6);
                                ProfileValidation.Elevation(row.ProjectBottom, "Отметка дрены «" + link.TributaryName + "»");
                            }
                            change.Outlet = target;
                            change.Shift = shift;
                            changed = true;
                        }
                    }
                }
                if (junction.Ground.HasValue)
                {
                    double ground = Math.Round(junction.Ground.Value, 3);
                    if (!outlet.Ground.HasValue || Math.Abs(outlet.Ground.Value - ground) > 1e-9)
                    {
                        change.PreviousGround = outlet.Ground;
                        outlet.Ground = ground;
                        change.Ground = ground;
                        changed = true;
                    }
                }
                string subtitle = Subtitle(tributary.Settings.Subtitle, main, link.Station, collectors);
                if (subtitle != null && subtitle != tributary.Settings.Subtitle)
                {
                    tributary.Settings.Subtitle = subtitle;
                    change.SubtitleChanged = changed = true;
                }
                if (changed) changes?.Add(change);
            }
            ProfileValidation.Check(result, false);
            return result;
        }

        private static readonly Regex SubtitleHead = new Regex(
            @"^\s*(?:коллектор|дренаж|дрена|д-на|к-р|кр|др)(?![а-яёa-z])\.?[\s\-–]*(?<number>[^\s,;]+)\s+пк\s*(?<picket>[0-9]+\s*\+\s*[0-9]+(?:[.,][0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The receiver line a tributary should have, or null to keep the written one.</summary>
        private static string Subtitle(string current, Collector main, double station, List<Collector> collectors)
        {
            string wanted = ReceiverText(main, station);
            if (string.IsNullOrWhiteSpace(current)) return wanted;
            var match = SubtitleHead.Match(current);
            if (!match.Success) return null;
            string number = match.Groups["number"].Value.TrimEnd(Marks);
            if (!collectors.Any(item => Same(item.Number, number))) return null;
            // It names another collector of the project: the link moved there.
            if (!Same(number, main.Number)) return wanted;
            var picket = match.Groups["picket"];
            string[] parts = picket.Value.Replace(" ", "").Replace(',', '.').Split('+');
            double written = double.Parse(parts[0], CultureInfo.InvariantCulture) * 100 + double.Parse(parts[1], CultureInfo.InvariantCulture);
            if (Math.Abs(written - station) < 0.005) return null;
            return current.Substring(0, picket.Index) + Picket(station) + current.Substring(picket.Index + picket.Length);
        }

        /// <summary>«к-р 3 пк0+38 к-л К-2»: the receiving collector at the station, then where that collector flows.</summary>
        public static string ReceiverText(string mainName, string mainSubtitle, double station)
        {
            string tail = Regex.Replace((mainSubtitle ?? "").Trim(), @"\s*пк\s*[0-9]+\s*\+\s*[0-9]+(?:[.,][0-9]+)?\s*$", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            string text = Prefix(mainName) + " " + Number(mainName) + " пк" + Picket(station);
            return tail.Length == 0 ? text : text + " " + tail;
        }

        private static string ReceiverText(Collector main, double station) => ReceiverText(main.Name, main.Settings.Subtitle, station);

        /// <summary>«0+38» for 38 m, «0+38.50» for 38.5 m, as written on the sheets.</summary>
        public static string Picket(double station)
        {
            string full = ProfileEngine.Station(station);
            return full.EndsWith(".00", StringComparison.Ordinal) ? full.Substring(0, full.Length - 3) : full;
        }

        private static List<Collector> Collectors(ProfileProject project)
        {
            var result = new List<Collector>
            {
                new Collector { Id = project.ProfileId, TabName = project.ProfileName, Template = project.Template, Settings = project.Settings, Rows = project.Longitudinal }
            };
            foreach (var profile in project.AdditionalProfiles ?? new List<ProfileData>())
                result.Add(new Collector { Id = profile.ProfileId, TabName = profile.Name, Template = profile.Template, Settings = profile.Settings, Rows = profile.Longitudinal });
            return result.Where(item => item.Template == ProfileTemplate.Drainage && item.Rows != null && item.Settings != null).ToList();
        }

        private static List<Link> Links(List<Collector> collectors, List<string> warnings)
        {
            var links = new List<Link>();
            foreach (var main in collectors)
                foreach (var row in main.Rows.Where(row => row != null).OrderBy(row => row.Station))
                    foreach (string token in Tokens(row.PointName))
                    {
                        var matches = collectors.Where(item => item != main && (Same(item.Number, token) || Same(item.Name, token) || Same(item.TabName, token))).ToList();
                        if (matches.Count == 0) continue;
                        if (matches.Count > 1)
                        {
                            warnings.Add("Подпись «" + token + "» на «" + main.Name + "» подходит к нескольким профилям: " + string.Join(", ", matches.Select(item => "«" + item.Name + "»")) + ". Связь не создана.");
                            continue;
                        }
                        var tributary = matches[0];
                        var existing = links.FirstOrDefault(link => link.TributaryProfileId == tributary.Id);
                        if (existing != null)
                        {
                            warnings.Add("«" + tributary.Name + "» указан как впадающий и в «" + existing.MainName + "» на ПК " + Picket(existing.Station) +
                                ", и в «" + main.Name + "» на ПК " + Picket(row.Station) + ". Используется первая связь.");
                            continue;
                        }
                        links.Add(new Link
                        {
                            MainProfileId = main.Id, MainName = main.Name, MainNumber = main.Number, Station = row.Station,
                            TributaryProfileId = tributary.Id, TributaryName = tributary.Name, TributaryNumber = tributary.Number
                        });
                    }
            // A collector cannot flow into one of its own tributaries: walking down
            // from the main collector to its receivers must not reach the tributary.
            foreach (var link in links.ToList())
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string current = link.MainProfileId;
                while (current != null && visited.Add(current))
                {
                    if (string.Equals(current, link.TributaryProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add("«" + link.TributaryName + "» и «" + link.MainName + "» впадают друг в друга по кругу. Связь не создана.");
                        links.Remove(link);
                        break;
                    }
                    string next = current;
                    current = links.FirstOrDefault(item => string.Equals(item.TributaryProfileId, next, StringComparison.OrdinalIgnoreCase))?.MainProfileId;
                }
            }
            return links;
        }
    }
}
