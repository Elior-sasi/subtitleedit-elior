using System;
using System.IO;

namespace Nikse.SubtitleEdit.Features.Video.BurnIn;

internal static class SubtitleMux
{
    public static string SoftOnly(string video, string subtitles, string output)
        => $"-nostdin -n -i {PathArg(video)} -i {PathArg(subtitles)} -map 0:V:0 -map 0:a? -map 1:s:0 -map_metadata 0 -map_chapters 0 -c:v copy -c:a copy {SubtitleOptions(output)} {PathArg(output)}";

    public static string SubtitleOptions(string output)
        => Path.GetExtension(output).ToLowerInvariant() switch {
            ".mkv" => "-c:s copy -disposition:s:0 0",
            ".mp4" => "-c:s mov_text -disposition:s:0 0",
            _ => throw new InvalidDataException("Choose MKV or MP4 for a text subtitle track.")
        };

    public static string PathArg(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new InvalidDataException("Invalid media file path.");
        return "\"" + path + "\"";
    }
}
