using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

/// <summary>
/// Collectors flowing into each other, as «К-р 3'» and «К-р 3-2'» on sheet 28:
/// the tributary starts at the collector's surface and drain elevation at the
/// junction, shows the collector's number at its ordinate 0, and keeps its slopes
/// when the collector's design changes.
/// </summary>
public sealed class DrainageNetworkTests
{
    private static ProfileProject Network() => SampleProjects.Drainage();

    private static ProfileData Tributary(ProfileProject project) => project.AdditionalProfiles.Single(profile => profile.Name == "К-р 3-2'");

    [Theory]
    [InlineData("К-р 3-2'", "3-2")]
    [InlineData("К-р 4'", "4")]
    [InlineData("к-р 25", "25")]
    [InlineData("Дрена 12", "12")]
    [InlineData("Кр3’", "3")]
    [InlineData("Д-1", "Д-1")]
    [InlineData("Кр-1", "1")]
    [InlineData("Др.5", "5")]
    // A word that only starts like a prefix is a name, not a prefix and a number.
    [InlineData("Кривой ручей", "Кривой ручей")]
    [InlineData("Дренажная канава 2", "Дренажная канава 2")]
    public void CollectorNumbersComeFromTheirNames(string name, string number)
    {
        Assert.Equal(number, DrainageNetwork.Number(name));
    }

    [Fact]
    public void TributaryOutletTakesTheCollectorElevationsAndReceiverLine()
    {
        var project = Network();
        var outlet = Tributary(project).Longitudinal[0];
        // Sheet 28: «К-р 3-2'» starts at 146.90 / 145.60, the values of «К-р 3'» at 38 m.
        Assert.Equal(146.90, outlet.Ground);
        Assert.Equal(145.60, outlet.ProjectBottom);
        Assert.Equal("к-р 3 пк0+38 к-л К-2", Tributary(project).Settings.Subtitle);
        var link = Assert.Single(DrainageNetwork.Links(project));
        Assert.Equal("3", link.MainNumber);
        Assert.Equal("3-2", link.TributaryNumber);
        Assert.Equal(38, link.Station);
    }

    [Fact]
    public void ChangingTheCollectorMovesTheTributaryWithItsSlopes()
    {
        var project = Network();
        project.Longitudinal[1].ProjectBottom = 145.70; // the junction of «3-2» 10 cm higher
        project.Longitudinal[1].Ground = 146.92;
        var changes = new List<DrainageNetwork.Change>();
        var synced = DrainageNetwork.Synchronize(project, changes);
        var rows = Tributary(synced).Longitudinal;
        Assert.Equal(145.70, rows[0].ProjectBottom.Value, 9);
        Assert.Equal(146.05, rows[1].ProjectBottom.Value, 9); // 145.95 + 0.10: the 0.0058 slope is kept
        // The outlet is a point of the collector: its surface follows too.
        Assert.Equal(146.92, rows[0].Ground);
        var change = Assert.Single(changes);
        Assert.Equal(Tributary(project).ProfileId, change.ProfileId);
        Assert.Equal(0.1, change.Shift.Value, 9);
        Assert.Equal("«К-р 3-2'» перестроен по «К-р 3'» на ПК 0+38: дрена в устье 145.70 (отметки дрены смещены на +0.10 м, уклоны сохранены), " +
            "поверхность в устье 146.92 (было 146.90).", change.Text);
        // Synchronising again changes nothing.
        changes.Clear();
        DrainageNetwork.Synchronize(synced, changes);
        Assert.Empty(changes);
    }

    [Fact]
    public void ReceiverLineFollowsAMovedJunctionAndKeepsOwnText()
    {
        var project = Network();
        project.Longitudinal[1].PointName = "";
        project.Longitudinal[2].PointName = "3-2"; // moved from 38 m to 72 m
        Assert.Equal("к-р 3 пк0+72 к-л К-2", Tributary(DrainageNetwork.Synchronize(project)).Settings.Subtitle);
        // Own text of the designer is kept unless it names a collector and picket.
        project = Network();
        Tributary(project).Settings.Subtitle = "в к-л К-2 через к-р 3";
        Assert.Equal("в к-л К-2 через к-р 3", Tributary(DrainageNetwork.Synchronize(project)).Settings.Subtitle);
        project = Network();
        Tributary(project).Settings.Subtitle = "к-р 3 пк0+38";
        Assert.Equal("к-р 3 пк0+38", Tributary(DrainageNetwork.Synchronize(project)).Settings.Subtitle);
    }

    [Fact]
    public void MembersListTheDrainageTabsWithLabelsAndReceivers()
    {
        var project = Network();
        var members = DrainageNetwork.Members(project);
        Assert.Equal(new[] { "К-р 3'", "К-р 3-2'" }, members.Select(member => member.Name));
        Assert.Equal(new[] { "3", "3-2" }, members.Select(member => member.Label));
        Assert.Null(members[0].Receiver);
        Assert.Equal(38, members[1].Receiver.Station);
        Assert.True(members[1].Matches("3-2") && members[1].Matches("к-р 3-2'") && !members[1].Matches("3"));
        Assert.Equal("К-р 3-1'", DrainageNetwork.TributaryName(project, project.ProfileId));
        Assert.Equal("К-р 3-2-1'", DrainageNetwork.TributaryName(project, members[1].ProfileId));
    }

    [Fact]
    public void JunctionBetweenGradeBreaksUsesTheInterpolatedDrainElevation()
    {
        var project = Network();
        project.Longitudinal[1].PointName = "";
        project.Longitudinal[2].PointName = "3-2"; // 72 m, between 145.60 at 38 m and 146.27 at 150 m
        var rows = Tributary(DrainageNetwork.Synchronize(project)).Longitudinal;
        // Rounded to the millimetre, as design elevations are written.
        Assert.Equal(Math.Round(145.60 + (146.27 - 145.60) * 34 / 112, 3), rows[0].ProjectBottom.Value, 9);
    }

    [Fact]
    public void TributaryShowsTheCollectorNumberAtOrdinateZero()
    {
        var network = Network();
        var project = ProfileCollection.Select(network, Tributary(network).ProfileId);
        var plan = ProfileEngine.Build(project);
        var marker = Assert.Single(plan.Entities.OfType<DrawPolyline>(), line => line.Layer == "PROF_MARKS" && line.Points.All(point => point.X == 0));
        Assert.Equal(0, marker.Points[0].Y);
        var label = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == "3");
        Assert.Equal(90, label.RotationDegrees);
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "0.0058");
        // «к-р 3 пк0+38 к-л К-2» is 40.6 mm in ISOCPEUR at 3 mm, wider than the 37.15 mm corner:
        // it wraps by words, and «к-л» goes down with the canal it names.
        var first = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == "к-р 3 пк0+38");
        var second = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == "к-л К-2");
        Assert.Equal(first.Position.X, second.Position.X, 9);
        Assert.Equal(5 * 0.1, first.Position.Y - second.Position.Y, 9);
        // The collector shows the tributary's number at the junction.
        var main = ProfileEngine.Build(network);
        Assert.Contains(main.Entities.OfType<DrawText>(), text => text.Text == "3-2" && text.RotationDegrees == 90);
    }

    [Fact]
    public void AllProfilesBuildEachTributaryWithItsCollector()
    {
        var plan = ProfileBuildScope.Build(Network(), null, false, true);
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "3" && text.RotationDegrees == 90);
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "3-2" && text.RotationDegrees == 90);
        var groups = ProfileBuildScope.Groups(Network(), null, false, true);
        Assert.All(groups, group => Assert.Equal(0.1, group.PaperUnits, 9));
    }

    [Fact]
    public void CyclesDuplicatesAndAmbiguousLabelsCreateNoLinks()
    {
        var project = Network();
        // «К-р 3-2'» names «К-р 3'» as flowing into it: a circle.
        Tributary(project).Longitudinal[1].PointName = "3";
        var warnings = new List<string>();
        var links = DrainageNetwork.Links(project, warnings);
        Assert.Single(links);
        Assert.Contains(warnings, warning => warning.Contains("по кругу"));
        // The same tributary at a second junction: the first link is used.
        project = Network();
        project.Longitudinal[3].PointName = "3-2";
        warnings.Clear();
        links = DrainageNetwork.Links(project, warnings);
        Assert.Equal(38, Assert.Single(links).Station);
        Assert.Contains(warnings, warning => warning.Contains("Используется первая связь"));
        // A plain label that names no drainage profile stays a label.
        project = Network();
        project.Longitudinal[3].PointName = "7-1";
        Assert.Single(DrainageNetwork.Links(project));
        Assert.Contains(ProfileEngine.Build(project).Entities.OfType<DrawText>(), text => text.Text == "7-1");
    }

    [Fact]
    public void SeveralCollectorsCanJoinAtOnePoint()
    {
        var network = Network();
        var project = ProfileCollection.Add(network, "К-р 3-4'");
        project = ProfileCollection.ChangeTemplate(project, project.ProfileId, ProfileTemplate.Drainage);
        project.Longitudinal.Add(new LongitudinalPoint { Station = 0 });
        project.Longitudinal.Add(new LongitudinalPoint { Station = 80, Ground = 148.9, ProjectBottom = 147.2 });
        project = ProfileCollection.Select(project, network.ProfileId);
        project.Longitudinal[1].PointName = "3-2, 3-4";
        var links = DrainageNetwork.Links(project);
        Assert.Equal(new[] { "3-2", "3-4" }, links.Select(link => link.TributaryNumber).OrderBy(number => number));
        var synced = DrainageNetwork.Synchronize(project);
        var added = synced.AdditionalProfiles.Single(profile => profile.Name == "К-р 3-4'");
        Assert.Equal(145.60, added.Longitudinal[0].ProjectBottom);
        Assert.Equal(146.90, added.Longitudinal[0].Ground);
    }

    [Theory]
    // Values written on sheets 27 and 28 for elevations given to the centimetre.
    [InlineData(38, 145.52, 145.60, "0.002")]
    [InlineData(112, 145.60, 146.27, "0.006")]
    [InlineData(94, 146.27, 146.46, "0.002")]
    [InlineData(60, 145.60, 145.95, "0.0058")]
    [InlineData(280, 146.22, 147.32, "0.0039")]
    [InlineData(108, 147.32, 147.70, "0.0035")]
    [InlineData(200, 144.10, 144.50, "0.002")]
    [InlineData(50, 145.00, 145.00, "0")]
    public void SlopesAreTheDesignSlopesOfTheRoundedElevations(double length, double first, double second, string expected)
    {
        Assert.Equal(expected, DrainageSlope.Text(length, first, true, second, true));
    }

    [Fact]
    public void ExactOrDerivedElevationsKeepTheExactSlope()
    {
        // Millimetre input narrows the tolerance; a derived end has none.
        Assert.Equal("0.0021", DrainageSlope.Text(38, 145.521, true, 145.601, true));
        Assert.Equal("0.0021", DrainageSlope.Text(38, 145.52, false, 145.60, false));
    }
}
