using System.Text.Json.Nodes;

namespace Profiles.Core.Tests;

internal static class SchemaTestDocuments
{
    internal static void RemoveVersionFiveFields(JsonObject document)
    {
        foreach (string name in new[] { "ProfileId", "ProfileName", "ActiveProfileIndex", "AdditionalProfiles" }) document.Remove(name);
        foreach (var item in document["Sections"]!.AsArray())
        {
            var section = item!.AsObject();
            section.Remove("SectionId");
            foreach (var point in section["Points"]!.AsArray()) point!.AsObject().Remove("Label");
        }
    }
}
