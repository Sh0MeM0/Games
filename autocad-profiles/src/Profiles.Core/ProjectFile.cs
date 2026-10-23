using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
                        LineInfoHandling = LineInfoHandling.Load
                    });
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.None)
                            throw new ProfileValidationException("После объекта проекта обнаружены лишние данные JSON.");
                    if (!(root is JObject) || root["SchemaVersion"]?.Type != JTokenType.Integer)
                        throw new ProfileValidationException("В проекте отсутствует целочисленная SchemaVersion.");
                    int version = root["SchemaVersion"].Value<int>();
                    if (version != 1 && version != 2 && version != 3)
                        throw new ProfileValidationException("Версия проекта не поддерживается. Поддерживаются SchemaVersion = 1, 2 и 3; обновите модуль или используйте совместимый файл.");
                    if (version == 1) UpgradeLegacy((JObject)root);
                    if (version <= 2) UpgradeVersionTwo((JObject)root);
                    ValidateToken(root, typeof(ProfileProject), "Проект");
                    var project = root.ToObject<ProfileProject>(JsonSerializer.Create(Settings()));
                    ProfileValidation.Check(project, geometry: false);
                    project.ProjectId = Guid.Parse(project.ProjectId).ToString("N");
                    return project;
                }
            }
            catch (ProfileValidationException) { throw; }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException || error is EncoderFallbackException || error is OverflowException)
            {
                throw new ProfileValidationException("Не удалось прочитать проект: проверьте корректность JSON. " + error.Message, error);
            }
        }

        public static ProfileProject Clone(ProfileProject project) => Deserialize(Serialize(project));

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

        private static void ValidateToken(JToken token, Type expected, string location)
        {
            Type nullable = Nullable.GetUnderlyingType(expected);
            if (token.Type == JTokenType.Null)
            {
                if (nullable != null) return;
                Invalid(location, "обязательное поле не может быть null");
            }
            if (nullable != null) expected = nullable;
            if (expected == typeof(double))
            {
                if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                    Invalid(location, "ожидается число или null для необязательной отметки, а не текст");
                double value = token.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value)) Invalid(location, "NaN и бесконечность недопустимы");
                return;
            }
            if (expected == typeof(int))
            {
                if (token.Type != JTokenType.Integer) Invalid(location, "ожидается целое число");
                token.Value<int>();
                return;
            }
            if (expected == typeof(bool))
            {
                if (token.Type != JTokenType.Boolean) Invalid(location, "ожидается true или false");
                return;
            }
            if (expected == typeof(string))
            {
                if (token.Type != JTokenType.String) Invalid(location, "ожидается строка");
                return;
            }
            if (expected.IsEnum)
            {
                if (token.Type != JTokenType.String || !Enum.GetNames(expected).Contains(token.Value<string>(), StringComparer.Ordinal))
                    Invalid(location, "неизвестное значение перечисления");
                return;
            }
            if (expected.IsGenericType && expected.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (!(token is JArray array)) Invalid(location, "ожидается массив");
                var items = (JArray)token;
                if (items.Count > 10000) Invalid(location, "слишком много элементов (предел 10000)");
                Type itemType = expected.GetGenericArguments()[0];
                for (int i = 0; i < items.Count; i++) ValidateToken(items[i], itemType, location + "[" + i + "]");
                return;
            }
            if (!(token is JObject)) Invalid(location, "ожидается объект");
            var obj = (JObject)token;
            var properties = expected.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (JProperty member in obj.Properties())
            {
                if (!properties.Any(p => p.Name == member.Name)) Invalid(location + "." + member.Name, "неизвестное поле");
            }
            foreach (PropertyInfo property in properties)
            {
                JToken value;
                if (!obj.TryGetValue(property.Name, StringComparison.Ordinal, out value))
                    Invalid(location + "." + property.Name, "поле отсутствует; файл должен содержать полную схему проекта");
                ValidateToken(value, property.PropertyType, location + "." + property.Name);
            }
            if (obj.Children().Any(child => child.Type == JTokenType.Comment)) Invalid(location, "комментарии не поддерживаются");
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
