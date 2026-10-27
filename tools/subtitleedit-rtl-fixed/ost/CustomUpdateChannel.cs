using Nikse.SubtitleEdit.Logic.Config;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Help.CheckForUpdates;

internal sealed record CustomUpdatePackage(long RunId, long ArtifactId, string Name, string Sha256, bool ReleaseAsset = false, string Commit = "");

internal static class CustomUpdateChannel
{
    internal const string DownloadPage = "https://github.com/Elior-sasi/subtitleedit-elior/actions/workflows/subtitleedit-rtl-fixed-hebrew.yml?query=branch%3Amain+is%3Asuccess";
    private const string RepoApi = "repos/Elior-sasi/subtitleedit-elior";
    private static string GhPath => Path.Combine(AppContext.BaseDirectory, "custom-updater", "gh.exe");
    internal static bool AuthenticationRequired { get; private set; }
    internal static bool IsInstallPending { get; private set; }
    internal static CustomUpdatePackage? AvailablePackage { get; private set; }
    internal static string Status { get; private set; } = "עדכונים לגרסה המותאמת שלנו";
    internal static string Description
    {
        get
        {
            var description = "הגרסה שלנו: " + Se.Version;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "custom-build.json")));
                var root = doc.RootElement;
                description += "\nבנייה: " + root.GetProperty("run_id").ToString();
                if (root.TryGetProperty("built_at", out var date)) description += "\nתאריך בנייה: " + date.GetString();
                if (root.TryGetProperty("changes", out var changes)) description += "\n\nמה השתנה:\n" + string.Join("\n", changes.EnumerateArray().Select(x => "• " + x.GetString()));
            }
            catch { }
            return description + "\n\nעדכונים מהמאגר הציבורי הייעודי שלנו בלבד, ללא התחברות ל־GitHub. ההתקנה ממתינה לסגירת התוכנה ושומרת את Settings.json ואת קובצי העבודה.";
        }
    }

    internal static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        AvailablePackage = null;
        AuthenticationRequired = false;
        var result = new UpdateCheckResult { LatestVersion = Se.Version, ChangeLogText = Description };
        if (!OperatingSystem.IsWindows()) return result;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var releases = JsonDocument.Parse(await ApiAsync(RepoApi + "/releases?per_page=100", timeout.Token));
        CustomUpdatePackage? package = null;
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var match = Regex.Match(tag, @"^custom-build-(\d+)-(\d+)$");
            if (!match.Success || release.GetProperty("draft").GetBoolean()) continue;
            if (!long.TryParse(match.Groups[1].Value, out var id) || !int.TryParse(match.Groups[2].Value, out var attempt)) continue;
            if (id <= CurrentRunId() || (package != null && id <= package.RunId)) continue;
            var commit = release.GetProperty("target_commitish").GetString() ?? "";
            var candidate = ParseReleasePackage(release.GetRawText(), id, commit, attempt);
            if (candidate == null) continue;
            using var run = JsonDocument.Parse(await ApiAsync(RepoApi + $"/actions/runs/{id}", timeout.Token));
            var metadata = run.RootElement;
            if (metadata.GetProperty("conclusion").GetString() != "success" ||
                metadata.GetProperty("head_branch").GetString() != "main" ||
                metadata.GetProperty("head_sha").GetString() != commit ||
                metadata.GetProperty("path").GetString() != ".github/workflows/subtitleedit-rtl-fixed-hebrew.yml" ||
                metadata.GetProperty("run_attempt").GetInt32() != attempt) continue;
            package = candidate;
        }
        if (package == null)
        {
            Status = IsInstallPending ? "העדכון יותקן כשכל חלונות התוכנה ייסגרו" : "הגרסה המותאמת שלנו מעודכנת";
            return result;
        }
        AvailablePackage = package;
        result.ChangeLogText = Description + "\n\nעדכון זמין — בנייה " + package.RunId + "\n" + package.Name;
        result.IsNewVersionAvailable = !IsInstallPending;
        result.LatestVersion = package.Name;
        Status = IsInstallPending ? "העדכון יותקן כשכל חלונות התוכנה ייסגרו" : "עדכון מותאם חדש זמין: " + package.Name;
        return result;
    }

    internal static long LatestSuccessfulRun(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var run in doc.RootElement.GetProperty("workflow_runs").EnumerateArray())
            if (run.GetProperty("conclusion").GetString() == "success" && run.GetProperty("head_branch").GetString() == "main")
                return run.GetProperty("id").GetInt64();
        return 0;
    }

    internal static CustomUpdatePackage? ParsePackage(string json, long runId)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var a in doc.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("SE-", StringComparison.Ordinal) || !name.Contains("RTL-Hebrew-OST", StringComparison.Ordinal) || a.GetProperty("expired").GetBoolean()) continue;
            var digest = a.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) continue;
            return new CustomUpdatePackage(runId, a.GetProperty("id").GetInt64(), name, digest[7..]);
        }
        return null;
    }

    private static long CurrentRunId()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "custom-build.json");
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("run_id").GetInt64();
    }

    internal static CustomUpdatePackage? ParseReleasePackage(string json, long runId, string commit, int attempt)
    {
        if (runId <= 0 || attempt <= 0 || commit.Length != 40 || !commit.All(Uri.IsHexDigit)) return null;
        using var doc = JsonDocument.Parse(json);
        var release = doc.RootElement;
        if (release.GetProperty("draft").GetBoolean() || !release.GetProperty("prerelease").GetBoolean()
            || release.GetProperty("tag_name").GetString() != $"custom-build-{runId}-{attempt}"
            || release.GetProperty("target_commitish").GetString() != commit) return null;
        CustomUpdatePackage? found = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("SE-", StringComparison.Ordinal) || !name.EndsWith("-RTL-Hebrew-OST-AutoUpdate.zip", StringComparison.Ordinal)) continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) return null;
            if (found != null) return null;
            found = new CustomUpdatePackage(runId, asset.GetProperty("id").GetInt64(), name, digest[7..], true, commit);
        }
        return found;
    }

    internal static async Task LoginAsync(Action<string> status, CancellationToken token)
    {
        var result = await RunGhAsync(new[] { "auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web", "--clipboard" }, token, status, true);
        if (result.Code != 0) throw new InvalidOperationException("ההתחברות לא הושלמה. נסה שוב ובחר את חשבון GitHub שלך.");
    }

    private static async Task<string> ApiAsync(string endpoint, CancellationToken token)
    {
        using var http = PublicHttp();
        return await http.GetStringAsync("https://api.github.com/" + endpoint, token);
    }

    private static HttpClient PublicHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SubtitleEdit-Elior-Updater/1.0");
        return http;
    }

    private static Process MakeGh(string[] args)
    {
        var info = new ProcessStartInfo(GhPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        info.Environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1";
        info.Environment["GH_PAGER"] = "cat";
        return new Process { StartInfo = info };
    }

    private static async Task<(int Code, string Output)> RunGhAsync(string[] args, CancellationToken token, Action<string>? status = null, bool login = false)
    {
        using var process = MakeGh(args);
        process.Start();
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } });
        // --web works without a terminal; a newline also answers the optional browser prompt.
        if (login) await process.StandardInput.WriteLineAsync();
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(token) is { } line)
                if (login && !string.IsNullOrWhiteSpace(line)) status?.Invoke(line);
        }, token);
        await Task.WhenAll(process.WaitForExitAsync(token), errors);
        return (process.ExitCode, await output);
    }

    internal static async Task<string> DownloadAsync(IProgress<float> progress, CancellationToken token)
    {
        var package = AvailablePackage ?? throw new InvalidOperationException("בדוק עדכונים לפני ההורדה.");
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Subtitle Edit Custom", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
        var archive = Path.Combine(staging, "artifact.zip");
        if (!package.ReleaseAsset) throw new InvalidDataException("Expected a published custom release.");
        using var asset = JsonDocument.Parse(await ApiAsync(RepoApi + $"/releases/assets/{package.ArtifactId}", token));
        var downloadUrl = new Uri(asset.RootElement.GetProperty("browser_download_url").GetString()!);
        if (downloadUrl.Scheme != "https" || downloadUrl.Host != "github.com" ||
            !downloadUrl.AbsolutePath.StartsWith("/Elior-sasi/subtitleedit-elior/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected release download location.");
        using (var http = PublicHttp())
        using (var input = await http.GetStreamAsync(downloadUrl, token))
        using (var output = File.Create(archive)) await input.CopyToAsync(output, token);
        progress.Report(0.75f);
        await VerifyArchiveAsync(archive, package.Sha256, token);
        var payload = Path.Combine(staging, "payload");
        ExtractPackage(archive, payload, package.ReleaseAsset);
        ValidatePayload(payload, package.RunId);
        if (package.ReleaseAsset)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, "custom-build.json")));
            if (manifest.RootElement.GetProperty("commit").GetString() != package.Commit)
                throw new InvalidDataException("חבילת העדכון אינה תואמת לקוד שנבדק.");
        }
        progress.Report(1.0f);
        return payload;
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    internal static async Task VerifyArchiveAsync(string path, string expected, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("אימות חבילת העדכון נכשל. התוכנה הקיימת לא שונתה.");
    }

    internal static void ExtractPackage(string archive, string payload, bool releaseAsset)
    {
        if (releaseAsset) { ZipFile.ExtractToDirectory(archive, payload); return; }
        var outer = Path.Combine(Path.GetDirectoryName(payload)!, "outer");
        ZipFile.ExtractToDirectory(archive, outer);
        var inner = Directory.GetFiles(outer, "*.zip", SearchOption.TopDirectoryOnly);
        if (inner.Length != 1) throw new InvalidDataException("מבנה חבילת העדכון אינו תקין.");
        ZipFile.ExtractToDirectory(inner[0], payload);
    }

    internal static void ValidatePayload(string payload, long runId)
    {
        if (!File.Exists(Path.Combine(payload, "SubtitleEdit.exe")) || !File.Exists(Path.Combine(payload, "custom-updater", "gh.exe"))) throw new InvalidDataException("חבילת העדכון אינה מכילה את כל קובצי התוכנה.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, "custom-build.json")));
        if (manifest.RootElement.GetProperty("run_id").GetInt64() != runId || manifest.RootElement.GetProperty("channel").GetString() != "Elior-sasi/subtitleedit-elior/main") throw new InvalidDataException("חבילת העדכון אינה מהערוץ שלנו.");
    }

    internal static void StartInstallAfterExit(string payload)
    {
        if (IsInstallPending) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var script = Path.Combine(Path.GetDirectoryName(payload)!, "install.ps1");
        File.WriteAllText(script, CustomUpdateInstaller.Script, new UTF8Encoding(true));
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-WaitProcessId", Environment.ProcessId.ToString(), "-SourceDirectory", payload, "-TargetDirectory", AppContext.BaseDirectory }) info.ArgumentList.Add(arg);
        using var helper = Process.Start(info) ?? throw new InvalidOperationException("לא ניתן להפעיל את מתקין העדכון.");
        IsInstallPending = true;
    }
}


