using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

/// <summary>
/// ProjectFile.Clone copies the object graph directly. Every public member of
/// every model type must reach the copy: a new field without copy code fails here.
/// </summary>
public sealed class ProjectCopyTests
{
    private static int counter;

    private static object Value(Type type, string name)
    {
        counter++;
        Type nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null) type = nullable;
        if (type == typeof(double)) return name.Contains("Scale") ? 100.0 + counter : name == "TextHeightMm" ? 3.0 : name == "TextWidthFactor" ? 0.9 :
            name == "MetresPerDrawingUnit" ? 0.5 : name.Contains("Slope") || name.Contains("Width") || name == "PeatDepth" || name == "SiltThickness" ? 1.0 + counter % 7 : counter * 1.25;
        if (type == typeof(int)) return 0;
        if (type == typeof(bool)) return true;
        if (type == typeof(string)) return name.EndsWith("Id") ? Guid.NewGuid().ToString("N") : name + " " + counter;
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().Last();
        throw new InvalidOperationException(type.Name);
    }

    private static T Fill<T>() where T : new() => (T)Fill(typeof(T));

    private static object Fill(Type type)
    {
        var result = Activator.CreateInstance(type);
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanWrite) continue;
            var propertyType = property.PropertyType;
            if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)Activator.CreateInstance(propertyType);
                Type item = propertyType.GetGenericArguments()[0];
                if (item != typeof(ProfileData)) for (int i = 0; i < 2; i++) list.Add(Fill(item));
                property.SetValue(result, list);
            }
            else if (propertyType.IsClass && propertyType != typeof(string)) property.SetValue(result, Fill(propertyType));
            else property.SetValue(result, Value(propertyType, property.Name));
        }
        return result;
    }

    private static ProfileProject Project()
    {
        var project = Fill<ProfileProject>();
        project.SchemaVersion = 5;
        project.Kind = ProfileKind.Longitudinal;
        project.ActiveProfileIndex = 1;
        project.AdditionalProfiles = new List<ProfileData> { Fill<ProfileData>(), Fill<ProfileData>() };
        project.AdditionalProfiles[1].Name = "Другой";
        project.ProfileName = "Активный";
        foreach (var points in new[] { project.Longitudinal }.Concat(project.AdditionalProfiles.Select(p => p.Longitudinal)))
            for (int i = 0; i < points.Count; i++) points[i].Station = i * 10 + 0.5;
        foreach (var section in project.Sections.Concat(project.AdditionalProfiles.SelectMany(p => p.Sections)))
        {
            section.ProjectAnchorIndex = 1;
            section.SiltLeft = -5; section.SiltRight = 5;
            section.DesignMode = SectionDesignMode.Points;
            for (int i = 0; i < section.Points.Count; i++) { section.Points[i].Offset = i; section.Points[i].DistanceFromPrevious = i == 0 ? 0 : 1; }
        }
        return project;
    }

    [Fact]
    public void CloneCopiesEveryMemberOfEveryModelType()
    {
        var source = Project();
        var copy = ProjectFile.Clone(source);
        // Serialize writes every public member; equal JSON means nothing was dropped.
        Assert.Equal(ProjectFile.Serialize(source), ProjectFile.Serialize(copy));
        Assert.Equal(ProjectFile.Serialize(ProjectFile.Deserialize(ProjectFile.Serialize(source))), ProjectFile.Serialize(copy));
    }

    [Fact]
    public void CloneIsIndependentOfTheSource()
    {
        var source = Project();
        var copy = ProjectFile.Clone(source);
        Assert.NotSame(source.Settings, copy.Settings);
        Assert.NotSame(source.Longitudinal, copy.Longitudinal);
        Assert.NotSame(source.Longitudinal[0], copy.Longitudinal[0]);
        Assert.NotSame(source.Sections[0].Points[0], copy.Sections[0].Points[0]);
        Assert.NotSame(source.AdditionalProfiles[0].Sections[0], copy.AdditionalProfiles[0].Sections[0]);
        copy.Longitudinal[0].Water = -1;
        copy.Sections[0].Points[0].Label = "изменено";
        copy.AdditionalProfiles[0].Settings.Title = "изменено";
        Assert.NotEqual(-1, source.Longitudinal[0].Water);
        Assert.NotEqual("изменено", source.Sections[0].Points[0].Label);
        Assert.NotEqual("изменено", source.AdditionalProfiles[0].Settings.Title);
    }

    [Fact]
    public void CloneNormalisesIdentifiersLikeTheJsonRoundTrip()
    {
        var source = Project();
        var id = Guid.NewGuid();
        source.ProjectId = id.ToString("D").ToUpperInvariant();
        source.Sections[0].SectionId = source.Sections[0].SectionId.ToUpperInvariant();
        var copy = ProjectFile.Clone(source);
        Assert.Equal(id.ToString("N"), copy.ProjectId);
        Assert.Equal(source.Sections[0].SectionId.ToLowerInvariant(), copy.Sections[0].SectionId);
        Assert.Equal(ProjectFile.Serialize(ProjectFile.Deserialize(ProjectFile.Serialize(source))), ProjectFile.Serialize(copy));
    }

    [Fact]
    public void CloneRejectsWhatTheJsonSchemaRejects()
    {
        void Rejected(Action<ProfileProject> damage)
        {
            var source = Project();
            damage(source);
            Assert.Throws<ProfileValidationException>(() => ProjectFile.Clone(source));
        }
        Rejected(p => p.Longitudinal[0].Water = double.NaN);
        Rejected(p => p.Sections[0].Points[0].Existing = double.PositiveInfinity);
        Rejected(p => p.Settings.CrossVerticalScale = double.NaN);
        Rejected(p => p.Longitudinal[0].PointName = null);
        Rejected(p => p.Sections[0].Points[1].Label = null);
        Rejected(p => p.AdditionalProfiles[0].Sections = null);
        Rejected(p => p.AdditionalProfiles[0].Settings = null);
        Rejected(p => p.Sections[0].Points.Add(null));
        Rejected(p => p.Template = (ProfileTemplate)17);
        Rejected(p => p.AdditionalProfiles[1].Template = (ProfileTemplate)17);
        Rejected(p => p.Sections[0].Points[0].Role = (SectionPointRole)17);
        Rejected(p => p.Settings.Title = null);
        Rejected(p => p.ProjectId = null);
    }
}
