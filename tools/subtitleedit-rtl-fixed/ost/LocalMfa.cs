using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Threading;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal static class LocalMfa
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string Runner => Path.Combine(AppContext.BaseDirectory, "local-mfa", "run_local_mfa.py");

    // A configured path wins; discovery only examines this environment name in
    // known per-user conda roots. Never execute arbitrary PATH/Python candidates.
    internal static string FindPython(string configured, string profile)
    {
        bool Valid(string p) => Path.IsPathFullyQualified(p) && File.Exists(p) && Path.GetFileName(p).Equals("python.exe", StringComparison.OrdinalIgnoreCase);
        configured = configured.Trim();
        if (Valid(configured)) return configured;
        foreach (var root in new[] { "miniforge3", "mambaforge", "miniconda3", "anaconda3", Path.Combine("AppData", "Local", "miniforge3"), Path.Combine("AppData", "Local", "miniconda3"), ".conda" })
        {
            var candidate = Path.Combine(profile, root, "envs", "subtitleedit-mfa", "python.exe");
            if (Valid(candidate)) return candidate;
        }
        return "";
    }

    internal static bool ModelsReady(string cache) =>
        File.Exists(Path.Combine(cache, "pretrained_models", "acoustic", "english_us_arpa.zip")) &&
        File.Exists(Path.Combine(cache, "pretrained_models", "dictionary", "english_us_arpa.dict"));

    internal sealed class ProgressDisplay : StackPanel
    {
        internal readonly TextBlock Stage = new() { Text = "מוכן לבדיקה.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 16, FlowDirection = Avalonia.Media.FlowDirection.RightToLeft };
        internal readonly ProgressBar Bar = new() { Minimum = 0, Maximum = 100, Height = 18, IsVisible = false };
        internal readonly TextBlock Detail = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 16, FlowDirection = Avalonia.Media.FlowDirection.RightToLeft };
        private readonly Stopwatch _clock = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private double? _percent;
        internal ProgressDisplay()
        {
            Name = "LocalMfaProgress";
            AutomationProperties.SetName(Bar, "התקדמות השלב הנוכחי של MFA");
            Children.Add(Stage); Children.Add(Bar); Children.Add(Detail);
            _timer.Tick += (_, _) => RefreshTime();
            DetachedFromVisualTree += (_, _) => Stop();
        }
        internal void Begin(string label)
        {
            _clock.Restart(); _timer.Start(); Bar.IsVisible = true; ApplyStage(label);
        }
        internal void ApplyStage(string label)
        {
            Stage.Text = label; _percent = null; Bar.Value = 0; Bar.IsIndeterminate = true; RefreshTime();
        }
        internal void Apply(string message)
        {
            if (message.StartsWith("mfa_progress:", StringComparison.Ordinal) &&
                double.TryParse(message.Substring(13), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 && n <= 100)
            {
                _percent = n; Bar.IsIndeterminate = false; Bar.Value = n; RefreshTime(); return;
            }
            var label = message switch
            {
                "preparing" => "מכין תסריט ושמע…", "aligning_pass1" => "מיישר את התסריט לשמע — מעבר ראשון…",
                "segmenting" => "מזהה קטעי דיבור…", "aligning_pass2" => "מדייק את התזמון בתוך קטעי הדיבור…",
                "downloading_dictionary" => "מוריד מילון הגייה לאנגלית…", "downloading_acoustic" => "מוריד מודל אקוסטי לאנגלית…",
                _ => null,
            };
            if (label != null) ApplyStage(label);
        }
        private void RefreshTime()
        {
            var elapsed = _clock.Elapsed;
            Detail.Text = (_percent.HasValue ? $"{_percent.Value:0}% בפעולת המנוע הנוכחית · " : "ממתין לנתוני התקדמות מהמנוע · ") +
                $"זמן שחלף: {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
            AutomationProperties.SetName(Bar, Detail.Text);
        }
        internal void Stop() { _timer.Stop(); _clock.Stop(); }
        internal void Finish(string label, bool success = false)
        {
            Stop(); Stage.Text = label; Bar.IsIndeterminate = false;
            // 100% overall is reserved for a validated, completed alignment.
            if (success) { Bar.Value = 100; _percent = 100; RefreshTime(); Detail.Text = "הושלם · " + Detail.Text; }
            else { Bar.IsVisible = false; Detail.Text = "זמן כולל: " + _clock.Elapsed.ToString(@"hh\:mm\:ss"); }
        }
    }

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

