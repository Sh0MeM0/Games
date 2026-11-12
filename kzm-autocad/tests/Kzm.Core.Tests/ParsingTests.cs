using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class ParsingTests
    {
        // Т5: «145,32» и «145.32» читаются одинаково.
        [Fact]
        public void T5_CommaAndDotGiveTheSameValue()
        {
            var comma = ElevationExtractor.Extract(TextCleaner.CleanText("145,32"));
            var dot = ElevationExtractor.Extract(TextCleaner.CleanText("145.32"));
            Assert.Equal(ImportStatus.Accepted, comma.Status);
            Assert.Equal(ImportStatus.Accepted, dot.Status);
            Assert.Equal(145.32, comma.Value.Value, 10);
            Assert.Equal(comma.Value, dot.Value);
        }

        // Т5: MTEXT с форматированием читается.
        [Theory]
        [InlineData(@"{\fArial|b0|i0|c204|p34;\H2.5;145,32}")]
        [InlineData(@"\A1;{\C1;145}{\H0.7x;,32}")]
        [InlineData(@"{\LОтм.\l} {\W0.8;\Q15;145.32}")]
        [InlineData(@"\pxi-3,l3,t3;\T1.1;145,32\P")]
        [InlineData(@"\U+0031\U+0034\U+0035,32")]
        [InlineData(@"{\c16711680;\K145\k,32}")]
        public void T5_FormattedMTextIsRead(string contents)
        {
            var result = ElevationExtractor.Extract(TextCleaner.CleanMText(contents));
            Assert.Equal(ImportStatus.Accepted, result.Status);
            Assert.Equal(145.32, result.Value.Value, 10);
        }

        // Т5 (по решению без диапазона): «12» — целое число, отбрасывается с причиной.
        [Fact]
        public void T5_PointNumberIsRejectedWithReason()
        {
            var result = ElevationExtractor.Extract("12");
            Assert.Equal(ImportStatus.Rejected, result.Status);
            Assert.Equal(ImportReason.IntegerOnly, result.Reason);
            Assert.Contains("не отметка", result.Message);
            Assert.Contains("12", result.Message);
            Assert.Null(result.Value);
        }

        [Theory]
        [InlineData("т.15 145,32", 145.32)]
        [InlineData("15  145.32", 145.32)]
        [InlineData("145,3", 145.3)]
        [InlineData("−0,50", -0.5)]
        [InlineData("+2,40", 2.4)]
        public void OneDecimalNumberIsTheElevation(string text, double expected)
        {
            var result = ElevationExtractor.Extract(text);
            Assert.Equal(ImportStatus.Accepted, result.Status);
            Assert.Equal(expected, result.Value.Value, 10);
        }

        [Fact]
        public void TwoDecimalNumbersAreDisputed()
        {
            var result = ElevationExtractor.Extract(TextCleaner.CleanMText(@"145,32\P146,10"));
            Assert.Equal(ImportStatus.Disputed, result.Status);
            Assert.Equal(ImportReason.MultipleNumbers, result.Reason);
            Assert.Equal(new[] { 145.32, 146.10 }, result.Decimals.Select(v => System.Math.Round(v, 2)));
        }

        [Fact]
        public void StackedFractionKeepsBothNumbers()
        {
            Assert.Equal(ImportStatus.Disputed, ElevationExtractor.Extract(TextCleaner.CleanMText(@"\S145,32^146,10;")).Status);
        }

        [Fact]
        public void TextControlCodesAreRemoved()
        {
            Assert.Equal("145,32", TextCleaner.CleanText("%%u145,32%%u"));
            Assert.Equal("±0,15", TextCleaner.CleanText("%%p0,15"));
            Assert.Equal(@"a\Pb", TextCleaner.CleanText(@"a\Pb"));
            Assert.Equal("100%", TextCleaner.CleanText("100%%%"));
            Assert.Equal("A", TextCleaner.CleanText("%%065"));
        }

        [Fact]
        public void MTextEscapesAreLiteral()
        {
            Assert.Equal(@"a\b{c}", TextCleaner.CleanMText(@"a\\b\{c\}"));
            Assert.Equal("a b", TextCleaner.CleanMText(@"a\~b"));
            Assert.Equal("1\n2", TextCleaner.CleanMText(@"1\P2"));
        }

        [Fact]
        public void EmptyAndNonNumericTextsAreRejected()
        {
            Assert.Equal(ImportReason.EmptyText, ElevationExtractor.Extract("  ").Reason);
            var none = ElevationExtractor.Extract("Скв. А");
            Assert.Equal(ImportStatus.Rejected, none.Status);
            Assert.Equal(ImportReason.NoNumber, none.Reason);
        }

        [Fact]
        public void SeparatorWithoutDigitsIsNotAFraction()
        {
            // «145.» is a whole number followed by a dot, not 145,0.
            Assert.Equal(ImportReason.IntegerOnly, ElevationExtractor.Extract("145.").Reason);
        }

        [Theory]
        [InlineData(145.32, 2, "145,32")]
        [InlineData(145.325, 2, "145,33")]
        [InlineData(145.3249, 2, "145,32")]
        [InlineData(-0.004, 2, "0,00")]
        [InlineData(100, 2, "100,00")]
        [InlineData(-0.255, 2, "-0,26")]
        public void ElevationsUseCommaAndTwoDecimals(double value, int decimals, string expected)
        {
            Assert.Equal(expected, ElevationFormat.Format(value, decimals));
        }

        [Fact]
        public void SignedFormatShowsPlus()
        {
            Assert.Equal("+0,50", ElevationFormat.FormatSigned(0.5, 2));
            Assert.Equal("-0,98", ElevationFormat.FormatSigned(-0.98, 2));
            Assert.Equal("0,00", ElevationFormat.FormatSigned(0, 2));
        }

        [Theory]
        [InlineData("145,32", true, 145.32)]
        [InlineData(" 145.32 ", true, 145.32)]
        [InlineData("145,", true, 145.0)]
        [InlineData("145,32 м", false, 0)]
        [InlineData("1 145,32", false, 0)]
        [InlineData("", false, 0)]
        public void UserInputIsOneNumber(string text, bool ok, double expected)
        {
            Assert.Equal(ok, ElevationFormat.TryParse(text, out double value));
            if (ok) Assert.Equal(expected, value, 10);
        }
    }
}
