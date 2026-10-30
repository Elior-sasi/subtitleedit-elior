using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal static class LocalMfa
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string Runner => Path.Combine(AppContext.BaseDirectory, "local-mfa", "run_local_mfa.py");

    internal static ProcessStartInfo PythonCommand(string python, string cache, params string[] arguments)
    {
        if (!Path.IsPathFullyQualified(python) || !File.Exists(python) ||
            !Path.GetFileName(python).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("בחר python.exe מתוך סביבת Conda שבה הותקן MFA 3.4.3.");
        if (!File.Exists(Runner)) throw new InvalidDataException("רכיב MFA המקומי חסר בהתקנה.");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(Runner)! };
        foreach (var a in new[] { "-I", "-u", Runner, "--cache", cache }.Concat(arguments)) start.ArgumentList.Add(a);
        // The selected conda environment supplies Kaldi/OpenFst DLLs; no global PATH changes.
        var prefix = Path.GetDirectoryName(python)!;
        start.Environment["PATH"] = string.Join(Path.PathSeparator.ToString(), new[] { Path.Combine(prefix, "Library", "bin"), Path.Combine(prefix, "Scripts"), prefix, Environment.GetEnvironmentVariable("PATH") ?? "" });
        start.Environment.Remove("PYTHONPATH");
        return start;
    }

    internal static async Task Run(ProcessStartInfo start, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        using var p = Process.Start(start) ?? throw new IOException("לא ניתן להפעיל את המנוע המקומי.");
        async Task Drain(StreamReader reader, bool report)
        {
            var buffer = new char[2048]; var line = "";
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), limit.Token);
                if (count == 0) break;
                if (!report) continue;
                foreach (var c in buffer.AsSpan(0, count).ToArray())
                {
                    if (c == '\n') { if (line.Length is > 0 and < 160) progress?.Report(line.Trim()); line = ""; }
                    else if (line.Length < 160) line += c;
                }
            }
        }
        var stdout = Drain(p.StandardOutput, true); var stderr = Drain(p.StandardError, false);
        try
        {
            await Task.WhenAll(p.WaitForExitAsync(limit.Token), stdout, stderr);
            token.ThrowIfCancellationRequested();
            if (p.ExitCode != 0) throw new InvalidDataException("MFA המקומי נכשל. בדוק שהסביבה היא MFA 3.4.3, שהמודלים לאנגלית הורדו ושהתסריט תואם לשמע.");
        }
        catch
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { await p.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            if (limit.IsCancellationRequested && !token.IsCancellationRequested) throw new TimeoutException("המנוע המקומי חרג ממגבלת הזמן. פצל את הפרק ונסה שוב.");
            throw;
        }
    }

    internal static async Task Exclusive(Func<Task> action, CancellationToken token)
    {
        if (!await Gate.WaitAsync(0, token)) throw new InvalidOperationException("פעולת MFA מקומית אחרת עדיין פועלת.");
        try { await action(); } finally { Gate.Release(); }
    }

    internal static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    internal static string Request(IReadOnlyList<CloudMfaCue> cues)
    {
        if (cues.Count is < 1 or > 5000 || cues.Any(c => string.IsNullOrWhiteSpace(c.Text) || c.Text.Length > 1000) || cues.Sum(c => c.Text.Length) > 400000)
            throw new InvalidDataException("התסריט גדול מדי. פצל אותו לחלקים קצרים יותר.");
        if (cues.Any(c => c.Text.Any(ch => ch is >= '\u0590' and <= '\u05ff')))
            throw new InvalidDataException("מסלול MFA המקומי הזה משתמש במודלים לאנגלית בלבד.");
        return JsonSerializer.Serialize(new { cues = cues.Select(c => new { id = c.Id, text = c.Text }) });
    }

    internal sealed record Result(List<CloudMfaImport.Cue> Cues, string ReviewText);

    internal static Result ReadResult(string path, IReadOnlyList<CloudMfaCue> cues, string audioHash, string requestHash)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("לא התקבלה תוצאה תקינה מהמנוע המקומי.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = CloudMfaClient.ParseResult(doc.RootElement);
        if (r.JobId != "local000000000000000000000000000" || r.AudioDuration <= 0 || r.AudioDuration > 7200) throw new InvalidDataException("זהות תוצאת היישור אינה תקינה.");
        var validated = CloudMfaImport.Validate(r, cues, audioHash, requestHash, true);
        return new Result(validated, r.SummaryText + "\n\n" + string.Join("\n\n", r.Cues.Select((c, i) => CloudMfaImport.Describe(c, cues[i].Text))));
    }
}
