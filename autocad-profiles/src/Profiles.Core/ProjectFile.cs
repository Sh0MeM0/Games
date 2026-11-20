using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Profiles.Core
{
    /// <summary>Versioned project files. Loading never turns a missing elevation into zero.</summary>
    public static class ProjectFile
    {
        private const int MaximumFileBytes = 10 * 1024 * 1024;

        public static void Save(string path, ProfileProject project)
        {
            // Validate before opening either the destination or a temporary file.
            ProfileValidation.Check(project, geometry: false);
            string temporary = null;
            try
            {
                string destination = FullPath(path);
                string directory = Path.GetDirectoryName(destination);
                if (!Directory.Exists(directory))
                    throw new ProfileValidationException("Папка для проекта не существует: выберите существующую папку.");
                string json = Serialize(project);
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(json + Environment.NewLine);
                if (bytes.Length > MaximumFileBytes)
                    throw new ProfileValidationException("Размер проекта превышает 10 МБ; разделите его на несколько файлов.");
                // A sibling temporary file keeps the final replace on the same volume.
                temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
                temporary = null;
            }
            catch (ProfileValidationException) { throw; }
            catch (Exception error) when (FileError(error) || error is JsonException)
            {
                throw new ProfileValidationException("Не удалось сохранить проект. Проверьте путь, доступ к папке и возможность атомарной замены файла. Существующий файл не удалялся.", error);
            }
            finally
            {
                // A failed replace must not remove the user's existing destination.
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public static ProfileProject Load(string path)
        {
            try
            {
                using (var stream = new FileStream(FullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumFileBytes)
                        throw new ProfileValidationException("Файл проекта превышает 10 МБ; разделите данные на несколько проектов.");
                    using (var text = new StreamReader(stream, new UTF8Encoding(false, true), true))
                        return Deserialize(text.ReadToEnd());
                }
            }
            catch (ProfileValidationException) { throw; }
            catch (Exception error) when (FileError(error) || error is DecoderFallbackException)
            {
                throw new ProfileValidationException("Не удалось открыть проект: проверьте путь и корректность JSON. " + error.Message, error);
            }
        }

        /// <summary>Serialize raw input, including null interpolation cells, for JSON or DWG storage.</summary>
        public static string Serialize(ProfileProject project)
        {
            ProfileValidation.Check(project, geometry: false);
            string json = JsonConvert.SerializeObject(project, Formatting.Indented, Settings());
            if (new UTF8Encoding(false, true).GetByteCount(json) > MaximumFileBytes)
                throw new ProfileValidationException("Размер проекта превышает 10 МБ; разделите его на несколько файлов.");
            return json;
        }

        public static ProfileProject Deserialize(string json)
        {
            try
            {
                if (json == null || json.Length > MaximumFileBytes || new UTF8Encoding(false, true).GetByteCount(json) > MaximumFileBytes)
                    throw new ProfileValidationException("Данные проекта отсутствуют или превышают 10 МБ.");
                using (var text = new StringReader(json))
                using (var reader = new JsonTextReader(text)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double,
                    MaxDepth = 32,
                    Culture = CultureInfo.InvariantCulture
                })
                {
                    var root = JToken.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Load,
                        // Messages name the JSON path; per-token line numbers only cost memory and time.
                        LineInfoHandling = LineInfoHandling.Ignore
                    });
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.None)
                            throw new ProfileValidationException("После объекта проекта обнаружены лишние данные JSON.");
                    if (!(root is JObject) || root["SchemaVersion"]?.Type != JTokenType.Integer)
                        throw new ProfileValidationException("В проекте отсутствует целочисленная SchemaVersion.");
                    int version = root["SchemaVersion"].Value<int>();
                    if (version < 1 || version > 5)
                        throw new ProfileValidationException("Версия проекта не поддерживается. Поддерживаются SchemaVersion = 1, 2, 3, 4 и 5; обновите модуль или используйте совместимый файл.");
                    if (version == 1) UpgradeLegacy((JObject)root);
                    if (version <= 2) UpgradeVersionTwo((JObject)root);
                    if (version <= 3) UpgradeVersionThree((JObject)root);
                    if (version <= 4) UpgradeVersionFour((JObject)root);
                    return Materialize(root);
                }
            }
            catch (ProfileValidationException) { throw; }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException || error is EncoderFallbackException || error is OverflowException)
            {
                throw new ProfileValidationException("Не удалось прочитать проект: проверьте корректность JSON. " + error.Message, error);
            }
        }

        private static ProfileProject Materialize(JToken root)
        {
            ValidateToken(root, typeof(ProfileProject));
            var project = root.ToObject<ProfileProject>(Serializer);
            ProfileValidation.Check(project, geometry: false);
            project.ProjectId = Guid.Parse(project.ProjectId).ToString("N");
            project.ProfileId = Guid.Parse(project.ProfileId).ToString("N");
            foreach (var section in project.Sections) section.SectionId = Guid.Parse(section.SectionId).ToString("N");
            foreach (var profile in project.AdditionalProfiles)
            {
                profile.ProfileId = Guid.Parse(profile.ProfileId).ToString("N");
                foreach (var section in profile.Sections) section.SectionId = Guid.Parse(section.SectionId).ToString("N");
            }
            return project;
        }

        /// <summary>
        /// An independent, validated copy with the same result as
        /// Deserialize(Serialize(project)): the source passes the same validation
        /// (for an object graph it covers everything the JSON schema check does —
        /// missing lists and texts, non-finite numbers, unknown enum values, sizes)
        /// and identifiers are normalised the same way. The graph is copied
        /// directly: the JSON round trip cost ~0.25 s for a large project and ran
        /// several times per edit. ProjectCopyTests guards that no member is missed.
        /// The 10 MB file limit is enforced when the project is serialized for a file or the DWG.
        /// </summary>
        public static ProfileProject Clone(ProfileProject project)
        {
            ProfileValidation.Check(project, geometry: false);
            var copy = new ProfileProject
            {
                SchemaVersion = project.SchemaVersion,
                ProjectId = Guid.Parse(project.ProjectId).ToString("N"),
                Name = project.Name,
                ProfileId = Guid.Parse(project.ProfileId).ToString("N"),
                ProfileName = project.ProfileName,
                ActiveProfileIndex = project.ActiveProfileIndex,
                AdditionalProfiles = project.AdditionalProfiles.Select(profile => new ProfileData
                {
                    ProfileId = Guid.Parse(profile.ProfileId).ToString("N"),
                    Name = profile.Name,
                    Template = profile.Template,
                    Settings = CopySettings(profile.Settings),
                    Longitudinal = profile.Longitudinal.Select(CopyPoint).ToList(),
                    Sections = profile.Sections.Select(CopySection).ToList()
                }).ToList(),
                Kind = project.Kind,
                Template = project.Template,
                Settings = CopySettings(project.Settings),
                Longitudinal = project.Longitudinal.Select(CopyPoint).ToList(),
                Sections = project.Sections.Select(CopySection).ToList()
            };
            return copy;
        }

        private static ProfileSettings CopySettings(ProfileSettings source) => new ProfileSettings
        {
            Title = source.Title, HorizontalScale = source.HorizontalScale, VerticalScale = source.VerticalScale,
            CrossHorizontalScale = source.CrossHorizontalScale, CrossVerticalScale = source.CrossVerticalScale,
            MetresPerDrawingUnit = source.MetresPerDrawingUnit, Datum = source.Datum,
            TextHeightMm = source.TextHeightMm, TextWidthFactor = source.TextWidthFactor, ShowEmptyRows = source.ShowEmptyRows
        };

        private static LongitudinalPoint CopyPoint(LongitudinalPoint source) => new LongitudinalPoint
        {
            Station = source.Station, ProjectGround = source.ProjectGround, PipeInvert = source.PipeInvert,
            PipeDescription = source.PipeDescription, Foundation = source.Foundation, PointName = source.PointName,
            PeatDepth = source.PeatDepth, Ground = source.Ground, LeftBank = source.LeftBank, RightBank = source.RightBank,
            ExistingBottom = source.ExistingBottom, Water = source.Water, ProjectBottom = source.ProjectBottom,
            BottomWidth = source.BottomWidth, LeftSlope = source.LeftSlope, RightSlope = source.RightSlope
        };

        private static CrossSection CopySection(CrossSection source)
        {
            var copy = ProfileInterpolation.CloneSection(source);
            copy.SectionId = Guid.Parse(source.SectionId).ToString("N");
            return copy;
        }

        private static void UpgradeLegacy(JObject root)
        {
            // The old schema had no identity or pending longitudinal seed elevations.
            // Do not fill missing old properties: strict validation still catches damaged files.
            if (root.Property("ProjectId") != null || root.Property("Name") != null || root.Property("Template") != null)
                Invalid("Проект", "поля версии 2 указаны в файле версии 1");
            root["ProjectId"] = Guid.NewGuid().ToString("N");
            root["Name"] = "Импортированный проект";
            bool reconstruction = root["Longitudinal"] is JArray longitudinal && longitudinal.OfType<JObject>().Any(point =>
                new[] { "ExistingBottom", "LeftBank", "RightBank", "Water" }.Any(name => point[name] != null && point[name].Type != JTokenType.Null));
            root["Template"] = reconstruction ? "Reconstruction" : "NewChannel";
            if (root["Longitudinal"] is JArray legacyPoints)
                foreach (var point in legacyPoints.OfType<JObject>())
                {
                    foreach (string name in new[] { "ProjectGround", "PipeInvert", "PeatDepth" })
                    {
                        if (point.Property(name) != null) Invalid("Пикет", "поле версии 2 указано в файле версии 1");
                        point[name] = JValue.CreateNull();
                    }
                    foreach (string name in new[] { "PipeDescription", "Foundation", "PointName" })
                    {
                        if (point.Property(name) != null) Invalid("Пикет", "поле версии 2 указано в файле версии 1");
                        point[name] = "";
                    }
                }
            if (root["Sections"] is JArray sections)
                foreach (var item in sections.OfType<JObject>())
                    foreach (string name in new[] { "LeftBank", "RightBank", "ExistingBottom" })
                    {
                        if (item.Property(name) != null) Invalid("Поперечник", "поле версии 2 указано в файле версии 1");
                        item[name] = JValue.CreateNull();
                    }
            root["SchemaVersion"] = 2;
        }

        private static void UpgradeVersionTwo(JObject root)
        {
            if (root["Sections"] is JArray sections)
                foreach (var section in sections.OfType<JObject>())
                {
                    string[] nullable = { "ProjectAnchorIndex", "SiltThickness", "SiltLeft", "SiltRight" };
                    string[] booleans = { "UsePointDistances", "UseLongitudinalProjectBottom", "UseLongitudinalWater" };
                    foreach (string name in nullable.Concat(booleans).Concat(new[] { "DesignMode" }))
                        if (section.Property(name) != null) Invalid("Поперечник", "поле версии 3 указано в старой версии файла");
                    foreach (string name in nullable) section[name] = JValue.CreateNull();
                    foreach (string name in booleans) section[name] = false;
                    bool trapezoid = new[] { "ProjectBottom", "BottomWidth", "LeftSlope", "RightSlope" }.Any(name => section[name] != null && section[name].Type != JTokenType.Null);
                    bool points = section["Points"] is JArray oldPoints && oldPoints.OfType<JObject>().Any(point => point["Design"] != null && point["Design"].Type != JTokenType.Null);
                    section["DesignMode"] = trapezoid && points ? "Automatic" : trapezoid ? "Trapezoid" : points ? "Points" : "None";
                    if (section["Points"] is JArray pointArray)
                        foreach (var point in pointArray.OfType<JObject>())
                        {
                            foreach (string name in new[] { "Role", "DistanceFromPrevious", "UseLongitudinalElevation" })
                                if (point.Property(name) != null) Invalid("Точка поперечника", "поле версии 3 указано в старой версии файла");
                            point["Role"] = "Regular";
                            point["DistanceFromPrevious"] = JValue.CreateNull();
                            point["UseLongitudinalElevation"] = false;
                        }
                }
            root["SchemaVersion"] = 3;
        }

        private static void UpgradeVersionThree(JObject root)
        {
            if (root["Settings"] is JObject settings)
            {
                foreach (string name in new[] { "CrossHorizontalScale", "CrossVerticalScale" })
                    if (settings.Property(name) != null) Invalid("Настройки", "поле версии 4 указано в старой версии файла");
                // Copy tokens rather than converting them: a damaged/missing old
                // scale must still fail strict validation, never acquire a default.
                settings["CrossHorizontalScale"] = settings["HorizontalScale"]?.DeepClone() ?? JValue.CreateNull();
                settings["CrossVerticalScale"] = settings["VerticalScale"]?.DeepClone() ?? JValue.CreateNull();
            }
            root["SchemaVersion"] = 4;
        }

        private static void UpgradeVersionFour(JObject root)
        {
            foreach (string field in new[] { "ProfileId", "ProfileName", "ActiveProfileIndex", "AdditionalProfiles" })
                if (root.Property(field) != null) Invalid("Проект", "поле версии 5 указано в старой версии файла");
            if (root["ProjectId"]?.Type != JTokenType.String) Invalid("Проект", "идентификатор старого проекта должен быть строкой");
            Guid projectIdentity;
            if (!Guid.TryParseExact(root["ProjectId"]?.Value<string>(), "N", out projectIdentity)
                && !Guid.TryParseExact(root["ProjectId"]?.Value<string>(), "D", out projectIdentity))
                Invalid("Проект", "некорректный идентификатор старого проекта");
            string initialProfileId = projectIdentity.ToString("N");
            root["ProfileId"] = initialProfileId;
            root["ProfileName"] = "Профиль 1";
            root["ActiveProfileIndex"] = 0;
            root["AdditionalProfiles"] = new JArray();
            int sectionIndex = 0;
            if (root["Sections"] is JArray sections)
                foreach (var section in sections.OfType<JObject>())
                {
                    if (section.Property("SectionId") != null) Invalid("Поперечник", "поле версии 5 указано в старой версии файла");
                    // Before the first save the same old DWG may be read twice.
                    // Deterministic migration keeps selected-output ownership
                    // stable until the identifiers are persisted in schema 5.
                    using (var sha = SHA256.Create())
                    {
                        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(initialProfileId + ":section:" + sectionIndex.ToString(CultureInfo.InvariantCulture)));
                        byte[] identifier = new byte[16];
                        Array.Copy(digest, identifier, identifier.Length);
                        section["SectionId"] = new Guid(identifier).ToString("N");
                    }
                    sectionIndex++;
                    if (section["Points"] is JArray points)
                        foreach (var point in points.OfType<JObject>())
                        {
                            if (point.Property("Label") != null) Invalid("Точка поперечника", "поле версии 5 указано в старой версии файла");
                            point["Label"] = "";
                        }
                }
            root["SchemaVersion"] = 5;
        }

        // A fresh serializer per call: cheap, and no shared state between concurrent callers.
        private static JsonSerializer Serializer => JsonSerializer.Create(Settings());

        private sealed class TypeShape
        {
            internal PropertyInfo[] Properties;
            internal Dictionary<string, PropertyInfo> ByName;
            internal HashSet<string> EnumNames;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, TypeShape> Shapes =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, TypeShape>();

        private static TypeShape Shape(Type type) => Shapes.GetOrAdd(type, key =>
        {
            var properties = key.IsEnum ? new PropertyInfo[0] : key.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            return new TypeShape
            {
                Properties = properties,
                ByName = properties.ToDictionary(property => property.Name, StringComparer.Ordinal),
                EnumNames = key.IsEnum ? new HashSet<string>(Enum.GetNames(key), StringComparer.Ordinal) : null
            };
        });

        /// <summary>Location in the style «Проект.Sections[0].Points[2].Existing», built only for an error.</summary>
        private static string Location(JToken token, string member = null)
        {
            string path = token.Path;
            string location = path.Length == 0 ? "Проект" : "Проект." + path;
            return member == null ? location : location + "." + member;
        }

        private static JsonSerializerSettings Settings()
        {
            return new JsonSerializerSettings
            {
                Culture = CultureInfo.InvariantCulture,
                TypeNameHandling = TypeNameHandling.None,
                MissingMemberHandling = MissingMemberHandling.Error,
                NullValueHandling = NullValueHandling.Include,
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
                Converters = new List<JsonConverter> { new StringEnumConverter { AllowIntegerValues = false } }
            };
        }

        private static void ValidateToken(JToken token, Type expected)
        {
            Type nullable = Nullable.GetUnderlyingType(expected);
            if (token.Type == JTokenType.Null)
            {
                if (nullable != null) return;
                Invalid(Location(token), "обязательное поле не может быть null");
            }
            if (nullable != null) expected = nullable;
            if (expected == typeof(double))
            {
                if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                    Invalid(Location(token), "ожидается число или null для необязательной отметки, а не текст");
                double value = token.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value)) Invalid(Location(token), "NaN и бесконечность недопустимы");
                return;
            }
            if (expected == typeof(int))
            {
                if (token.Type != JTokenType.Integer) Invalid(Location(token), "ожидается целое число");
                token.Value<int>();
                return;
            }
            if (expected == typeof(bool))
            {
                if (token.Type != JTokenType.Boolean) Invalid(Location(token), "ожидается true или false");
                return;
            }
            if (expected == typeof(string))
            {
                if (token.Type != JTokenType.String) Invalid(Location(token), "ожидается строка");
                return;
            }
            if (expected.IsEnum)
            {
                if (token.Type != JTokenType.String || !Shape(expected).EnumNames.Contains(token.Value<string>()))
                    Invalid(Location(token), "неизвестное значение перечисления");
                return;
            }
            if (expected.IsGenericType && expected.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (!(token is JArray items)) { Invalid(Location(token), "ожидается массив"); return; }
                if (items.Count > 10000) Invalid(Location(token), "слишком много элементов (предел 10000)");
                Type itemType = expected.GetGenericArguments()[0];
                foreach (var item in items) ValidateToken(item, itemType);
                return;
            }
            if (!(token is JObject obj)) { Invalid(Location(token), "ожидается объект"); return; }
            var shape = Shape(expected);
            foreach (JProperty member in obj.Properties())
                if (!shape.ByName.ContainsKey(member.Name)) Invalid(Location(token, member.Name), "неизвестное поле");
            foreach (PropertyInfo property in shape.Properties)
            {
                JToken value;
                if (!obj.TryGetValue(property.Name, StringComparison.Ordinal, out value))
                    Invalid(Location(token, property.Name), "поле отсутствует; файл должен содержать полную схему проекта");
                ValidateToken(value, property.PropertyType);
            }
            foreach (var child in obj.Children())
                if (child.Type == JTokenType.Comment) Invalid(Location(token), "комментарии не поддерживаются");
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ProfileValidationException("Выберите путь файла проекта.");
            return Path.GetFullPath(path);
        }
        private static bool FileError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException;
        }
        private static void Invalid(string location, string reason)
        {
            throw new ProfileValidationException("Некорректный проект: " + location + " — " + reason + ".");
        }
    }
}
