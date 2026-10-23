using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class ElevationTextParserTests
{
    [Theory]
    [InlineData("160.25", 160.25)]
    [InlineData("160,25", 160.25)]
    [InlineData("  Отм. 160,25 м  ", 160.25)]
    [InlineData("Отм.160.25", 160.25)]
    [InlineData("Ур.воды=160.25м", 160.25)]
    [InlineData("H = -12.125", -12.125)]
    [InlineData("−3,50", -3.5)]
    [InlineData("+.75", .75)]
    [InlineData("-.75", -.75)]
    [InlineData("0", 0)]
    [InlineData("Отметка\n+123.1234567890123", 123.1234567890123)]
    public void ReadsExactlyOnePlainElevationWithoutChangingPrecision(string text, double expected)
    {
        Assert.Equal(expected, ElevationTextParser.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Дно")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("160.2 159.1")]
    [InlineData("160.2 / 160.2")]
    [InlineData("ПК1+00.00")]
    [InlineData("1:1000")]
    [InlineData("1 234.50")]
    [InlineData("160.2.3")]
    [InlineData("160,2,3")]
    [InlineData("160.")]
    [InlineData(".160.")]
    [InlineData("1e3")]
    [InlineData("1e-3")]
    [InlineData("- 160.20")]
    [InlineData("±160.20")]
    [InlineData("%<\\AcExpr (160.2)>%")]
    [InlineData("{\\A1;160.20}")]
    [InlineData("\\S160/2;")]
    [InlineData("1000001")]
    [InlineData("-1000001")]
    public void RejectsAmbiguousMalformedOrNonElevationContent(string text)
    {
        Assert.Throws<ProfileValidationException>(() => ElevationTextParser.Parse(text));
    }

    [Fact]
    public void NoTextOrOversizedTextNeverBecomesZero()
    {
        Assert.Throws<ProfileValidationException>(() => ElevationTextParser.Parse(null));
        Assert.Throws<ProfileValidationException>(() => ElevationTextParser.Parse(new string('x', 4097) + "160.2"));
    }
}
