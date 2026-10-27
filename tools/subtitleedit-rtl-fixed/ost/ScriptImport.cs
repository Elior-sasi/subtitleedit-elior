using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Nikse.SubtitleEdit.Features.Files.ImportPlainText;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal static class ScriptImport
{
    internal const int MaxText = 1000000;
    internal static string Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 20 * 1024 * 1024) throw new InvalidDataException("התסריט גדול מדי. פצל אותו לקבצים קטנים יותר.");
        var ext = info.Extension.ToLowerInvariant();
        string text;
        if (ext == ".docx")
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("קובץ Word אינו תקין.");
            if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("תוכן ה־Word גדול מדי.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
            var doc = XDocument.Load(reader); XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            text = string.Join("\n", doc.Descendants(w + "p").Select(p => string.Concat(p.Descendants().Where(e => e.Name == w + "t" || e.Name == w + "tab" || e.Name == w + "br" || e.Name == w + "cr").Select(e => e.Name == w + "t" ? e.Value : e.Name == w + "tab" ? "\t" : "\n"))));
        }
        else if (ext is ".txt" or ".md") text = File.ReadAllText(path);
        else if (ext == ".srt")
        {
            var cues = CustomProject.ReadSrt(File.ReadAllText(path));
            if (cues.Count == 0) throw new InvalidDataException("לא נמצאו כתוביות SRT תקינות.");
            text = string.Join("\n\n", cues.Select(c => c.Text));
        }
        else throw new InvalidDataException("בחר DOCX, TXT, SRT או MD. קובץ DOC ישן יש לשמור כ־DOCX.");
        if (text.Length > MaxText || string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("התסריט ריק או ארוך מדי.");
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }
    // Opt-in literal prefix removal. No inferred speaker identities, stage-direction deletion or rewriting.
    internal static string Prepare(string text, bool removeNames)
    {
        if (text.Length > MaxText) throw new InvalidDataException("התסריט ארוך מדי.");
        if (!removeNames) return text;
        return string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => Regex.Replace(line, @"^\s*[\p{L}\p{M}][\p{L}\p{M}\p{N} .’'\-]{0,35}:\s+", "", RegexOptions.None, TimeSpan.FromSeconds(1))));
    }
    internal static string Split(string text, int max)
    {
        if (max < 20 || max > 100) throw new InvalidDataException("בחר מגבלה בין 20 ל־100 תווים בשורה.");
        var cues = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var words = Regex.Matches(paragraph, @"\S+").Select(m => m.Value).ToArray();
            var current = ""; var lines = new List<string>();
            void EmitLine() { if (current.Length > 0) { lines.Add(current); current = ""; } if (lines.Count == 2) { cues.Add(string.Join("\n", lines)); lines.Clear(); } }
            foreach (var word in words)
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > max) EmitLine();
                current += (current.Length == 0 ? "" : " ") + word;
            }
            EmitLine(); if (lines.Count > 0) cues.Add(string.Join("\n", lines));
        }
        if (cues.Count == 0) throw new InvalidDataException("אין טקסט לתזמון.");
        return string.Join("\n\n", cues);
    }
}

