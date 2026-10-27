using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nikse.SubtitleEdit.Features.Files.ImportPlainText;

public partial class ImportPlainTextViewModel
{
    internal bool CustomScriptMode { get; private set; }
    private string? _customAlignedText;
    internal void InitializeCustomScript(string text, string media)
    {
        CustomScriptMode = true;
        _videoFileName = media;
        SelectedSplitAtOption = SplitAtOptions[1]; // prepared two-line cues separated by blank lines
        PlainText = text;
        MarkDirty();
    }
    private string CustomScriptText() => string.Join("\n\u001f\n", Subtitles.Select(s => s.Text));
    internal bool CustomScriptCanImport => !CustomScriptMode || (!IsAligning && !_dirty && _customAlignedText != null && _customAlignedText == CustomScriptText() && Subtitles.All(s => s.StartTime >= TimeSpan.Zero && s.EndTime > s.StartTime));
    internal void CustomScriptInvalidate() { _customAlignedText = null; }
    internal void CustomScriptRecordAlignment() { if (CustomScriptMode) _customAlignedText = CustomScriptText(); }
    // None of the aligners offered in Subtitle Edit v5.4.0-beta6 has a Hebrew model
    // (wav2vec2: en/de/fr/es/it/ja/zh/nl/pt/ar/uk/cs; Canary CTC; Qwen3). Add a choice here only
    // after it is verified on real Hebrew speech.
    internal static readonly string[] HebrewCapableAlignerChoices = Array.Empty<string>();
    internal static bool LooksHebrew(string text) => Regex.Matches(text ?? "", @"\p{IsHebrew}").Count >= 3;
    internal static bool NeedsHebrewAlignerWarning(string text, string alignerChoice) => LooksHebrew(text) && !HebrewCapableAlignerChoices.Contains(alignerChoice);
    internal const string HebrewAlignerWarning = "התסריט בעברית, אבל למודל היישור שנבחר אין מודל לעברית. התזמונים עלולים להיות שגויים גם אם ייראו סבירים. לתסריט עברי מול דיבור עברי השתמש ב\"תסריט שונה מהדיבור - השוואה\", שמתמלל ומשווה. להמשיך בכל זאת?";
}

