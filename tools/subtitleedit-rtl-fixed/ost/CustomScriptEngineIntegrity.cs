using System;
using System.IO;
using System.Security.Cryptography;

namespace Nikse.SubtitleEdit.Features.Video.SpeechToText;

public partial class DownloadSpeechToTextEngineViewModel
{
    internal bool CustomScriptVerifyCpuDownload { get; set; }
    internal const string CustomScriptCpuSha256 = "997bc39f541e5d5d0bd14144159c038bd95d6ab8b523718c34a54e03ab404f47";
    // Official CrispStrobe/CrispASR v0.8.43 asset digest, checked BEFORE unpack or running binaries.
    internal static void VerifyCustomScriptEngine(Stream stream)
    {
        var original = stream.Position;
        try { stream.Position = 0; if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(CustomScriptCpuSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("בדיקת השלמות של מנוע התזמון נכשלה. הקובץ לא יופעל; נסה להוריד שוב."); }
        finally { stream.Position = original; }
    }
}

