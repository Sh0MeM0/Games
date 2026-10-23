using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "profiles-core-tests-" + Guid.NewGuid().ToString("N"));

    public ProjectFileTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, true);

    private string FilePath => Path.Combine(directory, "Проект профиля.json");

    private JsonObject SavedDocument(ProfileProject project = null)
    {
        ProjectFile.Save(FilePath, project ?? Longitudinal(10, 12));
        return JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
    }

    private static string Property(JsonObject document, string name) =>
        document.Select(pair => pair.Key).Single(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void VersionedJsonRoundtripPreservesSettingsNullsAndExplicitZero()
    {
        var project = Longitudinal(10, 12);
        project.Settings.Title = "Канава № 2 — проверка кириллицы";
        project.Settings.Datum = -5;
        project.Settings.TextWidthFactor = 0.73;
        project.Longitudinal[0].ProjectBottom = 0;
        project.Longitudinal[1].Water = 0;
        ProjectFile.Save(FilePath, project);
        var restored = ProjectFile.Load(FilePath);
        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal(ProfileKind.Longitudinal, restored.Kind);
        Assert.Equal(project.Settings.Title, restored.Settings.Title);
        Assert.Equal(-5, restored.Settings.Datum);
        Assert.Equal(0.73, restored.Settings.TextWidthFactor);
        Assert.Equal(2, restored.Longitudinal.Count);
        Assert.Equal(0, restored.Longitudinal[0].ProjectBottom);
        Assert.Null(restored.Longitudinal[0].Water);
        Assert.Null(restored.Longitudinal[1].ProjectBottom);
        Assert.Equal(0, restored.Longitudinal[1].Water);
        Assert.NotEmpty(ProfileEngine.Build(restored).Entities);
    }

    [Fact]
    public void CrossSectionRoundtripPreservesOptionalDesignAndAllGeometryParameters()
    {
        var project = SampleProjects.CrossSections();
        ProjectFile.Save(FilePath, project);
        var restored = ProjectFile.Load(FilePath);
        Assert.Equal(project.Kind, restored.Kind);
        Assert.Equal(project.Sections.Count, restored.Sections.Count);
        for (var i = 0; i < project.Sections.Count; i++)
        {
            var expected = project.Sections[i];
            var actual = restored.Sections[i];
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Station, actual.Station);
            Assert.Equal(expected.Water, actual.Water);
            Assert.Equal(expected.ProjectBottom, actual.ProjectBottom);
            Assert.Equal(expected.BottomWidth, actual.BottomWidth);
            Assert.Equal(expected.LeftSlope, actual.LeftSlope);
            Assert.Equal(expected.RightSlope, actual.RightSlope);
            Assert.Equal(expected.CenterOffset, actual.CenterOffset);
            Assert.Equal(expected.Points.Count, actual.Points.Count);
            for (var j = 0; j < expected.Points.Count; j++)
            {
                Assert.Equal(expected.Points[j].Offset, actual.Points[j].Offset);
                Assert.Equal(expected.Points[j].Existing, actual.Points[j].Existing);
                Assert.Equal(expected.Points[j].Design, actual.Points[j].Design);
            }
        }
        Assert.NotEmpty(ProfileEngine.Build(restored).Entities);
    }

    [Fact]
    public void UnknownSchemaVersionIsRejected()
    {
        var json = SavedDocument();
        json[Property(json, "SchemaVersion")] = 999;
        File.WriteAllText(FilePath, json.ToJsonString());
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
    }

    [Fact]
    public void MissingSchemaVersionIsRejectedRatherThanAssumedCurrent()
    {
        var json = SavedDocument();
        json.Remove(Property(json, "SchemaVersion"));
        File.WriteAllText(FilePath, json.ToJsonString());
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
    }

    [Fact]
    public void UnknownRootFieldIsRejected()
    {
        var json = SavedDocument();
        json["unexpectedTypoField"] = 12;
        File.WriteAllText(FilePath, json.ToJsonString());
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
    }

    [Fact]
    public void UnknownNestedFieldIsRejected()
    {
        var json = SavedDocument();
        json[Property(json, "Settings")]!.AsObject()["horizontalScael"] = 500;
        File.WriteAllText(FilePath, json.ToJsonString());
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejected()
    {
        var json = SavedDocument();
        var versionKey = Property(json, "SchemaVersion");
        var text = json.ToJsonString();
        File.WriteAllText(FilePath, "{\"" + versionKey + "\":1," + text.Substring(1));
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
    }

    [Fact]
    public void NullSettingsAreRejectedWithValidationError()
    {
        var json = SavedDocument();
        json[Property(json, "Settings")] = null;
        File.WriteAllText(FilePath, json.ToJsonString());
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
    }

    [Fact]
    public void JsonNumericStringsAreRejectedRatherThanSilentlyCoerced()
    {
        var json = SavedDocument();
        var settings = json[Property(json, "Settings")]!.AsObject();
        settings[Property(settings, "HorizontalScale")] = "1000";
        File.WriteAllText(FilePath, json.ToJsonString());
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
    }

    [Fact]
    public void UnknownProfileKindIsRejected()
    {
        var json = SavedDocument();
        json[Property(json, "Kind")] = "FutureProfileKind";
        File.WriteAllText(FilePath, json.ToJsonString());
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{} trailing")]
    public void MalformedJsonHasAnActionableValidationError(string text)
    {
        File.WriteAllText(FilePath, text);
        var error = Assert.Throws<ProfileValidationException>(() => ProjectFile.Load(FilePath));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void InvalidSavePreservesExistingFileBytes()
    {
        ProjectFile.Save(FilePath, Longitudinal(10, 12));
        var original = File.ReadAllBytes(FilePath);
        var invalid = Longitudinal(10, 12);
        invalid.Longitudinal[1].Ground = double.NaN;
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Save(FilePath, invalid));
        Assert.Equal(original, File.ReadAllBytes(FilePath));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void IncompleteDraftRoundtripsButCannotBeDrawn()
    {
        var draft = new ProfileProject { Kind = ProfileKind.CrossSection };
        draft.Settings.Title = "";
        draft.Sections.Add(new CrossSection { Name = "", ProjectBottom = 5 });
        ProjectFile.Save(FilePath, draft);
        var restored = ProjectFile.Load(FilePath);
        Assert.Empty(restored.Settings.Title);
        Assert.Single(restored.Sections);
        Assert.Equal(5, restored.Sections[0].ProjectBottom);
        Assert.Null(restored.Sections[0].BottomWidth);
        Assert.Empty(restored.Sections[0].Points);
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(restored));
    }

    [Fact]
    public void UnfinishedInactiveDatasetDoesNotBlockActiveDrawing()
    {
        var project = Longitudinal(10, 12);
        project.Sections.Add(new CrossSection { Name = "", ProjectBottom = 5 });
        Assert.NotEmpty(ProfileEngine.Build(project).Entities);
        ProjectFile.Save(FilePath, project);
        var restored = ProjectFile.Load(FilePath);
        Assert.NotEmpty(ProfileEngine.Build(restored).Entities);
        restored.Kind = ProfileKind.CrossSection;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(restored));
    }

    [Fact]
    public void RepeatedAtomicSaveReplacesProjectAndLeavesNoTemporaryFiles()
    {
        var first = Longitudinal(10, 12);
        first.Settings.Title = "Первый";
        ProjectFile.Save(FilePath, first);
        var second = Longitudinal(11, 13);
        second.Settings.Title = "Второй";
        ProjectFile.Save(FilePath, second);
        Assert.Equal("Второй", ProjectFile.Load(FilePath).Settings.Title);
        Assert.Equal(11, ProjectFile.Load(FilePath).Longitudinal[0].Ground);
        Assert.Single(Directory.GetFiles(directory));
    }
}
