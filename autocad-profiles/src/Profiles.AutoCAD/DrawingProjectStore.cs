using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    internal sealed class DrawingProjectCatalog
    {
        internal List<ProfileProject> Projects { get; } = new List<ProfileProject>();
        internal string LastActiveProjectId { get; set; }
    }

    internal sealed class StoredProfileOutput
    {
        [JsonProperty(Required = Required.Always)] public int Version { get; set; } = 1;
        [JsonProperty(Required = Required.Always)] public string ProjectId { get; set; }
        [JsonProperty(Required = Required.Always)] public string Kind { get; set; }
        [JsonProperty(Required = Required.Always)] public string DrawingId { get; set; }
        [JsonProperty(Required = Required.Always)] public string Fingerprint { get; set; }
        [JsonProperty(Required = Required.Always)] public string GenerationId { get; set; }
        [JsonProperty(Required = Required.Always)] public string SpaceHandle { get; set; }
        [JsonProperty(Required = Required.Always)] public double[] Transform { get; set; }
        [JsonProperty(Required = Required.Always)] public List<string> Handles { get; set; } = new List<string>();
    }

    /// <summary>
    /// All data lives in an owned Named Objects Dictionary branch and participates
    /// in normal DWG save/undo. Existing records are never silently reset on error.
    /// </summary>
    internal static class DrawingProjectStore
    {
        private const string RootName = "SH0MEM0_PROFILES_PROJECTS";
        private const string HeaderName = "HEADER";
        private const string ProjectsName = "PROJECTS";
        private const string OutputsName = "OUTPUTS";
        private const string ActiveName = "LAST_ACTIVE";
        private const int MaximumRecordBytes = 10 * 1024 * 1024;
        private const int MaximumCatalogBytes = 64 * 1024 * 1024;
        private const int MaximumProjects = 128;
        private const int ChunkSize = 127;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private sealed class StoreHeader
        {
            [JsonProperty(Required = Required.Always)] public int Version { get; set; } = 1;
            [JsonProperty(Required = Required.Always)] public string DrawingId { get; set; }
        }

        internal static DrawingProjectCatalog Read(Database database)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var catalog = Read(database, transaction);
                transaction.Commit();
                return catalog;
            }
        }

        private static DrawingProjectCatalog Read(Database database, Transaction transaction)
        {
            var result = new DrawingProjectCatalog();
            var root = Root(database, transaction, false);
            if (root == null) return result;
            var header = Header(root, transaction);
            var projects = Child(root, ProjectsName, transaction);
            long total = 0;
            foreach (DBDictionaryEntry entry in projects)
            {
                if (result.Projects.Count >= MaximumProjects) throw Invalid("слишком много проектов (предел 128)");
                string json = ReadPayload(entry.Value, transaction, "проект " + entry.Key);
                total += Utf8.GetByteCount(json);
                if (total > MaximumCatalogBytes) throw Invalid("каталог проектов превышает 64 МБ");
                ProfileProject project;
                try { project = ProjectFile.Deserialize(json); }
                catch (ProfileValidationException exception)
                { throw Invalid("проект " + entry.Key + ": " + exception.Message, exception); }
                ProjectId(entry.Key);
                if (!string.Equals(entry.Key, project.ProjectId, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("идентификатор проекта не совпадает с ключом записи " + entry.Key);
                result.Projects.Add(project);
            }
            var ids = new HashSet<string>(result.Projects.Select(project => project.ProjectId), StringComparer.OrdinalIgnoreCase);
            result.LastActiveProjectId = ReadActive(root, transaction, ids);
            ValidateOutputs(root, transaction, header, ids);
            return result;
        }

        internal static void Save(Database database, ProfileProject project)
        {
            // Validate and encode before entering a write transaction.
            string json = ProjectFile.Serialize(project);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                Save(database, transaction, project, json);
                transaction.Commit();
            }
        }

        internal static void Save(Database database, Transaction transaction, ProfileProject project, string serialized = null)
        {
            string json = serialized ?? ProjectFile.Serialize(project);
            ProjectId(project.ProjectId);
            var root = Root(database, transaction, true);
            var header = Header(root, transaction);
            var projects = Child(root, ProjectsName, transaction);
            if (projects.Count > MaximumProjects) throw Invalid("слишком много проектов (предел 128)");
            if (!projects.Contains(project.ProjectId) && projects.Count >= MaximumProjects)
                throw Invalid("достигнут предел 128 проектов; используйте другой чертёж");
            long total = Utf8.GetByteCount(json);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DBDictionaryEntry entry in projects)
            {
                // Validate every retained project, including the version about to
                // be replaced. Corrupt/unknown data must not be overwritten.
                string previousJson = ReadPayload(entry.Value, transaction, "проект " + entry.Key);
                ProfileProject previous;
                try { previous = ProjectFile.Deserialize(previousJson); }
                catch (ProfileValidationException exception) { throw Invalid("проект " + entry.Key + ": " + exception.Message, exception); }
                if (!string.Equals(previous.ProjectId, entry.Key, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("идентификатор сохранённого проекта не соответствует записи");
                ids.Add(previous.ProjectId);
                if (!string.Equals(entry.Key, project.ProjectId, StringComparison.OrdinalIgnoreCase))
                    total += Utf8.GetByteCount(previousJson);
            }
            if (total > MaximumCatalogBytes) throw Invalid("каталог проектов превышает 64 МБ");
            ReadActive(root, transaction, ids);
            ValidateOutputs(root, transaction, header, ids);
            WritePayload(projects, project.ProjectId, json, transaction);
            WritePayload(root, ActiveName, project.ProjectId, transaction);
        }

        internal static string DrawingId(Database database, Transaction transaction)
        {
            return Header(Root(database, transaction, true), transaction).DrawingId;
        }

        internal static StoredProfileOutput ReadOutput(Database database, Transaction transaction, string projectId, ProfileKind kind)
        {
            ProjectId(projectId);
            var root = Root(database, transaction, false);
            if (root == null) return null;
            var header = Header(root, transaction);
            var outputs = Child(root, OutputsName, transaction);
            string key = OutputKey(projectId, kind);
            if (!outputs.Contains(key)) return null;
            var result = Decode<StoredProfileOutput>(ReadPayload(outputs.GetAt(key), transaction, "построение " + key));
            ValidateOutput(result, projectId, kind);
            if (!string.Equals(result.DrawingId, header.DrawingId, StringComparison.Ordinal) ||
                !string.Equals(result.Fingerprint, Fingerprint(database), StringComparison.OrdinalIgnoreCase))
                throw Invalid("сведения о построении относятся к другому DWG. Существующие объекты не изменены; для копии создайте новый проект");
            return result;
        }

        internal static void SaveOutput(Database database, Transaction transaction, StoredProfileOutput output, ProfileKind kind)
        {
            ValidateOutput(output, output.ProjectId, kind);
            var root = Root(database, transaction, true);
            var header = Header(root, transaction);
            if (output.DrawingId != header.DrawingId || output.Fingerprint != Fingerprint(database))
                throw Invalid("построение не принадлежит текущему чертежу");
            var outputs = Child(root, OutputsName, transaction);
            WritePayload(outputs, OutputKey(output.ProjectId, kind), JsonConvert.SerializeObject(output, MetadataSettings()), transaction);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DBDictionaryEntry entry in Child(root, ProjectsName, transaction)) ids.Add(entry.Key);
            ValidateOutputs(root, transaction, header, ids);
        }

        private static string ReadActive(DBDictionary root, Transaction transaction, HashSet<string> projectIds)
        {
            if (!root.Contains(ActiveName)) return null;
            string id = ReadPayload(root.GetAt(ActiveName), transaction, "последний проект");
            ProjectId(id);
            if (!projectIds.Contains(id)) throw Invalid("ссылка на последний открытый проект не соответствует каталогу");
            return id;
        }

        private static void ValidateOutputs(DBDictionary root, Transaction transaction, StoreHeader header, HashSet<string> projectIds)
        {
            var outputs = Child(root, OutputsName, transaction);
            if (outputs.Count > MaximumProjects * 2) throw Invalid("слишком много записей построений");
            long total = 0;
            foreach (DBDictionaryEntry entry in outputs)
            {
                string json = ReadPayload(entry.Value, transaction, "построение " + entry.Key);
                total += Utf8.GetByteCount(json);
                if (total > MaximumCatalogBytes) throw Invalid("каталог построений превышает 64 МБ");
                var output = Decode<StoredProfileOutput>(json);
                if (entry.Key.Length != 34 || entry.Key[32] != '_' || (entry.Key[33] != 'L' && entry.Key[33] != 'C'))
                    throw Invalid("неизвестный ключ построения " + entry.Key);
                string projectId = entry.Key.Substring(0, 32);
                var kind = entry.Key[33] == 'L' ? ProfileKind.Longitudinal : ProfileKind.CrossSection;
                ValidateOutput(output, projectId, kind);
                if (!projectIds.Contains(projectId) || output.DrawingId != header.DrawingId)
                    throw Invalid("сведения о построении не соответствуют каталогу проектов");
                // Fingerprint is checked only before replacing this output. A DWG
                // copy with a new fingerprint may still open/export its projects.
            }
        }

        internal static string KindCode(ProfileKind kind)
        {
            if (kind == ProfileKind.Longitudinal) return "L";
            if (kind == ProfileKind.CrossSection) return "C";
            throw Invalid("неизвестный тип профиля");
        }

        internal static string Fingerprint(Database database)
        {
            Guid fingerprint;
            if (!Guid.TryParse(database.FingerprintGuid, out fingerprint) || fingerprint == Guid.Empty)
                throw Invalid("у чертежа отсутствует корректный отпечаток DWG; сохраните DWG и повторите построение");
            return fingerprint.ToString("D");
        }

        private static string OutputKey(string projectId, ProfileKind kind) => projectId + "_" + KindCode(kind);

        internal static long HandleValue(string value)
        {
            long handle;
            if (string.IsNullOrEmpty(value) || value.Length > 16 ||
                !long.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out handle) || handle <= 0)
                throw Invalid("некорректный идентификатор объекта чертежа");
            return handle;
        }

        private static void ValidateOutput(StoredProfileOutput output, string projectId, ProfileKind kind)
        {
            if (output == null || output.Version != 1) throw Invalid("неподдерживаемая версия записи построения");
            ProjectId(output.ProjectId);
            ProjectId(output.DrawingId);
            ProjectId(output.GenerationId);
            Guid fingerprint;
            if (!Guid.TryParseExact(output.Fingerprint, "D", out fingerprint)) throw Invalid("повреждён отпечаток DWG");
            if (output.ProjectId != projectId || output.Kind != KindCode(kind)) throw Invalid("запись построения принадлежит другому проекту или типу");
            HandleValue(output.SpaceHandle);
            if (output.Transform == null || output.Transform.Length != 16 || output.Transform.Any(value => double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e12))
                throw Invalid("повреждено положение профиля");
            var m = output.Transform;
            if (Math.Abs(m[12]) > 1e-9 || Math.Abs(m[13]) > 1e-9 || Math.Abs(m[14]) > 1e-9 || Math.Abs(m[15] - 1) > 1e-9)
                throw Invalid("неподдерживаемое преобразование профиля");
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                {
                    double dot = m[a] * m[b] + m[4 + a] * m[4 + b] + m[8 + a] * m[8 + b];
                    if (Math.Abs(dot - (a == b ? 1 : 0)) > 1e-8) throw Invalid("система координат профиля повреждена");
                }
            if (output.Handles == null || output.Handles.Count > 250000) throw Invalid("неверное количество объектов построения");
            var unique = new HashSet<long>();
            foreach (string handle in output.Handles)
                if (!unique.Add(HandleValue(handle))) throw Invalid("повторяющийся объект в записи построения");
        }

        private static DBDictionary Root(Database database, Transaction transaction, bool create)
        {
            var named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (named.Contains(RootName))
            {
                var existing = transaction.GetObject(named.GetAt(RootName), OpenMode.ForRead) as DBDictionary;
                if (existing == null) throw Invalid("вместо каталога проектов обнаружен другой объект");
                return existing;
            }
            if (!create) return null;
            named.UpgradeOpen();
            var root = new DBDictionary();
            named.SetAt(RootName, root);
            transaction.AddNewlyCreatedDBObject(root, true);
            foreach (string name in new[] { ProjectsName, OutputsName })
            {
                var child = new DBDictionary();
                root.SetAt(name, child);
                transaction.AddNewlyCreatedDBObject(child, true);
            }
            WritePayload(root, HeaderName, JsonConvert.SerializeObject(new StoreHeader { DrawingId = Guid.NewGuid().ToString("N") }, MetadataSettings()), transaction);
            return root;
        }

        private static StoreHeader Header(DBDictionary root, Transaction transaction)
        {
            foreach (DBDictionaryEntry entry in root)
                if (entry.Key != HeaderName && entry.Key != ProjectsName && entry.Key != OutputsName && entry.Key != ActiveName)
                    throw Invalid("обнаружена неизвестная запись каталога " + entry.Key + "; возможно, нужен более новый модуль");
            if (!root.Contains(HeaderName)) throw Invalid("отсутствует заголовок каталога");
            var header = Decode<StoreHeader>(ReadPayload(root.GetAt(HeaderName), transaction, "заголовок каталога"));
            if (header.Version != 1) throw Invalid("версия каталога не поддерживается; обновите модуль");
            ProjectId(header.DrawingId);
            Child(root, ProjectsName, transaction);
            Child(root, OutputsName, transaction);
            return header;
        }

        private static DBDictionary Child(DBDictionary parent, string name, Transaction transaction)
        {
            if (!parent.Contains(name)) throw Invalid("отсутствует раздел каталога " + name);
            var result = transaction.GetObject(parent.GetAt(name), OpenMode.ForRead) as DBDictionary;
            if (result == null) throw Invalid("повреждён раздел каталога " + name);
            return result;
        }

        private static void ProjectId(string value)
        {
            Guid id;
            if (string.IsNullOrEmpty(value) || !Guid.TryParseExact(value, "N", out id))
                throw Invalid("некорректный идентификатор проекта");
        }

        private static JsonSerializerSettings MetadataSettings() => new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error,
            DateParseHandling = DateParseHandling.None,
            MaxDepth = 16,
            Culture = CultureInfo.InvariantCulture
        };

        private static T Decode<T>(string json)
        {
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 })
                {
                    var token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error, CommentHandling = CommentHandling.Load });
                    if (!(token is JObject value) || value.DescendantsAndSelf().Any(child => child.Type == JTokenType.Comment)) throw Invalid("повреждён служебный JSON");
                    while (reader.Read()) if (reader.TokenType != JsonToken.None) throw Invalid("лишние данные в служебной записи");
                    return token.ToObject<T>(JsonSerializer.Create(MetadataSettings()));
                }
            }
            catch (JsonException exception) { throw Invalid("повреждена служебная запись: " + exception.Message, exception); }
        }

        private static void WritePayload(DBDictionary dictionary, string key, string text, Transaction transaction)
        {
            byte[] bytes = Utf8.GetBytes(text);
            if (bytes.Length > MaximumRecordBytes) throw Invalid("запись превышает 10 МБ");
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Int32, 1),
                new TypedValue((int)DxfCode.Text, Hash(bytes)),
                new TypedValue((int)DxfCode.Int32, bytes.Length)
            };
            for (int offset = 0; offset < bytes.Length; offset += ChunkSize)
            {
                var part = new byte[Math.Min(ChunkSize, bytes.Length - offset)];
                Buffer.BlockCopy(bytes, offset, part, 0, part.Length);
                values.Add(new TypedValue((int)DxfCode.BinaryChunk, part));
            }
            Xrecord record;
            if (dictionary.Contains(key))
            {
                record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite) as Xrecord;
                if (record == null) throw Invalid("запись " + key + " имеет неизвестный формат");
            }
            else
            {
                if (!dictionary.IsWriteEnabled) dictionary.UpgradeOpen();
                record = new Xrecord { XlateReferences = false };
                dictionary.SetAt(key, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            using (var buffer = new ResultBuffer(values.ToArray())) record.Data = buffer;
        }

        private static string ReadPayload(ObjectId id, Transaction transaction, string description)
        {
            var record = transaction.GetObject(id, OpenMode.ForRead) as Xrecord;
            if (record == null) throw Invalid(description + ": ожидалась запись Xrecord");
            using (var data = record.Data)
            using (var stream = new MemoryStream())
            {
                if (data == null) throw Invalid(description + ": пустая запись");
                int index = 0, length = -1;
                string hash = null;
                foreach (TypedValue value in data)
                {
                    if (index == 0)
                    {
                        if (value.TypeCode != (int)DxfCode.Int32 || !(value.Value is int) || (int)value.Value != 1)
                            throw Invalid(description + ": неподдерживаемая версия хранения");
                    }
                    else if (index == 1)
                    {
                        hash = value.Value as string;
                        if (value.TypeCode != (int)DxfCode.Text || hash == null || hash.Length != 64) throw Invalid(description + ": неверная контрольная сумма");
                    }
                    else if (index == 2)
                    {
                        if (value.TypeCode != (int)DxfCode.Int32 || !(value.Value is int)) throw Invalid(description + ": отсутствует длина данных");
                        length = (int)value.Value;
                        if (length < 0 || length > MaximumRecordBytes) throw Invalid(description + ": недопустимый размер записи");
                    }
                    else
                    {
                        var part = value.Value as byte[];
                        if (value.TypeCode != (int)DxfCode.BinaryChunk || part == null || part.Length == 0 || part.Length > ChunkSize)
                            throw Invalid(description + ": повреждён блок данных");
                        if (stream.Length + part.Length > length) throw Invalid(description + ": превышена заявленная длина данных");
                        stream.Write(part, 0, part.Length);
                    }
                    index++;
                    if (index > MaximumRecordBytes / ChunkSize + 4) throw Invalid(description + ": слишком много блоков данных");
                }
                if (index < 3 || stream.Length != length) throw Invalid(description + ": неполная запись");
                byte[] bytes = stream.ToArray();
                if (!string.Equals(Hash(bytes), hash, StringComparison.Ordinal)) throw Invalid(description + ": контрольная сумма не совпадает");
                try { return Utf8.GetString(bytes); }
                catch (DecoderFallbackException exception) { throw Invalid(description + ": повреждён текст UTF-8", exception); }
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static ProfileValidationException Invalid(string detail, System.Exception inner = null) =>
            new ProfileValidationException("Данные «Профиля» в DWG: " + detail + ". Сохранённые данные не удалялись и не сбрасывались.", inner);
    }
}
