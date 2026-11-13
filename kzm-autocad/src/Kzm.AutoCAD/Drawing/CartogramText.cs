using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using Kzm.Core;

namespace Kzm.AutoCAD.Drawing
{
    /// <summary>
    /// Текстовый стиль «КЗМ» для всех текстов картограммы — отметки, объёмы, таблица под сеткой, площади, ведомость
    /// (замечание 27). Шрифт выбирается в «Настройках», по умолчанию GOST Common. Если такого шрифта в Windows нет,
    /// тексты пишутся текущим стилем чертежа (как до версии 0.9), а в замечаниях об этом сказано: иначе AutoCAD
    /// подставил бы шрифт, в котором может не оказаться русских букв.
    /// </summary>
    internal static class CartogramText
    {
        internal const string StyleName = "КЗМ";
        /// <summary>DEFAULT_CHARSET: the character set of the Windows language (Cyrillic on a Russian or Belarusian Windows).</summary>
        private const int DefaultCharset = 1;
        private static List<string> installed;

        /// <summary>TrueType font families installed in Windows (read once per AutoCAD session).</summary>
        internal static IReadOnlyList<string> InstalledFonts()
        {
            if (installed != null) return installed;
            try
            {
                using (var fonts = new InstalledFontCollection())
                    installed = fonts.Families.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception)
            {
                installed = new List<string>();
            }
            return installed;
        }

        internal static bool IsInstalled(string font) => InstalledFonts().Contains(font, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The text style for the cartogram: «КЗМ» with the font of the settings (created or brought up to date), or the
        /// current style of the drawing when no font is chosen or it is not installed.
        /// </summary>
        internal static ObjectId Ensure(Database database, Transaction transaction, KzmSettings settings, ICollection<string> problems)
        {
            string font = settings?.Font?.Trim();
            if (string.IsNullOrEmpty(font)) return database.Textstyle;
            if (!IsInstalled(font))
            {
                problems?.Add("шрифт «" + font + "» не установлен в Windows — тексты картограммы написаны текущим текстовым стилем чертежа " +
                              "(шрифт — в «Настройках»)");
                return database.Textstyle;
            }
            var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            TextStyleTableRecord style;
            if (table.Has(StyleName)) style = (TextStyleTableRecord)transaction.GetObject(table[StyleName], OpenMode.ForRead);
            else
            {
                table.UpgradeOpen();
                style = new TextStyleTableRecord { Name = StyleName, TextSize = 0, XScale = 1 };
                table.Add(style);
                transaction.AddNewlyCreatedDBObject(style, true);
            }
            Apply(style, font, settings.FontItalic);
            return style.ObjectId;
        }

        /// <summary>After «Настройки → Сохранить»: an existing style «КЗМ» gets the new font at once (texts follow their style).</summary>
        internal static void Update(Database database, Transaction transaction, KzmSettings settings)
        {
            string font = settings?.Font?.Trim();
            if (string.IsNullOrEmpty(font) || !IsInstalled(font)) return;
            var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            if (!table.Has(StyleName)) return;
            Apply((TextStyleTableRecord)transaction.GetObject(table[StyleName], OpenMode.ForRead), font, settings.FontItalic);
        }

        private static void Apply(TextStyleTableRecord style, string font, bool italic)
        {
            var current = style.Font;
            if (string.Equals(current.TypeFace, font, StringComparison.OrdinalIgnoreCase) && current.Italic == italic && !current.Bold) return;
            style.UpgradeOpen();
            // A TrueType typeface set here takes precedence over the font file of the style.
            style.Font = new FontDescriptor(font, false, italic, DefaultCharset, 0);
        }
    }
}
