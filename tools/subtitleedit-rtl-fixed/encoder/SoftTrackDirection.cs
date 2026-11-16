using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;

namespace Nikse.SubtitleEdit.Features.Video.BurnIn;

public partial class BurnInViewModel
{
    // Never pass the editor's original through a save/format conversion. Only write
    // a temp copy if visible Hebrew-led text actually needs a direction hint.
    internal string PrepareSoftTrack(string file)
    {
        var ext = Path.GetExtension(file);
        bool srt = ext.Equals(".srt", StringComparison.OrdinalIgnoreCase);
        var input = File.ReadAllText(file, LanguageAutoDetect.GetEncodingFromFile(file));
        var lines = Regex.Split(input, "(\r\n|\r|\n)");
        bool inCue = false, events = false, changed = false;
        int commas = 9; // standard ASS and SSA Dialogue have 9 fields before Text
        for (int i=0; i<lines.Length; i+=2)
        {
            var line = lines[i];
            string updated;
            if (srt)
            {
                if (string.IsNullOrWhiteSpace(line)) { inCue=false; continue; }
                if (!inCue) { inCue = line.Contains("-->", StringComparison.Ordinal); continue; }
                updated = DirectionForLine(line);
            }
            else
            {
                var trim = line.Trim();
                if (trim.StartsWith('[')) { events = trim.Equals("[Events]",StringComparison.OrdinalIgnoreCase); commas=9; continue; }
                if (!events) continue;
                if (trim.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    var fields=trim[7..].Split(',');
                    commas=fields[^1].Trim().Equals("Text",StringComparison.OrdinalIgnoreCase) ? fields.Length-1 : -1;
                    continue;
                }
                if (commas<1 || !trim.StartsWith("Dialogue:",StringComparison.OrdinalIgnoreCase)) continue;
                int pos=0;
                for (int n=0;n<commas;n++) { pos=line.IndexOf(',',pos)+1; if (pos==0) break; }
                if (pos==0) continue;
                var body=line[pos..];
                if (body.Contains(@"\p",StringComparison.Ordinal) && Regex.IsMatch(body,@"\\p[1-9]")) continue;
                // Never touch style, timecode or metadata fields. Preserve ASS hard line breaks.
                var parts = Regex.Split(body, @"(\\[Nn])");
                for(int j=0;j<parts.Length;j+=2) parts[j]=DirectionForLine(parts[j]);
                updated = line[..pos]+string.Concat(parts);
            }
            if (updated!=line) { changed=true; lines[i]=updated; }
        }
        if (!changed) return file;
        var temp = _tempSubtitleFiles.GetFileName(ext);
        File.WriteAllText(temp,string.Concat(lines),new UTF8Encoding(false));
        return temp;
    }

    private static string DirectionForLine(string text)
    {
        if (text.Any(c => c is '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')) return text;
        var visible = Regex.Replace(text, @"\{[^}]*\}|<[^>]*>|\\h", "");
        var first = visible.FirstOrDefault(char.IsLetter);
        return first is >= 'א' and <= 'ת' or >= 'יִ' and <= 'ﭏ' ? "\u202b"+text+"\u202c" : text;
    }
}
