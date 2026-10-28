using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class ProfileEndExtrapolationTests
{
    [Fact]
    public void EndUsesLastTwoRawAnchorsPerFieldAndActualStationSpacing()
    {
        var raw = new List<LongitudinalPoint>
        {
            new() { Station = -10 },
            new() { Station = 0, ProjectBottom = 100, ExistingBottom = 110, ProjectGround = 120, PipeInvert = 90 },
            new() { Station = 10, ExistingBottom = 108 },
            new() { Station = 30, ProjectBottom = 97, ProjectGround = 126, PipeInvert = 84 },
            new() { Station = 45 }, new() { Station = 90 }
        };
        var result = ProfileInterpolation.ResolveLongitudinal(raw);
        Assert.Null(result[0].ProjectBottom);
        Assert.Equal(95.5, result[4].ProjectBottom);
        Assert.Equal(101, result[4].ExistingBottom);
        Assert.Equal(129, result[4].ProjectGround);
        Assert.Equal(81, result[4].PipeInvert);
        Assert.Equal(91, result[5].ProjectBottom);
        Assert.Equal(result[4].ProjectBottom, ProfileInterpolation.SampleLongitudinal(raw, 45).ProjectBottom);
        Assert.Equal(result[4].ExistingBottom, ProfileInterpolation.CreateSection(raw, 45).ExistingBottom);
        Assert.Null(raw[4].ProjectBottom);
        Assert.Null(raw[4].ExistingBottom);
        raw[3].ProjectBottom = 94;
        Assert.Equal(91, ProfileInterpolation.ResolveLongitudinal(raw)[4].ProjectBottom);
        raw[4].ProjectBottom = 93; // An explicit new endpoint wins and becomes the next anchor.
        Assert.Equal(93, ProfileInterpolation.ResolveLongitudinal(raw)[4].ProjectBottom);
        Assert.Equal(90, ProfileInterpolation.SampleLongitudinal(raw, 90).ProjectBottom);
    }

    [Fact]
    public void SingleAnchorContinuesItsHeightAndUnknownFieldsStayUnknown()
    {
        var raw = new List<LongitudinalPoint>
        {
            new() { Station = 0, ProjectBottom = 0, Ground = 10, Water = 5, BottomWidth = 2 },
            new() { Station = 20 }, new() { Station = 75 }
        };
        var resolved = ProfileInterpolation.ResolveLongitudinal(raw);
        Assert.Equal(0, resolved[2].ProjectBottom);
        Assert.Equal(0, ProfileInterpolation.SampleLongitudinal(raw, 100).ProjectBottom);
        Assert.Null(resolved[2].ExistingBottom);
        Assert.Null(resolved[2].Ground);
        Assert.Null(resolved[2].Water);
        Assert.Null(resolved[2].BottomWidth);
        Assert.Null(ProfileInterpolation.SampleLongitudinal(raw, -1).ProjectBottom);
        Assert.Null(raw[2].ProjectBottom);
        Assert.Null(ProfileInterpolation.SampleLongitudinal(new List<LongitudinalPoint>(), 10).ProjectBottom);
    }

    [Fact]
    public void DrawingContinuesBothBottomCurvesToTheNewStation()
    {
        var project = new ProfileProject
        {
            Longitudinal = new()
            {
                new() { Station = 0, ExistingBottom = 110, ProjectBottom = 108 },
                new() { Station = 20, ExistingBottom = 108, ProjectBottom = 107 },
                new() { Station = 50 }
            }
        };
        var plan = ProfileEngine.Build(project);
        foreach (string layer in new[] { "PROF_EXISTING", "PROF_PROJECT" })
        {
            var line = Assert.Single(plan.Entities.OfType<DrawPolyline>().Where(entity => entity.Layer == layer));
            Assert.Equal(3, line.Points.Count);
            Assert.Equal(50, line.Points.Last().X - line.Points.First().X, 8);
        }
        Assert.Null(project.Longitudinal[2].ExistingBottom);
        Assert.Null(project.Longitudinal[2].ProjectBottom);
    }

    [Fact]
    public void JsonRoundTripRetainsDerivedCellsAndLinkedSectionsRecalculate()
    {
        var project = new ProfileProject
        {
            Longitudinal = new()
            {
                new() { Station = 0, ExistingBottom = 10, ProjectBottom = 8 },
                new() { Station = 20, ExistingBottom = 12, ProjectBottom = 9 },
                new() { Station = 50 }
            }
        };
        project = ProjectFile.Deserialize(ProjectFile.Serialize(project));
        var section = ProfileInterpolation.CreateSection(project.Longitudinal, 50);
        section.Points[1].DistanceFromPrevious = 2;
        section.Points[2].DistanceFromPrevious = 3;
        Assert.Equal(10.5, ProfileInterpolation.ResolveSection(section, project.Longitudinal).ProjectBottom);
        project.Longitudinal[1].ProjectBottom = 10;
        Assert.Equal(13, ProfileInterpolation.ResolveSection(section, project.Longitudinal).ProjectBottom);
        Assert.Null(project.Longitudinal[2].ProjectBottom);
    }
}
