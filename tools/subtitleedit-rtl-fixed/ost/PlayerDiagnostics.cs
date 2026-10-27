using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Nikse.SubtitleEdit.Logic.VideoPlayers;

/// <summary>
/// Focused player lifecycle log (custom build). One line per step of opening, closing and
/// playing media, written to "player-diagnostics.log" next to Settings.json, so a stuck player
/// can be told apart from a load that was never sent, a native command that never returned,
/// a command that failed, and a command that succeeded while the media itself failed later.
/// <para>
/// Privacy: no subtitle text, no keys, no full paths. A media file is recorded only as its
/// extension plus a short one-way hash of its full path (<see cref="MediaTag"/>), enough to see
/// whether two log lines are about the same file. mpv messages have path-like text masked.
/// </para>
/// <para>
/// Pure logging: nothing here retries, recreates, waits on or changes the player.
/// </para>
/// </summary>
public static class PlayerDiagnostics
{
    public const string FileName = "player-diagnostics.log";
    public const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _nextPlayerId;
    private static long _nextControlId;
    private static long _nextOpenGeneration;
    private static string? _folder;
    private static bool _failed;

    private static readonly Regex PathLike = new(
        @"([A-Za-z]:[\\/]|\\\\|/(?:home|Users|mnt|media)/)[^\s""'<>|]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Folder the log goes to. Set once at startup (the settings folder); tests point it elsewhere.</summary>
    public static string? Folder
    {
        get => _folder;
        set { lock (Gate) { _folder = value; _failed = false; } }
    }

    public static long NewPlayerId() => Interlocked.Increment(ref _nextPlayerId);
    public static long NewControlId() => Interlocked.Increment(ref _nextControlId);
    public static long NewOpenGeneration() => Interlocked.Increment(ref _nextOpenGeneration);

    public static long Now => Clock.ElapsedMilliseconds;

    /// <summary>Extension plus 8 hex chars of SHA-256 of the full path - never the path itself.</summary>
    public static string MediaTag(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "none";
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            return "url#" + ShortHash(path);
        }

        string ext;
        try
        {
            ext = Path.GetExtension(path);
        }
        catch
        {
            ext = string.Empty;
        }

        if (ext.Length > 8 || ext.IndexOfAny(new[] { '\\', '/', ' ' }) >= 0)
        {
            ext = string.Empty;
        }

        return (string.IsNullOrEmpty(ext) ? "noext" : ext.ToLowerInvariant()) + "#" + ShortHash(path);
    }

    /// <summary>Masks anything path-shaped in free text (mpv messages name the file they fail on).</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        oneLine = PathLike.Replace(oneLine, m =>
        {
            // Sentence punctuation after a path ("... file.mkv.") is not part of it.
            var value = m.Value.TrimEnd('.', ',', ';', ':', ')', ']');
            return "<" + MediaTag(value) + ">" + m.Value[value.Length..];
        });
        return oneLine.Length > 300 ? oneLine[..300] + "…" : oneLine;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    /// <summary>Writes one line. Never throws; disables itself after an I/O failure.</summary>
    public static void Log(string area, string message)
    {
        try
        {
            var folder = _folder;
            if (string.IsNullOrEmpty(folder) || _failed)
            {
                return;
            }

            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} +{Now,9}ms T{Environment.CurrentManagedThreadId,-3} {area,-8} {message}{Environment.NewLine}");

            lock (Gate)
            {
                var path = Path.Combine(folder, FileName);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }

                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            _failed = true; // diagnostics must never affect the player
        }
    }

    /// <summary>Formats a double for the log (invariant, 3 decimals; NaN/inf kept readable).</summary>
    public static string N(double value) =>
        double.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>mpv_end_file_reason names (client.h).</summary>
    public static string EndFileReason(int reason) => reason switch
    {
        0 => "eof",
        2 => "stop",
        3 => "quit",
        4 => "error",
        5 => "redirect",
        _ => "reason" + reason.ToString(CultureInfo.InvariantCulture),
    };
}

