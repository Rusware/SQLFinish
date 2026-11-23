using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SQLFinish.Engine
{
    /// <summary>A result column: its heading, SQL Server type name ("decimal", "datetime2", ...) and numeric or time scale.</summary>
    public sealed class XlsxColumn
    {
        public string Name, SqlType;
        public int Scale;
    }

    /// <summary>
    /// Writes a result set as an Excel workbook: one sheet, a coloured header row that stays in view and has
    /// the filter switched on, and every column formatted for its SQL type. The file format is a zip of a few
    /// small XML files, written here directly so the extension needs no spreadsheet library.
    /// </summary>
    public static class Xlsx
    {
        /// <summary>Excel's limits: 1,048,576 rows including the header, and 32,767 characters in a cell.</summary>
        public const int MaxRows = 1048575, MaxCellChars = 32767;

        const string DateTimeFormat = "yyyy-mm-dd hh:mm:ss.000", TimeFormat = "hh:mm:ss.000";
        const string HeaderFill = "FF1F4E78", HeaderText = "FFFFFFFF"; // dark blue under white bold text
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
        const long Digits15 = 1000000000000000; // Excel keeps 15 significant digits
        static readonly long Epoch = new DateTime(1899, 12, 30).Ticks;
        static readonly DateTime FirstDate = new DateTime(1900, 3, 1); // earlier days are off by one in Excel (it believes 1900 was a leap year), or do not exist
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>
        /// A new name in <paramref name="folder"/> for a workbook that is only there to be opened in Excel.
        /// Earlier ones are cleared out once they are a week old (those still open in Excel stay).
        /// </summary>
        public static string ScratchFile(string folder, string name, DateTime now)
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "*.xlsx"))
            {
                try
                {
                    if (File.GetLastWriteTime(old) > now.AddDays(-7)) continue;
                    File.SetAttributes(old, FileAttributes.Normal); // they are written read-only
                    File.Delete(old);
                }
                catch (IOException) { }                 // open in Excel
                catch (UnauthorizedAccessException) { } // not ours to delete
            }
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(folder, name + " " + now.ToString("yyyy-MM-dd HHmmss", Invariant) + ".xlsx");
        }

        /// <summary>The Excel number format for a SQL Server type, or null where Excel's General format is right.</summary>
        public static string Format(string sqlType, int scale)
        {
            switch ((sqlType ?? "").ToLowerInvariant())
            {
                case "tinyint": case "smallint": case "int": case "bigint": return "0";
                case "decimal": case "numeric": return scale > 0 && scale <= 38 ? "0." + new string('0', Math.Min(scale, 30)) : "0";
                case "money": case "smallmoney": return "#,##0.00##";
                case "date": return "yyyy-mm-dd";
                case "smalldatetime": return "yyyy-mm-dd hh:mm";
                case "datetime": return DateTimeFormat;
                case "datetime2": return scale > 0 ? DateTimeFormat : "yyyy-mm-dd hh:mm:ss";
                case "time": return scale > 0 ? TimeFormat : "hh:mm:ss";
                default: return null; // text, float, bit, uniqueidentifier, binary, ...
            }
        }

        /// <summary>
        /// Writes the workbook to <paramref name="output"/> and returns the number of rows written, which is
        /// less than <paramref name="rowCount"/> only when Excel's row limit was reached.
        /// </summary>
        /// <param name="value">The stored value of a cell (row and column from 0): a SqlTypes or CLR value, or null.</param>
        /// <param name="text">The same cell as text, used where Excel cannot hold the value itself.</param>
        /// <param name="truncated">How many cells were cut off at Excel's character limit.</param>
        public static long Write(Stream output, IList<XlsxColumn> columns, long rowCount, Func<long, int, object> value, Func<long, int, string> text, out int truncated)
        {
            // Style 0 is plain, 1 the header, and then one per number format.
            var formats = new List<string>();
            Func<string, int> styleOf = format =>
            {
                if (format == null) return 0;
                int at = formats.IndexOf(format);
                if (at < 0) { formats.Add(format); at = formats.Count - 1; }
                return at + 2;
            };
            var style = new int[columns.Count];
            var isDate = new bool[columns.Count];
            var isTime = new bool[columns.Count];
            var letters = new string[columns.Count];
            var widths = new int[columns.Count];
            for (int c = 0; c < columns.Count; c++)
            {
                string format = Format(columns[c].SqlType, columns[c].Scale);
                style[c] = styleOf(format);
                isDate[c] = format != null && format.StartsWith("yyyy", StringComparison.Ordinal);
                isTime[c] = format != null && format.StartsWith("hh", StringComparison.Ordinal);
                letters[c] = Letters(c);
                widths[c] = (columns[c].Name ?? "").Length + 4; // room for the filter button
            }
            int dateTimeStyle = styleOf(DateTimeFormat), timeStyle = styleOf(TimeFormat); // for dates in columns of no fixed type (sql_variant)

            long rows = Math.Min(rowCount, MaxRows);
            int cut = 0; // a lambda cannot count into an out parameter
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                Part(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                    + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                    + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                    + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                    + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                    + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Part(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rId1\" Type=\"" + Relationships + "/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Part(zip, "xl/workbook.xml", "<workbook xmlns=\"" + Main + "\" xmlns:r=\"" + Relationships + "\"><bookViews><workbookView/></bookViews>"
                    + "<sheets><sheet name=\"Results\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Part(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rId1\" Type=\"" + Relationships + "/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                    + "<Relationship Id=\"rId2\" Type=\"" + Relationships + "/styles\" Target=\"styles.xml\"/></Relationships>");
                Part(zip, "xl/styles.xml", Styles(formats));

                using (var sheet = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open(), new UTF8Encoding(false), 1 << 16))
                {
                    var row = new StringBuilder();
                    Action<long> build = r =>
                    {
                        string number = (r + 2).ToString(Invariant); // the header is row 1
                        row.Append("<row r=\"").Append(number).Append("\">");
                        for (int c = 0; c < columns.Count; c++)
                        {
                            object v = Plain(value(r, c));
                            if (v == null) continue; // NULL: an empty cell
                            if (v is bool flag)
                            {
                                row.Append("<c r=\"").Append(letters[c]).Append(number).Append("\" t=\"b\"><v>").Append(flag ? '1' : '0').Append("</v></c>");
                                widths[c] = Math.Max(widths[c], 7);
                                continue;
                            }
                            int cellStyle = style[c], shown;
                            string n = Number(v);
                            if (v is DateTime && n != null)
                            {
                                if (!isDate[c]) cellStyle = dateTimeStyle;
                                shown = formats[cellStyle - 2].Length;
                            }
                            else if (v is TimeSpan && n != null)
                            {
                                if (!isTime[c]) cellStyle = timeStyle;
                                shown = formats[cellStyle - 2].Length;
                            }
                            else shown = n == null ? 0 : n.Length + 1;
                            if (n != null)
                            {
                                row.Append("<c r=\"").Append(letters[c]).Append(number);
                                if (cellStyle != 0) row.Append("\" s=\"").Append(cellStyle);
                                row.Append("\"><v>").Append(n).Append("</v></c>");
                            }
                            else
                            {
                                string s = text(r, c) ?? Convert.ToString(v, Invariant) ?? "";
                                if (s.Length > MaxCellChars)
                                {
                                    // ponytail: Excel cannot hold more in a cell; the caller reports how many were cut.
                                    s = s.Substring(0, MaxCellChars);
                                    cut++;
                                }
                                row.Append("<c r=\"").Append(letters[c]).Append(number).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                                AppendText(row, s);
                                row.Append("</t></is></c>");
                                shown = s.Length;
                            }
                            if (shown > widths[c]) widths[c] = shown;
                        }
                        row.Append("</row>\n");
                    };

                    // Column widths come before the rows in the file, so they are measured on the first rows only.
                    long measured = Math.Min(rows, 200);
                    for (long r = 0; r < measured; r++) build(r);

                    sheet.Write(Declaration + "<worksheet xmlns=\"" + Main + "\"><sheetViews><sheetView workbookViewId=\"0\">"
                        + "<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
                    for (int c = 0; c < columns.Count; c++)
                        sheet.Write("<col min=\"" + (c + 1) + "\" max=\"" + (c + 1) + "\" width=\"" + Math.Min(Math.Max(widths[c] + 2, 8), 60)
                            + "\" style=\"" + style[c] + "\" customWidth=\"1\"/>");
                    sheet.Write("</cols><sheetData>\n<row r=\"1\">");
                    var header = new StringBuilder();
                    for (int c = 0; c < columns.Count; c++)
                    {
                        header.Append("<c r=\"").Append(letters[c]).Append("1\" s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                        AppendText(header, columns[c].Name ?? "");
                        header.Append("</t></is></c>");
                    }
                    sheet.Write(header.Append("</row>\n").ToString());

                    for (long r = measured; ; r++)
                    {
                        sheet.Write(row.ToString()); // the measured rows first, then one row at a time
                        row.Clear();
                        if (r >= rows) break;
                        build(r);
                    }
                    sheet.Write("</sheetData><autoFilter ref=\"A1:" + letters[columns.Count - 1] + (rows + 1).ToString(Invariant) + "\"/></worksheet>");
                }
            }
            truncated = cut;
            return rows;
        }

        static void Part(ZipArchive zip, string name, string xml)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(Declaration + xml);
        }

        static string Styles(List<string> formats)
        {
            var xml = new StringBuilder("<styleSheet xmlns=\"" + Main + "\">");
            if (formats.Count > 0)
            {
                xml.Append("<numFmts count=\"").Append(formats.Count).Append("\">");
                for (int i = 0; i < formats.Count; i++) xml.Append("<numFmt numFmtId=\"").Append(164 + i).Append("\" formatCode=\"").Append(formats[i]).Append("\"/>");
                xml.Append("</numFmts>");
            }
            xml.Append("<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                + "<font><b/><sz val=\"11\"/><color rgb=\"" + HeaderText + "\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" // Excel reserves the first two
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + HeaderFill + "\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>"
                + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"").Append(formats.Count + 2).Append("\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>");
            for (int i = 0; i < formats.Count; i++)
                xml.Append("<xf numFmtId=\"").Append(164 + i).Append("\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>");
            return xml.Append("</cellXfs></styleSheet>").ToString();
        }

        /// <summary>A, B, ... Z, AA, AB, ... for a column index from 0.</summary>
        static string Letters(int index)
        {
            string name = "";
            for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
            return name;
        }

        /// <summary>The value out of its SqlTypes wrapper; null for a database NULL.</summary>
        static object Plain(object v)
        {
            if (v == null || v is DBNull || (v is INullable nullable && nullable.IsNull)) return null;
            switch (v)
            {
                case SqlBoolean x: return x.Value;
                case SqlByte x: return (long)x.Value;
                case SqlInt16 x: return (long)x.Value;
                case SqlInt32 x: return (long)x.Value;
                case SqlInt64 x: return x.Value;
                case SqlMoney x: return x.Value;
                case SqlDouble x: return x.Value;
                case SqlSingle x: return x.Value;
                case SqlDateTime x: return x.Value;
                case SqlDecimal x:
                    try { return x.Value; }
                    catch (OverflowException) { return x.ToString(); } // more digits than a CLR decimal holds: kept as text
                case byte x: return (long)x;
                case short x: return (long)x;
                case int x: return (long)x;
                default: return v;
            }
        }

        /// <summary>
        /// The value as Excel stores a number (dates and times are numbers too), or null when it must be
        /// written as text instead: not a number at all, or one Excel would silently change.
        /// </summary>
        static string Number(object v)
        {
            switch (v)
            {
                case long x:
                    return x > -Digits15 && x < Digits15 ? x.ToString(Invariant) : null; // a longer bigint would lose its last digits
                case decimal x:
                    double d = (double)x;
                    return Math.Abs(d) < 1e28 && (decimal)d == x ? x.ToString(Invariant) : null; // survives Excel's 15 digits unchanged
                case double x:
                    return double.IsNaN(x) || double.IsInfinity(x) ? null : x.ToString("R", Invariant);
                case float x:
                    return float.IsNaN(x) || float.IsInfinity(x) ? null : x.ToString("R", Invariant);
                // ponytail: dates and times are cut to the millisecond, the finest Excel can show. Left whole, Excel
                // rounds what it shows, and 23:59:59.9999999 would read as midnight.
                case DateTime x:
                    return x >= FirstDate ? (Milliseconds(x.Ticks - Epoch) / (double)TimeSpan.TicksPerDay).ToString("R", Invariant) : null;
                case TimeSpan x:
                    return x >= TimeSpan.Zero && x < TimeSpan.FromDays(1) ? (Milliseconds(x.Ticks) / (double)TimeSpan.TicksPerDay).ToString("R", Invariant) : null;
                default:
                    return null;
            }
        }

        static long Milliseconds(long ticks) => ticks - ticks % TimeSpan.TicksPerMillisecond;

        /// <summary>Appends cell text as XML. Characters XML cannot carry are written the way Excel does: _x0001_.</summary>
        static void AppendText(StringBuilder xml, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '&': xml.Append("&amp;"); break;
                    case '<': xml.Append("&lt;"); break;
                    case '>': xml.Append("&gt;"); break;
                    case '\t': case '\n': xml.Append(ch); break;
                    case '\r': // Excel breaks lines on \n alone
                        if (i + 1 >= s.Length || s[i + 1] != '\n') xml.Append('\n');
                        break;
                    case '_': // text that happens to look like an escape must not be read back as one
                        bool escape = i + 6 < s.Length && s[i + 1] == 'x' && s[i + 6] == '_'
                            && Uri.IsHexDigit(s[i + 2]) && Uri.IsHexDigit(s[i + 3]) && Uri.IsHexDigit(s[i + 4]) && Uri.IsHexDigit(s[i + 5]);
                        xml.Append(escape ? "_x005F_" : "_");
                        break;
                    default:
                        if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) xml.Append(ch).Append(s[++i]);
                        else if (char.IsSurrogate(ch) || ch == '\uFFFE' || ch == '\uFFFF') xml.Append('\uFFFD');
                        else if (ch < ' ') xml.Append("_x").Append(((int)ch).ToString("X4", Invariant)).Append('_');
                        else xml.Append(ch);
                        break;
                }
            }
        }
    }
}
