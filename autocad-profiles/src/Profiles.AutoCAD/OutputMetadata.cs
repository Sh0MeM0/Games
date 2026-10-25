using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    internal sealed class StoredProfileOutput
    {
        [JsonProperty(Required = Required.Always)] public int Version { get; set; } = 1;
        [JsonProperty(Required = Required.Always)] public string ProjectId { get; set; }
        [JsonProperty(Required = Required.Always)] public string Kind { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ProfileId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string SectionId { get; set; }
        [JsonProperty(Required = Required.Always)] public string DrawingId { get; set; }
        [JsonProperty(Required = Required.Always)] public string Fingerprint { get; set; }
        [JsonProperty(Required = Required.Always)] public string GenerationId { get; set; }
        [JsonProperty(Required = Required.Always)] public string SpaceHandle { get; set; }
        [JsonProperty(Required = Required.Always)] public double[] Transform { get; set; }
        [JsonProperty(Required = Required.Always)] public List<string> Handles { get; set; } = new List<string>();
    }

    /// <summary>Strict output identity and ownership checks, independent of the CAD runtime.</summary>
    internal static class OutputMetadata
    {
        internal static void ValidateJson(JObject value)
        {
            if (value["Version"]?.Type != JTokenType.Integer) throw Invalid("версия построения должна быть целым числом");
            foreach (string field in new[] { "ProjectId", "Kind", "DrawingId", "Fingerprint", "GenerationId", "SpaceHandle" })
                if (value[field]?.Type != JTokenType.String) throw Invalid("служебное поле " + field + " должно быть строкой");
            foreach (string field in new[] { "ProfileId", "SectionId" })
                if (value[field] != null && value[field].Type != JTokenType.String && value[field].Type != JTokenType.Null)
                    throw Invalid("служебное поле " + field + " должно быть строкой или null");
            if (!(value["Transform"] is JArray transform) || transform.Count != 16 ||
                transform.Any(number => number.Type != JTokenType.Float && number.Type != JTokenType.Integer))
                throw Invalid("преобразование построения должно содержать 16 чисел");
            if (!(value["Handles"] is JArray handles) || handles.Any(handle => handle.Type != JTokenType.String))
                throw Invalid("идентификаторы объектов должны быть строками");
        }

        internal static string OutputProfileId(StoredProfileOutput output) => output.Version == 1 ? output.ProjectId : output.ProfileId;

        internal static string OutputKey(string projectId, ProfileKind kind, string profileId = null, string sectionId = null)
        {
            ProjectId(projectId);
            KindCode(kind);
            if (profileId == null)
            {
                if (sectionId != null) throw Invalid("у поперечника отсутствует идентификатор продольного профиля");
                return projectId + "_" + KindCode(kind);
            }
            ProjectId(profileId);
            if (kind == ProfileKind.Longitudinal)
            {
                if (sectionId != null) throw Invalid("продольный профиль не может иметь идентификатор поперечника");
                return projectId + "_L_" + profileId;
            }
            ProjectId(sectionId);
            return projectId + "_C_" + profileId + "_" + sectionId;
        }

        internal static void ParseOutputKey(string key, out string projectId, out ProfileKind kind, out string profileId, out string sectionId)
        {
            if (key == null || (key.Length != 34 && key.Length != 67 && key.Length != 100) || key[32] != '_' ||
                (key[33] != 'L' && key[33] != 'C') ||
                (key.Length == 67 && (key[33] != 'L' || key[34] != '_')) ||
                (key.Length == 100 && (key[33] != 'C' || key[34] != '_' || key[67] != '_')))
                throw Invalid("неизвестный ключ построения " + key);
            projectId = key.Substring(0, 32);
            kind = key[33] == 'L' ? ProfileKind.Longitudinal : ProfileKind.CrossSection;
            profileId = key.Length >= 67 ? key.Substring(35, 32) : null;
            sectionId = key.Length == 100 ? key.Substring(68, 32) : null;
            ProjectId(projectId);
            if (profileId != null) ProjectId(profileId);
            if (sectionId != null) ProjectId(sectionId);
        }

        /// <summary>True for an output record of any kind, profile or section of the project.</summary>
        internal static bool BelongsToProject(string key, string projectId)
        {
            ProjectId(projectId);
            ParseOutputKey(key, out string keyProject, out ProfileKind _, out string _, out string _);
            return string.Equals(keyProject, projectId, StringComparison.OrdinalIgnoreCase);
        }

        internal static long HandleValue(string value)
        {
            long handle;
            if (string.IsNullOrEmpty(value) || value.Length > 16 ||
                !long.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out handle) || handle <= 0)
                throw Invalid("некорректный идентификатор объекта чертежа");
            return handle;
        }

        internal static void ValidateOutput(StoredProfileOutput output, string projectId, ProfileKind kind, string profileId = null, string sectionId = null)
        {
            if (output == null || (output.Version != 1 && output.Version != 2)) throw Invalid("неподдерживаемая версия записи построения");
            if (output.Version == 1 && (output.ProfileId != null || profileId != null || output.SectionId != null || sectionId != null))
                throw Invalid("устаревшая запись построения не может содержать отдельный поперечник");
            if (output.Version == 2)
            {
                ProjectId(output.ProfileId);
                if (!string.Equals(output.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("запись построения принадлежит другому продольному профилю");
                if (kind == ProfileKind.CrossSection) ProjectId(output.SectionId);
                if ((kind == ProfileKind.Longitudinal && output.SectionId != null) || !string.Equals(output.SectionId, sectionId, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("запись построения принадлежит другому поперечнику");
            }
            ProjectId(output.ProjectId);
            ProjectId(output.DrawingId);
            ProjectId(output.GenerationId);
            Guid fingerprint;
            if (!Guid.TryParseExact(output.Fingerprint, "D", out fingerprint)) throw Invalid("повреждён отпечаток DWG");
            if (!string.Equals(output.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) || output.Kind != KindCode(kind))
                throw Invalid("запись построения принадлежит другому проекту или типу");
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

        internal static bool InScope(StoredProfileOutput output, string projectId, string profileId,
            ProfileKind kind, string selectedSectionId, bool allSections, bool allProfiles)
        {
            if (!string.Equals(output.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) || output.Kind != KindCode(kind)) return false;
            bool profile = string.Equals(OutputProfileId(output), profileId, StringComparison.OrdinalIgnoreCase);
            return kind == ProfileKind.Longitudinal ? allProfiles || profile : profile &&
                (allSections || (output.SectionId != null && string.Equals(output.SectionId, selectedSectionId, StringComparison.OrdinalIgnoreCase)));
        }

        internal static StoredProfileOutput FindPrevious(IEnumerable<StoredProfileOutput> outputs, string projectId,
            ProfileKind kind, string profileId, string sectionId)
        {
            return outputs.Where(output => string.Equals(output.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) &&
                output.Kind == KindCode(kind) && string.Equals(OutputProfileId(output), profileId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(output.SectionId, sectionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(output => output.Version).FirstOrDefault();
        }

        internal static string KindCode(ProfileKind kind)
        {
            if (kind == ProfileKind.Longitudinal) return "L";
            if (kind == ProfileKind.CrossSection) return "C";
            throw Invalid("неизвестный тип профиля");
        }

        private static void ProjectId(string value)
        {
            if (string.IsNullOrEmpty(value) || !Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty)
                throw Invalid("некорректный идентификатор проекта или профиля");
        }

        private static ProfileValidationException Invalid(string detail) =>
            new ProfileValidationException("Данные «Профиля» в DWG: " + detail + ". Сохранённые данные не удалялись и не сбрасывались.");
    }
}
