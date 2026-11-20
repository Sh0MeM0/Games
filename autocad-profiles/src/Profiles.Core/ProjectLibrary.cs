using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Profiles.Core
{
    /// <summary>A project kept in the per-user autosave library.</summary>
    public sealed class LibraryEntry
    {
        public string ProjectId { get; internal set; }
        public string Name { get; internal set; }
        public DateTime SavedUtc { get; internal set; }
        public string Hash { get; internal set; }
        /// <summary>Increases with every stored change of this project.</summary>
        public long Sequence { get; internal set; }
        /// <summary>File name of the drawing the project was last saved from (display only).</summary>
        public string SourceDrawing { get; internal set; }
        public string JsonPath { get; internal set; }
        public bool JsonAutoSave { get; internal set; }
        public int ProfileCount { get; internal set; }
        public int PointCount { get; internal set; }
        /// <summary>Identifier of a deleted copy; null for live entries.</summary>
        public string DeletedId { get; internal set; }
        public DateTime? DeletedUtc { get; internal set; }
        internal Dictionary<string, List<DrawingState>> Drawings { get; set; } =
            new Dictionary<string, List<DrawingState>>(StringComparer.OrdinalIgnoreCase);

        internal LibraryEntry Copy()
        {
            var copy = (LibraryEntry)MemberwiseClone();
            copy.Drawings = Drawings.ToDictionary(item => item.Key, item => item.Value.Select(state => state).ToList(), StringComparer.OrdinalIgnoreCase);
            return copy;
        }
    }

    /// <summary>One earlier state of a project.</summary>
    public sealed class LibraryVersion
    {
        public string VersionId { get; internal set; }
        public DateTime SavedUtc { get; internal set; }
        public long Bytes { get; internal set; }
        public bool IsCurrent { get; internal set; }
    }

    internal struct DrawingState
    {
        internal string Hash;
        internal long Sequence;
    }

    public sealed class LibrarySyncResult
    {
        /// <summary>Projects of the drawing; a project is replaced by its newer library version when the drawing rolled back.</summary>
        public List<ProfileProject> DrawingProjects { get; } = new List<ProfileProject>();
        /// <summary>Library projects that are not stored in this drawing, newest first.</summary>
        public List<LibraryEntry> LibraryOnly { get; } = new List<LibraryEntry>();
        /// <summary>Projects taken from the library because the drawing holds an earlier state (UNDO, DWG not saved, older copy).</summary>
        public HashSet<string> Restored { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Hash of the JSON currently stored in the drawing, by project.</summary>
        public Dictionary<string, string> DrawingHashes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Messages { get; } = new List<string>();
    }

    /// <summary>
    /// Per-user autosave of every project, independent of the DWG, its UNDO and
    /// whether the DWG was saved. Files are ordinary project JSON (importable with
    /// «Импорт JSON») with a small sidecar; earlier states go to History and
    /// deleted projects to Deleted, so nothing is lost silently.
    /// </summary>
    public sealed class ProjectLibrary
    {
        private const int MaximumHistoryVersions = 40;
        private const long MaximumHistoryBytes = 100L * 1024 * 1024;
        private const int MaximumDeleted = 60;
        private const int MaximumStatesPerDrawing = 200;
        private const int MaximumDrawings = 64;
        private static readonly TimeSpan HistoryInterval = TimeSpan.FromMinutes(3);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Func<DateTime> clock;

        public ProjectLibrary(string root, Func<DateTime> clock = null)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Не задана папка автосохранения.", nameof(root));
            RootPath = Path.GetFullPath(root);
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>%APPDATA%\Sh0MeM0\Profiles — the same vendor folder as the other Sh0MeM0 modules.</summary>
        public static string DefaultRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) appData = Path.GetTempPath();
            return Path.Combine(appData, "Sh0MeM0", "Profiles");
        }

        public string RootPath { get; }
        public string ProjectsPath => Path.Combine(RootPath, "Projects");
        private string HistoryPath => Path.Combine(RootPath, "History");
        private string DeletedPath => Path.Combine(RootPath, "Deleted");

        public static string Hash(string json)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Utf8.GetBytes(json ?? ""));
                var text = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        /// <summary>Live projects, newest first. Damaged sidecars are skipped, never deleted.</summary>
        public List<LibraryEntry> List()
        {
            var result = new List<LibraryEntry>();
            if (!Directory.Exists(ProjectsPath)) return result;
            foreach (string meta in SafeFiles(ProjectsPath, "*.meta.json"))
            {
                string id = Path.GetFileName(meta);
                id = id.Substring(0, id.Length - ".meta.json".Length);
                if (!ValidId(id) || !File.Exists(ProjectFilePath(id))) continue;
                var entry = TryReadMeta(meta);
                if (entry != null && string.Equals(entry.ProjectId, id, StringComparison.OrdinalIgnoreCase)) result.Add(entry);
            }
            return result.OrderByDescending(entry => entry.SavedUtc).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public LibraryEntry Get(string projectId)
        {
            RequireId(projectId);
            return ReadEntry(projectId, verify: true);
        }

        public ProfileProject Load(string projectId)
        {
            RequireId(projectId);
            return ReadProjectFile(ProjectFilePath(projectId), "автосохранение проекта");
        }

        /// <summary>
        /// Stores the project. Unchanged content is not rewritten. The previous
        /// content goes to History when <paramref name="checkpoint"/> is set or
        /// the last kept version is older than three minutes.
        /// </summary>
        public LibraryEntry Save(ProfileProject project, string json = null, string drawingKey = null, string drawingName = null, bool checkpoint = false)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            json = json ?? ProjectFile.Serialize(project);
            string id = project.ProjectId;
            RequireId(id);
            string hash = Hash(json);
            return Guard(() =>
            {
                Directory.CreateDirectory(ProjectsPath);
                var previous = ReadEntry(id, verify: true);
                if (previous != null && previous.Hash == hash)
                {
                    bool changed = RecordState(previous, drawingKey, hash, previous.Sequence);
                    if (!string.IsNullOrEmpty(drawingName) && previous.SourceDrawing != drawingName) { previous.SourceDrawing = drawingName; changed = true; }
                    if (changed) WriteMeta(previous);
                    return previous.Copy();
                }
                if (previous != null) Archive(previous, checkpoint);
                WriteAtomic(ProjectFilePath(id), json + Environment.NewLine);
                var entry = previous?.Copy() ?? new LibraryEntry { ProjectId = id };
                entry.Name = project.Name;
                entry.Hash = hash;
                entry.Sequence = (previous?.Sequence ?? 0) + 1;
                entry.SavedUtc = clock();
                if (!string.IsNullOrEmpty(drawingName)) entry.SourceDrawing = drawingName;
                entry.ProfileCount = 1 + (project.AdditionalProfiles?.Count ?? 0);
                entry.PointCount = Points(project);
                RecordState(entry, drawingKey, hash, entry.Sequence);
                WriteMeta(entry);
                return entry.Copy();
            });
        }

        /// <summary>Remembers that a drawing now holds this exact content.</summary>
        public void RecordDrawingState(string projectId, string hash, string drawingKey)
        {
            RequireId(projectId);
            if (string.IsNullOrEmpty(drawingKey) || string.IsNullOrEmpty(hash)) return;
            Guard(() =>
            {
                var entry = ReadEntry(projectId, verify: false);
                if (entry == null || entry.Hash != hash) return 0;
                if (RecordState(entry, drawingKey, hash, entry.Sequence)) WriteMeta(entry);
                return 0;
            });
        }

        /// <summary>Remembers the external JSON file of a project and whether it is rewritten automatically.</summary>
        public void SetJsonLink(string projectId, string path, bool autoSave)
        {
            RequireId(projectId);
            Guard(() =>
            {
                var entry = ReadEntry(projectId, verify: false);
                if (entry == null) return 0;
                string normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
                if (entry.JsonPath == normalized && entry.JsonAutoSave == (autoSave && normalized != null)) return 0;
                entry.JsonPath = normalized;
                entry.JsonAutoSave = autoSave && normalized != null;
                WriteMeta(entry);
                return 0;
            });
        }

        /// <summary>Copies the stored content to History now (before a risky action).</summary>
        public void Checkpoint(string projectId)
        {
            RequireId(projectId);
            Guard(() =>
            {
                var entry = ReadEntry(projectId, verify: true);
                if (entry != null) Archive(entry, true);
                return 0;
            });
        }

        /// <summary>Moves the project to Deleted. Its history is kept.</summary>
        public void Delete(string projectId)
        {
            RequireId(projectId);
            Guard(() =>
            {
                var entry = ReadEntry(projectId, verify: true);
                if (entry == null) return 0;
                Directory.CreateDirectory(DeletedPath);
                string stamp = Stamp(clock()) + "-" + projectId;
                entry.DeletedId = stamp;
                entry.DeletedUtc = clock();
                File.Copy(ProjectFilePath(projectId), Path.Combine(DeletedPath, stamp + ".json"), true);
                WriteAtomic(Path.Combine(DeletedPath, stamp + ".meta.json"), MetaJson(entry));
                File.Delete(ProjectFilePath(projectId));
                File.Delete(MetaFilePath(projectId));
                PruneDeleted();
                return 0;
            });
        }

        public List<LibraryEntry> ListDeleted()
        {
            var result = new List<LibraryEntry>();
            if (!Directory.Exists(DeletedPath)) return result;
            foreach (string meta in SafeFiles(DeletedPath, "*.meta.json"))
            {
                var entry = TryReadMeta(meta);
                string stamp = Path.GetFileName(meta);
                stamp = stamp.Substring(0, stamp.Length - ".meta.json".Length);
                if (entry == null || !File.Exists(Path.Combine(DeletedPath, stamp + ".json"))) continue;
                entry.DeletedId = stamp;
                result.Add(entry);
            }
            return result.OrderByDescending(entry => entry.DeletedUtc ?? entry.SavedUtc).ToList();
        }

        /// <summary>Returns a deleted project to the library and removes the deleted copy.</summary>
        public ProfileProject RestoreDeleted(string deletedId)
        {
            if (string.IsNullOrEmpty(deletedId) || deletedId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || deletedId.Contains(".."))
                throw new ProfileValidationException("Некорректная запись удалённого проекта.");
            string file = Path.Combine(DeletedPath, deletedId + ".json");
            var project = ReadProjectFile(file, "удалённый проект");
            Save(project, checkpoint: true);
            Guard(() =>
            {
                File.Delete(file);
                File.Delete(Path.Combine(DeletedPath, deletedId + ".meta.json"));
                return 0;
            });
            return project;
        }

        /// <summary>The current state and the kept earlier states, newest first.</summary>
        public List<LibraryVersion> Versions(string projectId)
        {
            RequireId(projectId);
            var result = new List<LibraryVersion>();
            var entry = ReadEntry(projectId, verify: false);
            if (entry != null)
                result.Add(new LibraryVersion { VersionId = "", SavedUtc = entry.SavedUtc, Bytes = Length(ProjectFilePath(projectId)), IsCurrent = true });
            string folder = Path.Combine(HistoryPath, projectId);
            if (Directory.Exists(folder))
                foreach (string file in SafeFiles(folder, "*.json"))
                {
                    string version = Path.GetFileNameWithoutExtension(file);
                    if (!TryParseStamp(version, out var saved)) continue;
                    result.Add(new LibraryVersion { VersionId = version, SavedUtc = saved, Bytes = Length(file) });
                }
            return result.OrderByDescending(version => version.IsCurrent).ThenByDescending(version => version.SavedUtc).ToList();
        }

        public ProfileProject LoadVersion(string projectId, string versionId)
        {
            RequireId(projectId);
            if (string.IsNullOrEmpty(versionId)) return Load(projectId);
            if (!TryParseStamp(versionId, out _)) throw new ProfileValidationException("Некорректная версия проекта.");
            return ReadProjectFile(Path.Combine(HistoryPath, projectId, versionId + ".json"), "версия проекта");
        }

        /// <summary>
        /// Brings the drawing and the library together when the editor opens:
        /// new drawing projects are added to the library; an exact earlier state
        /// of this drawing (UNDO, DWG closed without saving, older copy) yields to
        /// the newer library version; an unknown drawing state wins and the
        /// library version is kept in History.
        /// </summary>
        public LibrarySyncResult Synchronize(IEnumerable<ProfileProject> drawingProjects, string drawingKey, string drawingName)
        {
            var result = new LibrarySyncResult();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in drawingProjects ?? Enumerable.Empty<ProfileProject>())
            {
                ids.Add(project.ProjectId);
                string json = ProjectFile.Serialize(project);
                string hash = Hash(json);
                result.DrawingHashes[project.ProjectId] = hash;
                LibraryEntry entry = null;
                try { entry = Get(project.ProjectId); }
                catch (ProfileValidationException error) { result.Messages.Add(error.Message); }
                try
                {
                    if (entry == null)
                    {
                        Save(project, json, drawingKey, drawingName);
                        result.DrawingProjects.Add(project);
                        continue;
                    }
                    if (entry.Hash == hash)
                    {
                        RecordDrawingState(project.ProjectId, hash, drawingKey);
                        result.DrawingProjects.Add(project);
                        continue;
                    }
                    long? drawingSequence = null;
                    if (!string.IsNullOrEmpty(drawingKey) && entry.Drawings.TryGetValue(drawingKey, out var states))
                        foreach (var state in states)
                            if (state.Hash == hash) drawingSequence = Math.Max(drawingSequence ?? 0, state.Sequence);
                    if (drawingSequence.HasValue && drawingSequence.Value < entry.Sequence)
                    {
                        var newer = Load(project.ProjectId);
                        result.DrawingProjects.Add(newer);
                        result.Restored.Add(project.ProjectId);
                        result.Messages.Add("Проект «" + newer.Name + "»: в чертеже более ранняя версия (UNDO, DWG закрыт без сохранения или старая копия файла). " +
                            "Открыта последняя автосохранённая версия от " + Local(entry.SavedUtc) + ". Прежние версии — кнопка «Версии…».");
                        continue;
                    }
                    // The drawing was changed where this library did not see it
                    // (another computer, older module): the DWG wins, nothing is dropped.
                    Save(project, json, drawingKey, drawingName, checkpoint: true);
                    result.DrawingProjects.Add(project);
                    result.Messages.Add("Проект «" + project.Name + "»: в чертеже другая версия, чем в автосохранении. Открыта версия из DWG; " +
                        "автосохранённая от " + Local(entry.SavedUtc) + " доступна в «Версии…».");
                }
                catch (ProfileValidationException error)
                {
                    result.DrawingProjects.Add(project);
                    result.Messages.Add("Автосохранение недоступно: " + error.Message);
                }
            }
            try { result.LibraryOnly.AddRange(List().Where(entry => !ids.Contains(entry.ProjectId))); }
            catch (ProfileValidationException error) { result.Messages.Add("Автосохранение недоступно: " + error.Message); }
            return result;
        }

        internal static string Local(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

        private static int Points(ProfileProject project)
        {
            int count = (project.Longitudinal?.Count ?? 0) + (project.Sections?.Sum(section => section.Points?.Count ?? 0) ?? 0);
            foreach (var profile in project.AdditionalProfiles ?? new List<ProfileData>())
                count += (profile.Longitudinal?.Count ?? 0) + (profile.Sections?.Sum(section => section.Points?.Count ?? 0) ?? 0);
            return count;
        }

        private void Archive(LibraryEntry entry, bool force)
        {
            string source = ProjectFilePath(entry.ProjectId);
            if (!File.Exists(source)) return;
            string folder = Path.Combine(HistoryPath, entry.ProjectId);
            Directory.CreateDirectory(folder);
            var kept = SafeFiles(folder, "*.json").Select(file => new { File = file, Ok = TryParseStamp(Path.GetFileNameWithoutExtension(file), out var time), Time = time })
                .Where(item => item.Ok).OrderByDescending(item => item.Time).ToList();
            if (kept.Count > 0)
            {
                // Continuous typing keeps one earlier state every three minutes.
                if (!force && clock() - kept[0].Time < HistoryInterval) return;
                // The same content is never stored twice in a row.
                try { if (Hash(File.ReadAllText(kept[0].File, Utf8).TrimEnd('\r', '\n')) == entry.Hash) return; }
                catch (IOException) { }
                catch (DecoderFallbackException) { }
            }
            string name = Stamp(entry.SavedUtc);
            string target = Path.Combine(folder, name + ".json");
            for (int suffix = 1; File.Exists(target); suffix++)
                target = Path.Combine(folder, Stamp(entry.SavedUtc.AddMilliseconds(suffix)) + ".json");
            File.Copy(source, target);
            PruneHistory(folder);
        }

        private void PruneHistory(string folder)
        {
            var files = SafeFiles(folder, "*.json").Select(file => new FileInfo(file))
                .Where(info => TryParseStamp(Path.GetFileNameWithoutExtension(info.Name), out _))
                .OrderByDescending(info => info.Name, StringComparer.Ordinal).ToList();
            long total = 0;
            for (int i = 0; i < files.Count; i++)
            {
                total += files[i].Length;
                if (i >= MaximumHistoryVersions || (i > 0 && total > MaximumHistoryBytes))
                    try { files[i].Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private void PruneDeleted()
        {
            var metas = SafeFiles(DeletedPath, "*.meta.json").OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal).ToList();
            foreach (string meta in metas.Skip(MaximumDeleted))
            {
                string stamp = Path.GetFileName(meta);
                stamp = stamp.Substring(0, stamp.Length - ".meta.json".Length);
                try
                {
                    File.Delete(Path.Combine(DeletedPath, stamp + ".json"));
                    File.Delete(meta);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static bool RecordState(LibraryEntry entry, string drawingKey, string hash, long sequence)
        {
            if (string.IsNullOrEmpty(drawingKey)) return false;
            if (!entry.Drawings.TryGetValue(drawingKey, out var states))
            {
                if (entry.Drawings.Count >= MaximumDrawings)
                {
                    string oldest = entry.Drawings.OrderBy(item => item.Value.Count == 0 ? 0 : item.Value.Max(state => state.Sequence)).First().Key;
                    entry.Drawings.Remove(oldest);
                }
                entry.Drawings[drawingKey] = states = new List<DrawingState>();
            }
            if (states.Any(state => state.Hash == hash && state.Sequence == sequence)) return false;
            states.RemoveAll(state => state.Hash == hash);
            states.Add(new DrawingState { Hash = hash, Sequence = sequence });
            if (states.Count > MaximumStatesPerDrawing) states.RemoveRange(0, states.Count - MaximumStatesPerDrawing);
            return true;
        }

        private LibraryEntry ReadEntry(string projectId, bool verify)
        {
            string meta = MetaFilePath(projectId), file = ProjectFilePath(projectId);
            if (!File.Exists(file)) return null;
            var entry = File.Exists(meta) ? TryReadMeta(meta) : null;
            if (entry != null && !string.Equals(entry.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)) entry = null;
            if (entry != null && !verify) return entry;
            string json;
            try { json = File.ReadAllText(file, Utf8).TrimEnd('\r', '\n'); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is DecoderFallbackException)
            { throw Failure("не удалось прочитать " + file, error); }
            string hash = Hash(json);
            if (entry != null && entry.Hash == hash) return entry;
            // The JSON was written but its sidecar is missing or older (an
            // interrupted save, another AutoCAD): the file content is the truth.
            ProfileProject project;
            try { project = ProjectFile.Deserialize(json); }
            catch (ProfileValidationException error) { throw Failure("файл автосохранения " + file + " повреждён: " + error.Message, error); }
            var repaired = entry ?? new LibraryEntry { ProjectId = projectId };
            repaired.Name = project.Name;
            repaired.Hash = hash;
            repaired.Sequence = (entry?.Sequence ?? 0) + 1;
            repaired.SavedUtc = SafeWriteTime(file);
            repaired.ProfileCount = 1 + project.AdditionalProfiles.Count;
            repaired.PointCount = Points(project);
            return repaired;
        }

        private LibraryEntry TryReadMeta(string path)
        {
            try
            {
                JObject root;
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path, Utf8))) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 })
                    root = JObject.Load(reader);
                if (root.Value<int?>("Version") != 1) return null;
                var entry = new LibraryEntry
                {
                    ProjectId = root.Value<string>("ProjectId"),
                    Name = root.Value<string>("Name") ?? "Проект",
                    Hash = root.Value<string>("Hash"),
                    Sequence = root.Value<long?>("Sequence") ?? 0,
                    SourceDrawing = root.Value<string>("SourceDrawing"),
                    JsonPath = root.Value<string>("JsonPath"),
                    JsonAutoSave = root.Value<bool?>("JsonAutoSave") ?? false,
                    ProfileCount = root.Value<int?>("ProfileCount") ?? 1,
                    PointCount = root.Value<int?>("PointCount") ?? 0
                };
                if (!ValidId(entry.ProjectId) || string.IsNullOrEmpty(entry.Hash)) return null;
                if (!TryParseUtc(root.Value<string>("SavedUtc"), out var saved)) return null;
                entry.SavedUtc = saved;
                if (TryParseUtc(root.Value<string>("DeletedUtc"), out var deleted)) entry.DeletedUtc = deleted;
                if (root["Drawings"] is JObject drawings)
                    foreach (var drawing in drawings.Properties())
                    {
                        var states = new List<DrawingState>();
                        if (drawing.Value is JArray array)
                            foreach (var item in array.OfType<JObject>())
                            {
                                string hash = item.Value<string>("Hash");
                                long? sequence = item.Value<long?>("Sequence");
                                if (!string.IsNullOrEmpty(hash) && sequence.HasValue) states.Add(new DrawingState { Hash = hash, Sequence = sequence.Value });
                            }
                        entry.Drawings[drawing.Name] = states;
                    }
                return entry;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException ||
                                          error is DecoderFallbackException || error is InvalidCastException || error is FormatException || error is OverflowException)
            {
                return null;
            }
        }

        private void WriteMeta(LibraryEntry entry) => WriteAtomic(MetaFilePath(entry.ProjectId), MetaJson(entry));

        private static string MetaJson(LibraryEntry entry)
        {
            var drawings = new JObject();
            foreach (var drawing in entry.Drawings)
                drawings[drawing.Key] = new JArray(drawing.Value.Select(state => new JObject { ["Hash"] = state.Hash, ["Sequence"] = state.Sequence }));
            var root = new JObject
            {
                ["Version"] = 1,
                ["ProjectId"] = entry.ProjectId,
                ["Name"] = entry.Name,
                ["SavedUtc"] = entry.SavedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["Hash"] = entry.Hash,
                ["Sequence"] = entry.Sequence,
                ["SourceDrawing"] = entry.SourceDrawing,
                ["JsonPath"] = entry.JsonPath,
                ["JsonAutoSave"] = entry.JsonAutoSave,
                ["ProfileCount"] = entry.ProfileCount,
                ["PointCount"] = entry.PointCount,
                ["Drawings"] = drawings
            };
            if (entry.DeletedUtc.HasValue) root["DeletedUtc"] = entry.DeletedUtc.Value.ToString("o", CultureInfo.InvariantCulture);
            return root.ToString(Formatting.Indented);
        }

        private static ProfileProject ReadProjectFile(string path, string description)
        {
            if (!File.Exists(path)) throw new ProfileValidationException("Автосохранение: " + description + " не найдено.");
            return ProjectFile.Load(path);
        }

        private static void WriteAtomic(string path, string text)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = Utf8.GetBytes(text);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch (Exception error) when (error is IOException || error is PlatformNotSupportedException || error is UnauthorizedAccessException)
                    {
                        // Some antivirus/network folders refuse Replace; a full copy still never truncates the old file first.
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                }
                else File.Move(temporary, path);
                temporary = null;
            }
            finally
            {
                if (temporary != null)
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static T Guard<T>(Func<T> action)
        {
            try { return action(); }
            catch (ProfileValidationException) { throw; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException || error is NotSupportedException || error is ArgumentException)
            { throw Failure(error.Message, error); }
        }

        private static ProfileValidationException Failure(string detail, Exception inner) =>
            new ProfileValidationException("Автосохранение: " + detail, inner);

        private static IEnumerable<string> SafeFiles(string folder, string pattern)
        {
            try { return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : new string[0]; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return new string[0]; }
        }

        private static long Length(string file)
        {
            try { return new FileInfo(file).Length; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return 0; }
        }

        private DateTime SafeWriteTime(string file)
        {
            try { return File.GetLastWriteTimeUtc(file); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return clock(); }
        }

        private string ProjectFilePath(string id) => Path.Combine(ProjectsPath, id + ".json");
        private string MetaFilePath(string id) => Path.Combine(ProjectsPath, id + ".meta.json");

        private static string Stamp(DateTime utc) => utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        private static bool TryParseStamp(string value, out DateTime utc) =>
            DateTime.TryParseExact(value, "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

        private static bool TryParseUtc(string value, out DateTime utc)
        {
            if (!DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc)) return false;
            utc = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return true;
        }

        private static bool ValidId(string id) => !string.IsNullOrEmpty(id) && Guid.TryParseExact(id, "N", out _);

        private static void RequireId(string id)
        {
            if (!ValidId(id)) throw new ProfileValidationException("Автосохранение: некорректный идентификатор проекта.");
        }
    }
}
