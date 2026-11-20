using System;
using System.IO;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class ProjectLibraryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "profiles-library-tests-" + Guid.NewGuid().ToString("N"));
    private DateTime now = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
    private const string DrawingA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DrawingB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private ProjectLibrary Library() => new ProjectLibrary(directory, () => now);

    private static ProfileProject Project(string name = "Канал", double bottom = 100)
    {
        var project = SampleProjects.Longitudinal();
        project.ProjectId = Guid.NewGuid().ToString("N");
        project.Name = name;
        project.Longitudinal[0].ExistingBottom = bottom;
        return ProjectFile.Clone(project);
    }

    private static ProfileProject Edit(ProfileProject project, double bottom)
    {
        var copy = ProjectFile.Clone(project);
        copy.Longitudinal[0].ExistingBottom = bottom;
        return copy;
    }

    [Fact]
    public void SavedProjectIsOrdinaryImportableJsonAndUnchangedContentIsNotRewritten()
    {
        var library = Library();
        var project = Project();
        var first = library.Save(project, drawingName: "план.dwg");
        Assert.Equal(1, first.Sequence);
        Assert.Equal("план.dwg", first.SourceDrawing);
        var file = Path.Combine(library.ProjectsPath, project.ProjectId + ".json");
        Assert.Equal(100, ProjectFile.Load(file).Longitudinal[0].ExistingBottom);
        var written = File.GetLastWriteTimeUtc(file);
        now = now.AddMinutes(10);
        var second = library.Save(ProjectFile.Clone(project));
        Assert.Equal(1, second.Sequence);
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Single(library.Versions(project.ProjectId));
        Assert.Equal(100, library.Load(project.ProjectId).Longitudinal[0].ExistingBottom);
    }

    [Fact]
    public void HistoryKeepsTheOriginalThenOneStateEveryThreeMinutesAndCheckpoints()
    {
        var library = Library();
        var project = Project();
        library.Save(project);
        for (int i = 1; i <= 10; i++)
        {
            now = now.AddSeconds(10);
            library.Save(Edit(project, 100 + i));
        }
        // The first change keeps the original; the following 90 seconds of typing add nothing.
        var versions = library.Versions(project.ProjectId);
        Assert.Equal(2, versions.Count);
        Assert.True(versions[0].IsCurrent);
        Assert.Equal(100, library.LoadVersion(project.ProjectId, versions[1].VersionId).Longitudinal[0].ExistingBottom);
        now = now.AddMinutes(4);
        library.Save(Edit(project, 200));
        Assert.Equal(3, library.Versions(project.ProjectId).Count);
        now = now.AddSeconds(5);
        library.Save(Edit(project, 201), checkpoint: true);
        versions = library.Versions(project.ProjectId);
        Assert.Equal(4, versions.Count);
        Assert.Equal(200, library.LoadVersion(project.ProjectId, versions[1].VersionId).Longitudinal[0].ExistingBottom);
        Assert.Equal(201, library.Load(project.ProjectId).Longitudinal[0].ExistingBottom);
    }

    [Fact]
    public void HistoryIsBoundedAndNeverStoresTheSameStateTwice()
    {
        var library = Library();
        var project = Project();
        library.Save(project);
        for (int i = 1; i <= 60; i++)
        {
            now = now.AddMinutes(5);
            library.Save(Edit(project, 100 + i));
        }
        Assert.Equal(41, library.Versions(project.ProjectId).Count);
        library.Checkpoint(project.ProjectId);
        library.Checkpoint(project.ProjectId);
        var versions = library.Versions(project.ProjectId);
        Assert.Equal(41, versions.Count);
        Assert.Equal(160, library.LoadVersion(project.ProjectId, versions[1].VersionId).Longitudinal[0].ExistingBottom);
    }

    [Fact]
    public void DrawingProjectsEnterTheLibraryAndAppearInOtherDrawings()
    {
        var library = Library();
        var project = Project("Канал А");
        var openA = library.Synchronize(new[] { project }, DrawingA, "А.dwg");
        Assert.Single(openA.DrawingProjects);
        Assert.Empty(openA.Restored);
        Assert.Empty(openA.LibraryOnly);
        var openB = library.Synchronize(Array.Empty<ProfileProject>(), DrawingB, "Б.dwg");
        var entry = Assert.Single(openB.LibraryOnly);
        Assert.Equal(project.ProjectId, entry.ProjectId);
        Assert.Equal("Канал А", entry.Name);
        Assert.Equal("А.dwg", entry.SourceDrawing);
        Assert.Equal(100, library.Load(entry.ProjectId).Longitudinal[0].ExistingBottom);
    }

    [Fact]
    public void UndoOrUnsavedDrawingYieldsTheNewerAutosavedVersion()
    {
        var library = Library();
        var original = Project();
        library.Synchronize(new[] { original }, DrawingA, "А.dwg");
        // Edits during the command: each one reached the DWG and the library.
        var edited = original;
        for (int i = 1; i <= 3; i++)
        {
            now = now.AddSeconds(30);
            edited = Edit(original, 100 + i);
            library.Save(edited, drawingKey: DrawingA, drawingName: "А.dwg");
        }
        // UNDO (or closing AutoCAD without saving the DWG) returns the drawing to the original.
        now = now.AddMinutes(1);
        var reopened = library.Synchronize(new[] { ProjectFile.Clone(original) }, DrawingA, "А.dwg");
        Assert.Contains(original.ProjectId, reopened.Restored);
        Assert.Equal(103, reopened.DrawingProjects.Single().Longitudinal[0].ExistingBottom);
        Assert.Contains("более ранняя версия", reopened.Messages.Single());
        Assert.Equal(LibraryProjectHash(original), reopened.DrawingHashes[original.ProjectId]);
        // An intermediate state (one UNDO step back) is also recognised.
        var partial = library.Synchronize(new[] { Edit(original, 102) }, DrawingA, "А.dwg");
        Assert.Contains(original.ProjectId, partial.Restored);
    }

    [Fact]
    public void ForeignDrawingChangeWinsAndTheLibraryVersionIsKept()
    {
        var library = Library();
        var project = Project();
        library.Synchronize(new[] { project }, DrawingA, "А.dwg");
        now = now.AddSeconds(20);
        library.Save(Edit(project, 150), drawingKey: DrawingA);
        // Another computer saved a different edit in the same DWG.
        now = now.AddMinutes(1);
        var foreign = Edit(project, 175);
        var result = library.Synchronize(new[] { foreign }, DrawingA, "А.dwg");
        Assert.Empty(result.Restored);
        Assert.Equal(175, result.DrawingProjects.Single().Longitudinal[0].ExistingBottom);
        Assert.Equal(175, library.Load(project.ProjectId).Longitudinal[0].ExistingBottom);
        var versions = library.Versions(project.ProjectId);
        Assert.Contains(versions.Skip(1), version => library.LoadVersion(project.ProjectId, version.VersionId).Longitudinal[0].ExistingBottom == 150);
        Assert.Contains("другая версия", result.Messages.Single());
    }

    [Fact]
    public void ADrawingCopyNeverReplacesNewerWorkMadeInAnotherDrawing()
    {
        var library = Library();
        var project = Project();
        library.Synchronize(new[] { project }, DrawingA, "А.dwg");
        // Opened in drawing B from the library and edited there.
        now = now.AddMinutes(1);
        library.Save(Edit(project, 120), drawingKey: DrawingB, drawingName: "Б.dwg");
        // Drawing A still holds the state recorded for it earlier.
        var result = library.Synchronize(new[] { ProjectFile.Clone(project) }, DrawingA, "А.dwg");
        Assert.Contains(project.ProjectId, result.Restored);
        Assert.Equal(120, result.DrawingProjects.Single().Longitudinal[0].ExistingBottom);
    }

    [Fact]
    public void DeletedProjectsCanBeRestoredWithTheirHistory()
    {
        var library = Library();
        var project = Project("Лишний");
        library.Save(project);
        now = now.AddMinutes(5);
        library.Save(Edit(project, 111));
        library.Delete(project.ProjectId);
        Assert.Empty(library.List());
        Assert.Null(library.Get(project.ProjectId));
        var deleted = Assert.Single(library.ListDeleted());
        Assert.Equal("Лишний", deleted.Name);
        Assert.NotNull(deleted.DeletedUtc);
        var restored = library.RestoreDeleted(deleted.DeletedId);
        Assert.Equal(111, restored.Longitudinal[0].ExistingBottom);
        Assert.Single(library.List());
        Assert.Empty(library.ListDeleted());
        Assert.True(library.Versions(project.ProjectId).Count >= 2);
    }

    [Fact]
    public void JsonLinkSurvivesNewEditorSessions()
    {
        var library = Library();
        var project = Project();
        library.Save(project);
        string path = Path.Combine(directory, "Канал.json");
        library.SetJsonLink(project.ProjectId, path, true);
        now = now.AddMinutes(1);
        library.Save(Edit(project, 101));
        var entry = new ProjectLibrary(directory).Get(project.ProjectId);
        Assert.Equal(Path.GetFullPath(path), entry.JsonPath);
        Assert.True(entry.JsonAutoSave);
        library.SetJsonLink(project.ProjectId, path, false);
        Assert.False(library.Get(project.ProjectId).JsonAutoSave);
    }

    [Fact]
    public void DamagedSidecarIsRepairedFromTheProjectFile()
    {
        var library = Library();
        var project = Project("Канал Б");
        library.Save(project);
        File.WriteAllText(Path.Combine(library.ProjectsPath, project.ProjectId + ".meta.json"), "{ broken");
        var entry = library.Get(project.ProjectId);
        Assert.Equal("Канал Б", entry.Name);
        now = now.AddMinutes(1);
        var saved = library.Save(Edit(project, 130));
        Assert.Equal(130, library.Load(project.ProjectId).Longitudinal[0].ExistingBottom);
        Assert.True(saved.Sequence > entry.Sequence - 1);
        Assert.Single(library.List());
    }

    [Fact]
    public void DamagedProjectFileDoesNotBlockOpeningTheDrawing()
    {
        var library = Library();
        var project = Project();
        library.Save(project);
        File.WriteAllText(Path.Combine(library.ProjectsPath, project.ProjectId + ".json"), "{ broken");
        var result = library.Synchronize(new[] { project }, DrawingA, "А.dwg");
        Assert.Equal(100, result.DrawingProjects.Single().Longitudinal[0].ExistingBottom);
        Assert.Contains(result.Messages, message => message.StartsWith("Автосохранение", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidIdentifiersNeverBecomeFileNames()
    {
        var library = Library();
        Assert.Throws<ProfileValidationException>(() => library.Get("..\\..\\x"));
        Assert.Throws<ProfileValidationException>(() => library.RestoreDeleted("..\\x"));
        Assert.Throws<ProfileValidationException>(() => library.LoadVersion(Guid.NewGuid().ToString("N"), "../../x"));
    }

    private static string LibraryProjectHash(ProfileProject project) => ProjectLibrary.Hash(ProjectFile.Serialize(project));
}
