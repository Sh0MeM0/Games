using System;
using System.Globalization;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class NumberParserTests
{
    [Theory]
    [InlineData("12.75", 12.75)]
    [InlineData(" 12,75 ", 12.75)]
    [InlineData("-0,5", -0.5)]
    [InlineData("0", 0)]
    [InlineData("1+25.50", 125.5)]
    [InlineData("1+25,50", 125.5)]
    [InlineData("ПК 12+00", 1200)]
    public void RequiredNumbersUseMetresAndUnambiguousPicketNotation(string input, double expected)
    {
        Assert.Equal(expected, NumberParser.ParseRequired(input, "Пикет"), 8);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOptionalElevationIsMissingRatherThanZero(string input)
    {
        Assert.Null(NumberParser.ParseOptional(input, "Отметка воды"));
    }

    [Fact]
    public void ExplicitZeroRemainsARealOptionalValue()
    {
        Assert.Equal(0, NumberParser.ParseOptional("0", "Отметка воды"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e309")]
    [InlineData("1,234.56")]
    [InlineData("1.234,56")]
    [InlineData("1 234")]
    [InlineData("1,2,3")]
    [InlineData("1+100")]
    [InlineData("1+-25")]
    public void InvalidOrAmbiguousNumbersReportTheField(string input)
    {
        var error = Assert.Throws<ProfileValidationException>(
            () => NumberParser.ParseRequired(input, "Контрольное поле"));
        Assert.Contains("Контрольное поле", error.Message);
    }

    [Fact]
    public void InterpretationDoesNotDependOnCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "ru-RU", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(12.75, NumberParser.ParseRequired("12,75", "Отметка"));
                Assert.Equal(12.75, NumberParser.ParseRequired("12.75", "Отметка"));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
