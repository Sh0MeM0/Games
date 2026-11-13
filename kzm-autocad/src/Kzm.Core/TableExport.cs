using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Kzm.Core
{
    /// <summary>One sheet of an exported table: text and numbers; the first rows can be bold headers.</summary>
    public sealed class ExportSheet
    {
        public ExportSheet(string name) { Name = name; }

        public string Name { get; }
        public List<object[]> Rows { get; } = new List<object[]>();
        /// <summary>Rows (by index) shown in bold: headings of the tables on the sheet.</summary>
        public HashSet<int> BoldRows { get; } = new HashSet<int>();
        /// <summary>Decimals of number cells by column (absent — as is).</summary>
        public Dictionary<int, int> Decimals { get; } = new Dictionary<int, int>();
        /// <summary>Column widths in characters.</summary>
        public Dictionary<int, double> Widths { get; } = new Dictionary<int, double>();
        /// <summary>Freeze the rows above this one (0 — none).</summary>
        public int FrozenRows { get; set; }

        public ExportSheet Header(params object[] cells)
        {
            BoldRows.Add(Rows.Count);
            Rows.Add(cells);
            return this;
        }

        public ExportSheet Row(params object[] cells)
        {
            Rows.Add(cells);
            return this;
        }
    }

    /// <summary>
    /// Минимальный файл Excel (.xlsx, Office Open XML, ISO/IEC 29500) без сторонних библиотек:
    /// листы с текстом (inline strings) и числами, жирные заголовки, ширина столбцов.
    /// </summary>
    public static class XlsxWriter
    {
        private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static void Write(Stream stream, IReadOnlyList<ExportSheet> sheets)
        {
            if (sheets == null || sheets.Count == 0) throw new ArgumentException("Нет листов для записи.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in sheets)
            {
                if (string.IsNullOrWhiteSpace(sheet.Name) || sheet.Name.Length > 31 || sheet.Name.IndexOfAny(new[] { '[', ']', ':', '*', '?', '/', '\\' }) >= 0)
                    throw new ArgumentException("Недопустимое имя листа: «" + sheet.Name + "».");
                if (!names.Add(sheet.Name)) throw new ArgumentException("Повторяется имя листа: «" + sheet.Name + "».");
            }
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                Part(zip, "[Content_Types].xml", w =>
                {
                    w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                    Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                    Default(w, "xml", "application/xml");
                    Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                    Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                    for (int i = 0; i < sheets.Count; i++)
                        Override(w, "/xl/worksheets/sheet" + (i + 1) + ".xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                    w.WriteEndElement();
                });
                Part(zip, "_rels/.rels", w =>
                {
                    w.WriteStartElement("Relationships", PackageRelationships);
                    Relationship(w, "rId1", Relationships + "/officeDocument", "xl/workbook.xml");
                    w.WriteEndElement();
                });
                Part(zip, "xl/workbook.xml", w =>
                {
                    w.WriteStartElement("workbook", Main);
                    w.WriteAttributeString("xmlns", "r", null, Relationships);
                    w.WriteStartElement("sheets", Main);
                    for (int i = 0; i < sheets.Count; i++)
                    {
                        w.WriteStartElement("sheet", Main);
                        w.WriteAttributeString("name", sheets[i].Name);
                        w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("id", Relationships, "rId" + (i + 1));
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteEndElement();
                });
                Part(zip, "xl/_rels/workbook.xml.rels", w =>
                {
                    w.WriteStartElement("Relationships", PackageRelationships);
                    for (int i = 0; i < sheets.Count; i++)
                        Relationship(w, "rId" + (i + 1), Relationships + "/worksheet", "worksheets/sheet" + (i + 1) + ".xml");
                    Relationship(w, "rId" + (sheets.Count + 1), Relationships + "/styles", "styles.xml");
                    w.WriteEndElement();
                });
                Part(zip, "xl/styles.xml", Styles);
                for (int i = 0; i < sheets.Count; i++)
                {
                    var sheet = sheets[i];
                    Part(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", w => Sheet(w, sheet));
                }
            }
        }

        private static void Part(ZipArchive zip, string name, Action<XmlWriter> body)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            {
                writer.WriteStartDocument(true);
                body(writer);
                writer.WriteEndDocument();
            }
        }

        private static void Default(XmlWriter w, string extension, string type)
        {
            w.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types");
            w.WriteAttributeString("Extension", extension);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        private static void Override(XmlWriter w, string part, string type)
        {
            w.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types");
            w.WriteAttributeString("PartName", part);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        private static void Relationship(XmlWriter w, string id, string type, string target)
        {
            w.WriteStartElement("Relationship", PackageRelationships);
            w.WriteAttributeString("Id", id);
            w.WriteAttributeString("Type", type);
            w.WriteAttributeString("Target", target);
            w.WriteEndElement();
        }

        // Cell formats: 0 — general, 1 — bold, 2..5 — numbers with 0..3 decimals, 6..9 — the same, bold.
        private static readonly string[] NumberFormats = { "0", "0.0", "0.00", "0.000" };

        private static int Style(bool bold, int? decimals)
        {
            if (!decimals.HasValue) return bold ? 1 : 0;
            int d = Math.Max(0, Math.Min(3, decimals.Value));
            return (bold ? 6 : 2) + d;
        }

        private static void Styles(XmlWriter w)
        {
            w.WriteStartElement("styleSheet", Main);
            w.WriteStartElement("numFmts", Main);
            w.WriteAttributeString("count", NumberFormats.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < NumberFormats.Length; i++)
            {
                w.WriteStartElement("numFmt", Main);
                w.WriteAttributeString("numFmtId", (164 + i).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("formatCode", NumberFormats[i]);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("fonts", Main);
            w.WriteAttributeString("count", "2");
            foreach (bool bold in new[] { false, true })
            {
                w.WriteStartElement("font", Main);
                if (bold) { w.WriteStartElement("b", Main); w.WriteEndElement(); }
                w.WriteStartElement("sz", Main); w.WriteAttributeString("val", "11"); w.WriteEndElement();
                w.WriteStartElement("name", Main); w.WriteAttributeString("val", "Calibri"); w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("fills", Main);
            w.WriteAttributeString("count", "2");
            foreach (string pattern in new[] { "none", "gray125" })
            {
                w.WriteStartElement("fill", Main);
                w.WriteStartElement("patternFill", Main);
                w.WriteAttributeString("patternType", pattern);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("borders", Main);
            w.WriteAttributeString("count", "1");
            w.WriteStartElement("border", Main);
            foreach (string side in new[] { "left", "right", "top", "bottom", "diagonal" }) { w.WriteStartElement(side, Main); w.WriteEndElement(); }
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("cellStyleXfs", Main);
            w.WriteAttributeString("count", "1");
            Xf(w, 0, 0, false);
            w.WriteEndElement();
            w.WriteStartElement("cellXfs", Main);
            w.WriteAttributeString("count", "10");
            Xf(w, 0, 0, true);
            Xf(w, 0, 1, true);
            for (int i = 0; i < NumberFormats.Length; i++) Xf(w, 164 + i, 0, true);
            for (int i = 0; i < NumberFormats.Length; i++) Xf(w, 164 + i, 1, true);
            w.WriteEndElement();
            w.WriteStartElement("cellStyles", Main);
            w.WriteAttributeString("count", "1");
            w.WriteStartElement("cellStyle", Main);
            w.WriteAttributeString("name", "Normal");
            w.WriteAttributeString("xfId", "0");
            w.WriteAttributeString("builtinId", "0");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void Xf(XmlWriter w, int format, int font, bool inCellXfs)
        {
            w.WriteStartElement("xf", Main);
            w.WriteAttributeString("numFmtId", format.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fillId", "0");
            w.WriteAttributeString("borderId", "0");
            if (inCellXfs)
            {
                w.WriteAttributeString("xfId", "0");
                if (format != 0) w.WriteAttributeString("applyNumberFormat", "1");
                if (font != 0) w.WriteAttributeString("applyFont", "1");
            }
            w.WriteEndElement();
        }

        internal static string Column(int index)
        {
            string name = string.Empty;
            for (int n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
            return name;
        }

        private static void Sheet(XmlWriter w, ExportSheet sheet)
        {
            w.WriteStartElement("worksheet", Main);
            if (sheet.FrozenRows > 0)
            {
                w.WriteStartElement("sheetViews", Main);
                w.WriteStartElement("sheetView", Main);
                w.WriteAttributeString("workbookViewId", "0");
                w.WriteStartElement("pane", Main);
                w.WriteAttributeString("ySplit", sheet.FrozenRows.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("topLeftCell", "A" + (sheet.FrozenRows + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("activePane", "bottomLeft");
                w.WriteAttributeString("state", "frozen");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            }
            if (sheet.Widths.Count > 0)
            {
                w.WriteStartElement("cols", Main);
                foreach (var pair in sheet.Widths.OrderBy(p => p.Key))
                {
                    w.WriteStartElement("col", Main);
                    string index = (pair.Key + 1).ToString(CultureInfo.InvariantCulture);
                    w.WriteAttributeString("min", index);
                    w.WriteAttributeString("max", index);
                    w.WriteAttributeString("width", pair.Value.ToString("0.##", CultureInfo.InvariantCulture));
                    w.WriteAttributeString("customWidth", "1");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteStartElement("sheetData", Main);
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                var cells = sheet.Rows[r] ?? new object[0];
                string row = (r + 1).ToString(CultureInfo.InvariantCulture);
                w.WriteStartElement("row", Main);
                w.WriteAttributeString("r", row);
                bool bold = sheet.BoldRows.Contains(r);
                for (int c = 0; c < cells.Length; c++)
                {
                    object value = cells[c];
                    if (value == null) continue;
                    w.WriteStartElement("c", Main);
                    w.WriteAttributeString("r", Column(c) + row);
                    if (TryNumber(value, out double number))
                    {
                        int style = Style(bold, sheet.Decimals.TryGetValue(c, out int d) ? d : (int?)null);
                        if (style != 0) w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
                        w.WriteElementString("v", Main, number.ToString("R", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                        w.WriteAttributeString("t", "inlineStr");
                        if (bold) w.WriteAttributeString("s", "1");
                        w.WriteStartElement("is", Main);
                        w.WriteStartElement("t", Main);
                        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1])))
                            w.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                        w.WriteString(Clean(text));
                        w.WriteEndElement();
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }

        internal static bool TryNumber(object value, out double number)
        {
            switch (value)
            {
                case double d when Guard.Finite(d): number = d; return true;
                case float f when Guard.Finite(f): number = f; return true;
                case int i: number = i; return true;
                case long l: number = l; return true;
                case decimal m: number = (double)m; return true;
                default: number = 0; return false;
            }
        }

        /// <summary>Characters not allowed in XML 1.0 are dropped.</summary>
        private static string Clean(string text)
        {
            var output = new StringBuilder(text.Length);
            foreach (char c in text)
                if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF)) output.Append(c);
            return output.ToString();
        }
    }

    /// <summary>CSV для Excel с русскими настройками: разделитель «;», десятичная запятая, UTF-8 с BOM.</summary>
    public static class CsvWriter
    {
        public static void Write(Stream stream, IReadOnlyList<ExportSheet> sheets)
        {
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 4096, true))
            {
                bool first = true;
                foreach (var sheet in sheets)
                {
                    if (!first) writer.WriteLine();
                    first = false;
                    writer.WriteLine(Field(sheet.Name));
                    foreach (var row in sheet.Rows)
                    {
                        var cells = row ?? new object[0];
                        var line = new string[cells.Length];
                        for (int c = 0; c < cells.Length; c++)
                        {
                            object value = cells[c];
                            if (value == null) { line[c] = string.Empty; continue; }
                            if (XlsxWriter.TryNumber(value, out double number))
                            {
                                line[c] = sheet.Decimals.TryGetValue(c, out int d)
                                    ? ElevationFormat.Format(number, Math.Max(0, Math.Min(6, d)))
                                    : number.ToString("R", CultureInfo.InvariantCulture).Replace('.', ',');
                            }
                            else line[c] = Field(Convert.ToString(value, CultureInfo.InvariantCulture));
                        }
                        writer.WriteLine(string.Join(";", line));
                    }
                }
            }
        }

        private static string Field(string text)
        {
            text = text ?? string.Empty;
            if (text.IndexOfAny(new[] { ';', '"', '\n', '\r' }) < 0) return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
