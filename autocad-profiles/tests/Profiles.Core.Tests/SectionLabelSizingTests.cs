using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class SectionLabelSizingTests
{
    [Fact]
    public void DenseGroupDoesNotShrinkDistantElevationOrPointLabels()
    {
        var loose = Labels(0, 2, 4, 10);
        var dense = Labels(0, .01, .02, 10);
        var far = dense.Single(t => t.Text == "104.00" && t.Position.Y < 0);
        Assert.Equal(loose.Single(t => t.Text == "104.00" && t.Position.Y < 0).Height, far.Height, 9);
        Assert.Equal(.25, far.Height, 9);
        Assert.Contains(dense, t => t.Text == "102.00" && t.Position.Y < 0 && t.Height < far.Height);
        Assert.Equal(loose.Single(t => t.Text == "4").Height, dense.Single(t => t.Text == "4").Height, 9);
    }

    [Theory]
    [InlineData(.001)]
    [InlineData(.01)]
    [InlineData(.1)]
    [InlineData(1)]
    [InlineData(10)]
    public void DistanceFontFitsItsOwnIntervalAndNeverExceedsConfiguredHeight(double interval)
    {
        var texts = Labels(0, interval);
        var caption = texts.Single(t => t.Text == "РАССТОЯНИЕ, м");
        var number = texts.Single(t => t.Position.X >= 0 && Math.Abs(t.Position.Y - caption.Position.Y) < 1e-9);
        Assert.InRange(number.Height, double.Epsilon, .25);
        Assert.True(number.Text.Length * number.Height * .75 * .85 <= interval + 1e-9);
        Assert.Equal(interval / 2, number.Position.X, 9);
        Assert.Equal(0, number.RotationDegrees);
    }

    [Fact]
    public void WideningIntervalCannotMakeItsNumberSmaller()
    {
        double previous = 0;
        foreach (double interval in new[] { .001, .01, .1, .5, 1.0, 2, 10 })
        {
            var labels = Labels(0, interval);
            var row = labels.Single(t => t.Text == "РАССТОЯНИЕ, м");
            var text = labels.Single(t => t.Position.X >= 0 && Math.Abs(t.Position.Y - row.Position.Y) < 1e-9);
            Assert.True(text.Height >= previous);
            previous = text.Height;
        }
    }

    private static List<DrawText> Labels(params double[] offsets)
    {
        var project = new ProfileProject
        {
            Kind = ProfileKind.CrossSection,
            Settings = new ProfileSettings { CrossHorizontalScale = 100, CrossVerticalScale = 100, TextHeightMm = 2.5, TextWidthFactor = .85 },
            Sections = new List<CrossSection> { new() { Name = "Промер", DesignMode = SectionDesignMode.None,
                Points = offsets.Select((x, i) => new SectionPoint { Offset = x, Existing = 101 + i }).ToList() } }
        };
        return ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();
    }
}
