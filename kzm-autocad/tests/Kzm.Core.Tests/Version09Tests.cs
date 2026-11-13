using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Замечания 27 и 29: шрифт текстов картограммы и высота текста по умолчанию.</summary>
    public class Version09Tests
    {
        [Fact]
        public void FontIsStoredWithTheSettings()
        {
            var settings = new KzmSettings { Font = "ISOCPEUR", FontItalic = true };
            var back = SettingsCodec.Decode(SettingsCodec.Encode(settings));
            Assert.Equal("ISOCPEUR", back.Font);
            Assert.True(back.FontItalic);
        }

        [Fact]
        public void OldDrawingsGetGostCommon()
        {
            var old = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("version", "1"), new KeyValuePair<string, string>("decimals", "2") };
            var settings = SettingsCodec.Decode(old);
            Assert.Equal("GOST Common", settings.Font);
            Assert.False(settings.FontItalic);
        }

        [Fact]
        public void TemplateKeepsTheFont()
        {
            var text = DefaultsCodec.Encode(new KzmSettings { Font = "GOST type A", FontItalic = true }, new CartogramInfo());
            var (settings, _) = DefaultsCodec.Decode(text);
            Assert.Equal("GOST type A", settings.Font);
            Assert.True(settings.FontItalic);
        }

        // 0,9 в чертеже при любом масштабе: на листе 0,9 мм при 1:1000 и вдвое больше при 1:500.
        [Theory]
        [InlineData(1000, 0.9)]
        [InlineData(500, 1.8)]
        [InlineData(2000, 0.45)]
        public void DefaultTextHeight(int scale, double millimetres)
        {
            Assert.Equal(millimetres, CartogramInfo.DefaultTextMm(scale), 9);
            Assert.Equal(0.9, millimetres * scale / 1000.0, 9);
        }

        [Fact]
        public void NewCartogramDefaultsAreForOneToFiveHundred()
        {
            var c = new CartogramInfo();
            Assert.Equal(500, c.Scale);
            Assert.Equal(new[] { 1.8, 1.8, 1.8 }, new[] { c.NodeTextMm, c.VolumeTextMm, c.TotalsTextMm });
        }
    }
}
