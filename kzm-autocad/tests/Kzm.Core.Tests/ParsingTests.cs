using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class ParsingTests
    {
        private static ElevationRules Rules(double min = 140, double max = 160) =>
            new ElevationRules(KzmSettings.DefaultPattern, min, max, 2);

        // Т5: «145,32» и «145.32» читаются одинаково.
        [Fact]
        public void T5_CommaAndDotGiveTheSameValue()
        {
            var comma = ElevationExtractor.Extract(TextCleaner.CleanText("145,32"), Rules());
            var dot = ElevationExtractor.Extract(TextCleaner.CleanText("145.32"), Rules());
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
            var result = ElevationExtractor.Extract(TextCleaner.CleanMText(contents), Rules());
            Assert.Equal(ImportStatus.Accepted, result.Status);
            Assert.Equal(145.32, result.Value.Value, 10);
        }

        // Т5: текст «12» при диапазоне 140…160 отбрасывается с причиной.
        [Fact]
        public void T5_PointNumberOutsideRangeIsRejectedWithReason()
        {
            var result = ElevationExtractor.Extract("12", Rules());
            Assert.Equal(ImportStatus.Rejected, result.Status);
            Assert.Equal(ImportReason.OutOfRange, result.Reason);
            Assert.Contains("вне диапазона 140,00…160,00", result.Message);
            Assert.Contains("12", result.Message);
            Assert.Null(result.Value);
        }

        [Fact]
        public void PointNumberAndElevationInOneTextGiveTheElevation()
        {
            var result = ElevationExtractor.Extract("т.15 145,32", Rules());
            Assert.Equal(ImportStatus.Accepted, result.Status);
            Assert.Equal(145.32, result.Value.Value, 10);
        }

        [Fact]
        public void TwoElevationsInOneTextAreDisputed()
        {
            var result = ElevationExtractor.Extract(TextCleaner.CleanMText(@"145,32\P146,10"), Rules());
            Assert.Equal(ImportStatus.Disputed, result.Status);
            Assert.Equal(ImportReason.MultipleInRange, result.Reason);
            Assert.Equal(new[] { 145.32, 146.10 }, result.InRange.Select(v => System.Math.Round(v, 2)));
        }

        [Fact]
        public void StackedFractionKeepsBothNumbers()
        {
            var plain = TextCleaner.CleanMText(@"\S145,32^146,10;");
            var result = ElevationExtractor.Extract(plain, Rules());
            Assert.Equal(ImportStatus.Disputed, result.Status);
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
            Assert.Equal(ImportReason.EmptyText, ElevationExtractor.Extract("  ", Rules()).Reason);
            var none = ElevationExtractor.Extract("Скв. А", Rules());
            Assert.Equal(ImportStatus.Rejected, none.Status);
            Assert.Equal(ImportReason.NoNumber, none.Reason);
        }

        [Fact]
        public void UnicodeMinusIsAMinus()
        {
            var result = ElevationExtractor.Extract("−0,50", new ElevationRules(KzmSettings.DefaultPattern, -1, 1, 2));
            Assert.Equal(-0.5, result.Value.Value, 10);
        }

        [Fact]
        public void RangeBoundsAreInclusive()
        {
            Assert.Equal(ImportStatus.Accepted, ElevationExtractor.Extract("140", Rules()).Status);
            Assert.Equal(ImportStatus.Accepted, ElevationExtractor.Extract("160,00", Rules()).Status);
            Assert.Equal(ImportStatus.Rejected, ElevationExtractor.Extract("160,01", Rules()).Status);
        }

        [Fact]
        public void CustomPatternMayNameTheNumberGroup()
        {
            var rules = new ElevationRules(@"Н=(?<num>[0-9]+[.,][0-9]+)", 140, 160, 2);
            var result = ElevationExtractor.Extract("т.7 Н=145,32 h=0,15", rules);
            Assert.Equal(ImportStatus.Accepted, result.Status);
            Assert.Equal(145.32, result.Value.Value, 10);
        }

        [Fact]
        public void BrokenPatternIsReportedNotThrownLater()
        {
            var exception = Assert.Throws<KzmException>(() => new ElevationRules("[0-9", 140, 160, 2));
            Assert.Contains("Шаблон числа содержит ошибку", exception.Message);
            var settings = new KzmSettings { NumberPattern = "[0-9", MinElevation = 1, MaxElevation = 2 };
            Assert.Contains(settings.ValidateForImport(), problem => problem.Contains("Шаблон числа"));
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
