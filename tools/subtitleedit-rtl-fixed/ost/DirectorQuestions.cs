using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal sealed record DirectorLine(Guid Id, int Number, string Text, double Start, double End);
internal sealed record DirectorQuestionRow(Guid Id, int Number, string Time, string Text, string Question, string Status, string Answer);

internal static class DirectorQuestions
{
    internal static readonly string[] Headers = { "כתובית", "זמן", "טקסט הכתובית", "השאלה לבמאי", "מצב", "תשובה" };
    internal static List<DirectorQuestionRow> Rows(ProjectWork work, IEnumerable<DirectorLine> lines, bool openOnly, double videoOffsetMs)
    {
        var result = new List<DirectorQuestionRow>();
        foreach (var row in lines)
        {
            var note = CustomProject.FindNote(work, row.Id, row.Text, row.Start, row.End);
            if (note?.DirectorQuestion != true || (openOnly && note.DirectorResolved)) continue;
            result.Add(new(row.Id, row.Number, Time(row.Start + videoOffsetMs) + " – " + Time(row.End + videoOffsetMs), CustomQc.Visible(row.Text), note.Note,
                note.DirectorResolved ? "טופל" : "ממתין לתשובה", note.DirectorAnswer));
        }
        return result;
    }
    private static string Time(double ms)
    {
        var span = TimeSpan.FromMilliseconds(Math.Abs(ms));
        return (ms < 0 ? "-" : "") + ((long)span.TotalHours).ToString("00", CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    }
    internal static void Update(WorkNote note, string question, string answer, bool resolved)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new InvalidDataException("כתוב שאלה לבמאי לפני השמירה.");
        note.Note = question; note.DirectorAnswer = answer; note.DirectorQuestion = true; note.DirectorResolved = resolved;
        // Resolving a question is deliberately independent of the row's general QC status.
    }
    internal static void RefreshNote(WorkNote note, string text, double start, double end)
    {
        if (note.Text != text && note.DirectorQuestion) note.DirectorResolved = false;
        note.Text = text; note.Start = start; note.End = end;
    }
    private static bool RtlText(string text) => text.Any(c => c is >= '\u0590' and <= '\u08ff' or '\u200f');
    private static string[] Cells(DirectorQuestionRow row) => new[] { row.Number.ToString(CultureInfo.InvariantCulture), row.Time, CustomQc.Visible(row.Text), row.Question, row.Status, row.Answer };
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static void Entry(ZipArchive zip, string name, XDocument xml)
    {
        using var stream = zip.CreateEntry(name).Open(); xml.Save(stream);
    }
    private static string Clean(string text) => string.Concat(text.EnumerateRunes().Where(r => r.Value is 9 or 10 or 13 || r.Value >= 32 && r.Value != 0xfffe && r.Value != 0xffff).Select(r => r.ToString()));
    private static XDocument Types(params (string Path, string Type)[] entries) => new(new XElement(C + "Types",
        new XElement(C + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
        new XElement(C + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
        entries.Select(e => new XElement(C + "Override", new XAttribute("PartName", e.Path), new XAttribute("ContentType", e.Type)))));
    private static XDocument Relations(params (string Id, string Type, string Target)[] entries) => new(new XElement(P + "Relationships", entries.Select(e => new XElement(P + "Relationship", new XAttribute("Id", e.Id), new XAttribute("Type", R.NamespaceName + "/" + e.Type), new XAttribute("Target", e.Target)))));
    private static void WriteAtomic(string path, Action<ZipArchive> build)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".director-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = File.Create(temporary)) using (var zip = new ZipArchive(stream, ZipArchiveMode.Create)) build(zip);
            using (var zip = ZipFile.OpenRead(temporary)) foreach (var item in zip.Entries) { using var input = item.Open(); _ = XDocument.Load(input); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void ExportWord(string path, string title, IReadOnlyList<DirectorQuestionRow> rows)
    {
        WriteAtomic(path, zip =>
        {
            Entry(zip, "[Content_Types].xml", Types(("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")));
            Entry(zip, "_rels/.rels", Relations(("rId1", "officeDocument", "word/document.xml")));
            XElement Paragraph(string text, bool rtl, int size, bool bold = false, bool white = false)
            {
                var run = new XElement(W + "rPr", new XElement(W + "rFonts", new XAttribute(W + "ascii", "Arial"), new XAttribute(W + "hAnsi", "Arial"), new XAttribute(W + "cs", "Arial")), new XElement(W + "sz", new XAttribute(W + "val", size)), new XElement(W + "szCs", new XAttribute(W + "val", size)));
                if (bold) { run.Add(new XElement(W + "b")); run.Add(new XElement(W + "bCs")); }
                if (white) run.Add(new XElement(W + "color", new XAttribute(W + "val", "FFFFFF")));
                var p = new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "bidi", new XAttribute(W + "val", rtl ? "1" : "0")), new XElement(W + "jc", new XAttribute(W + "val", "start")), new XElement(W + "spacing", new XAttribute(W + "after", "100"))));
                var lines = Clean(text).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (var i = 0; i < lines.Length; i++) { if (i > 0) p.Add(new XElement(W + "r", new XElement(W + "br"))); p.Add(new XElement(W + "r", new XElement(run), new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), lines[i]))); }
                return p;
            }
            var widths = new[] { 1100, 2100, 3300, 3600, 1700, 3598 };
            var table = new XElement(W + "tbl", new XElement(W + "tblPr", new XElement(W + "bidiVisual"), new XElement(W + "tblW", new XAttribute(W + "w", "15398"), new XAttribute(W + "type", "dxa")), new XElement(W + "tblBorders", new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(n => new XElement(W + n, new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "C6D4E0")))), new XElement(W + "tblLayout", new XAttribute(W + "type", "fixed"))), new XElement(W + "tblGrid", widths.Select(width => new XElement(W + "gridCol", new XAttribute(W + "w", width)))));
            void AddRow(string[] values, bool header, int index)
            {
                var tr = new XElement(W + "tr"); if (header) tr.Add(new XElement(W + "trPr", new XElement(W + "tblHeader")));
                for (var i = 0; i < values.Length; i++)
                {
                    var properties = new XElement(W + "tcPr", new XElement(W + "tcW", new XAttribute(W + "w", widths[i]), new XAttribute(W + "type", "dxa")), new XElement(W + "shd", new XAttribute(W + "fill", header ? "214C70" : index % 2 == 0 ? "F0F5F9" : "FFFFFF")), new XElement(W + "tcMar", new XElement(W + "top", new XAttribute(W + "w", "100"), new XAttribute(W + "type", "dxa")), new XElement(W + "bottom", new XAttribute(W + "w", "100"), new XAttribute(W + "type", "dxa")), new XElement(W + "left", new XAttribute(W + "w", "100"), new XAttribute(W + "type", "dxa")), new XElement(W + "right", new XAttribute(W + "w", "100"), new XAttribute(W + "type", "dxa"))));
                    tr.Add(new XElement(W + "tc", properties, Paragraph(values[i], RtlText(values[i]), 28, header, header)));
                }
                table.Add(tr);
            }
            AddRow(Headers.Select((header, i) => i == 0 ? "מס׳" : header).ToArray(), true, 0); for (var i = 0; i < rows.Count; i++) AddRow(Cells(rows[i]), false, i);
            var body = new XElement(W + "body", Paragraph(title, true, 40, true), Paragraph("שאלות לבמאי · " + DateTime.Now.ToString("dd/MM/yyyy") + " · " + rows.Count + " שאלות", true, 24), table,
                new XElement(W + "sectPr", new XElement(W + "pgSz", new XAttribute(W + "w", "16838"), new XAttribute(W + "h", "11906"), new XAttribute(W + "orient", "landscape")), new XElement(W + "pgMar", new XAttribute(W + "top", "720"), new XAttribute(W + "right", "720"), new XAttribute(W + "bottom", "720"), new XAttribute(W + "left", "720"))));
            Entry(zip, "word/document.xml", new XDocument(new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), body)));
        });
    }

    internal static void ExportExcel(string path, string title, IReadOnlyList<DirectorQuestionRow> rows)
    {
        if (rows.Count == 0) throw new InvalidDataException("אין שאלות לייצוא.");
        var all = new[] { new[] { title } , Headers }.Concat(rows.Select(Cells)).ToArray();
        if (all.SelectMany(x => x).Any(x => x.Length > 32767)) throw new InvalidDataException("אחד השדות ארוך ממגבלת Excel. ייצא ל־Word או קצר את ההערה.");
        WriteAtomic(path, zip =>
        {
            Entry(zip, "[Content_Types].xml", Types(("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"), ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"), ("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"), ("/xl/tables/table1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml")));
            Entry(zip, "_rels/.rels", Relations(("rId1", "officeDocument", "xl/workbook.xml")));
            Entry(zip, "xl/_rels/workbook.xml.rels", Relations(("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "styles", "styles.xml")));
            Entry(zip, "xl/workbook.xml", new XDocument(new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R), new XElement(S + "sheets", new XElement(S + "sheet", new XAttribute("name", "שאלות לבמאי"), new XAttribute("sheetId", "1"), new XAttribute(R + "id", "rId1"))))));
            XElement Font(bool header) => new(S + "font", new XElement(S + "sz", new XAttribute("val", "16")), new XElement(S + "name", new XAttribute("val", "Arial")), header ? new XElement(S + "b") : null, header ? new XElement(S + "color", new XAttribute("rgb", "FFFFFFFF")) : null);
            XElement Style(bool header, bool rtl) => new(S + "xf", new XAttribute("numFmtId", "0"), new XAttribute("fontId", header ? "1" : "0"), new XAttribute("fillId", header ? "2" : "0"), new XAttribute("borderId", "0"), new XAttribute("xfId", "0"), new XAttribute("applyFont", "1"), new XAttribute("applyFill", "1"), new XAttribute("applyAlignment", "1"), new XElement(S + "alignment", new XAttribute("wrapText", "1"), new XAttribute("vertical", "top"), new XAttribute("horizontal", rtl ? "right" : "left"), new XAttribute("readingOrder", rtl ? "2" : "1")));
            var fonts = new XElement(S + "fonts", new XAttribute("count", "2"), Font(false), Font(true));
            var fills = new XElement(S + "fills", new XAttribute("count", "3"),
                new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "none"))),
                new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "gray125"))),
                new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "solid"),
                    new XElement(S + "fgColor", new XAttribute("rgb", "FF214C70")), new XElement(S + "bgColor", new XAttribute("indexed", "64")))));
            var borders = new XElement(S + "borders", new XAttribute("count", "1"), new XElement(S + "border", new[] { "left", "right", "top", "bottom", "diagonal" }.Select(n => new XElement(S + n))));
            var styleBase = new XElement(S + "cellStyleXfs", new XAttribute("count", "1"), new XElement(S + "xf", new XAttribute("numFmtId", "0"), new XAttribute("fontId", "0"), new XAttribute("fillId", "0"), new XAttribute("borderId", "0")));
            var styles = new XElement(S + "cellXfs", new XAttribute("count", "3"), Style(false, true), Style(true, true), Style(false, false));
            var names = new XElement(S + "cellStyles", new XAttribute("count", "1"), new XElement(S + "cellStyle", new XAttribute("name", "Normal"), new XAttribute("xfId", "0"), new XAttribute("builtinId", "0")));
            Entry(zip, "xl/styles.xml", new XDocument(new XElement(S + "styleSheet", fonts, fills, borders, styleBase, styles, names)));
            var data = new XElement(S + "sheetData");
            for (var rowIndex = 0; rowIndex < all.Length; rowIndex++)
            {
                var values = all[rowIndex]; var row = new XElement(S + "row", new XAttribute("r", rowIndex + 1), new XAttribute("ht", rowIndex < 2 ? 32 : Math.Min(400, 28 * values.Select(v => Math.Max(1, v.Split('\n').Sum(line => Math.Max(1, (line.Length + 34) / 35)))).Max())), new XAttribute("customHeight", "1"));
                for (var i = 0; i < values.Length; i++) row.Add(new XElement(S + "c", new XAttribute("r", ((char)('A' + i)).ToString() + (rowIndex + 1)), new XAttribute("s", rowIndex < 2 ? "1" : RtlText(values[i]) ? "0" : "2"), new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Clean(values[i])))));
                data.Add(row);
            }
            var last = rows.Count + 2; var range = "A2:F" + last;
            Entry(zip, "xl/worksheets/sheet1.xml", new XDocument(new XElement(S + "worksheet", new XAttribute(XNamespace.Xmlns + "r", R), new XElement(S + "sheetViews", new XElement(S + "sheetView", new XAttribute("workbookViewId", "0"), new XAttribute("rightToLeft", "1"), new XElement(S + "pane", new XAttribute("ySplit", "2"), new XAttribute("topLeftCell", "A3"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))), new XElement(S + "cols", new[] { 10, 32, 50, 60, 22, 55 }.Select((width, i) => new XElement(S + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", width), new XAttribute("customWidth", "1")))), data, new XElement(S + "mergeCells", new XAttribute("count", "1"), new XElement(S + "mergeCell", new XAttribute("ref", "A1:F1"))), new XElement(S + "tableParts", new XAttribute("count", "1"), new XElement(S + "tablePart", new XAttribute(R + "id", "rId1"))))));
            Entry(zip, "xl/worksheets/_rels/sheet1.xml.rels", Relations(("rId1", "table", "../tables/table1.xml")));
            Entry(zip, "xl/tables/table1.xml", new XDocument(new XElement(S + "table", new XAttribute("id", "1"), new XAttribute("name", "DirectorQuestions"), new XAttribute("displayName", "DirectorQuestions"), new XAttribute("ref", range), new XAttribute("totalsRowShown", "0"), new XElement(S + "autoFilter", new XAttribute("ref", range)), new XElement(S + "tableColumns", new XAttribute("count", "6"), Headers.Select((name, i) => new XElement(S + "tableColumn", new XAttribute("id", i + 1), new XAttribute("name", name)))), new XElement(S + "tableStyleInfo", new XAttribute("name", "TableStyleMedium2"), new XAttribute("showFirstColumn", "0"), new XAttribute("showLastColumn", "0"), new XAttribute("showRowStripes", "1"), new XAttribute("showColumnStripes", "0")))));
        });
    }
}

