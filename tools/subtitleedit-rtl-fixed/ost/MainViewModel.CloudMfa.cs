using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

// Cloud MFA alignment inside the existing script-to-subtitles flow. The local CrispASR
// path is unchanged; nothing leaves the computer before an explicit, informed click.
public partial class MainViewModel
{
    private static string CloudMfaFolder => Path.Combine(Se.DataFolder, "CloudMfa");
    private static string CloudMfaConfigPath => Path.Combine(CloudMfaFolder, "config.json");
    private static string CloudMfaPendingPath => Path.Combine(CloudMfaFolder, "pending.json");

    internal sealed class CloudMfaConfig { public string ServiceUrl { get; set; } = ""; public string Language { get; set; } = ""; }
    internal sealed class CloudMfaPending { public string RequestId { get; set; } = ""; public string JobId { get; set; } = ""; public DateTime Created { get; set; } }

    internal static CloudMfaConfig LoadCloudMfaConfig()
    {
        try { return File.Exists(CloudMfaConfigPath) ? JsonSerializer.Deserialize<CloudMfaConfig>(File.ReadAllText(CloudMfaConfigPath)) ?? new() : new(); }
        catch { return new(); }
    }
    private static void SaveCloudMfaConfig(CloudMfaConfig config)
    {
        Directory.CreateDirectory(CloudMfaFolder);
        var temp = CloudMfaConfigPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(config)); File.Move(temp, CloudMfaConfigPath, true);
    }
    private static Dictionary<string, CloudMfaPending> LoadCloudMfaPending()
    {
        try { return File.Exists(CloudMfaPendingPath) ? JsonSerializer.Deserialize<Dictionary<string, CloudMfaPending>>(File.ReadAllText(CloudMfaPendingPath)) ?? new() : new(); }
        catch { return new(); }
    }
    private static void UpdateCloudMfaPending(string fingerprint, CloudMfaPending? value)
    {
        var all = LoadCloudMfaPending().Where(p => p.Value.Created > DateTime.UtcNow.AddDays(-3)).ToDictionary(p => p.Key, p => p.Value);
        if (value == null) all.Remove(fingerprint); else all[fingerprint] = value;
        Directory.CreateDirectory(CloudMfaFolder);
        var temp = CloudMfaPendingPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(all)); File.Move(temp, CloudMfaPendingPath, true);
    }

    private async Task OpenCloudMfaAlignment(string prepared, string media)
    {
        if (Window == null) return;
        var document = _subtitleFileName; var generation = _ostDocumentGeneration; var hash = GetFastHash();
        var mediaInfo = new FileInfo(media);
        if (!mediaInfo.Exists) throw new InvalidDataException("בחר קובץ שמע או וידאו קיים.");
        var mediaBytes = mediaInfo.Length; var mediaWrite = mediaInfo.LastWriteTimeUtc;
        var cues = CloudMfaImport.CuesFromPrepared(prepared);
        if (cues.Count == 0) throw new InvalidDataException("אין טקסט ליישור.");
        List<CloudMfaImport.Cue>? approved = null;
        var config = LoadCloudMfaConfig();

        await ProjectDialog("יישור תסריט לשמע בענן - Montreal Forced Aligner", (panel, dialog) =>
        {
            var closing = new CancellationTokenSource(); var busy = false; CancellationTokenSource? run = null;
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            CloudMfaClient? client = null; CloudMfaServiceInfo? info = null; CloudMfaStatus? job = null; CloudMfaResult? result = null;
            string? wavPath = null, workFolder = null, audioSha = null, fingerprint = null;
            dialog.Closed += async (_, _) =>
            {
                closing.Cancel();
                // Closing never leaves a job running in the cloud on our behalf.
                // After import the cloud copy is no longer needed either, so it is deleted in both cases.
                if (client != null && job != null)
                {
                    try { using var t = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await client.CancelAsync(job.JobId, t.Token); await client.DeleteAsync(job.JobId, t.Token); if (fingerprint != null) UpdateCloudMfaPending(fingerprint, null); } catch { }
                }
                if (workFolder != null) { try { Directory.Delete(workFolder, true); } catch { } }
                http.Dispose(); closing.Dispose();
            };

            panel.Children.Add(Label("MFA מיישר טקסט קיים לשמע. הוא אינו מתמלל ואינו מתרגם, והתסריט אינו משתנה. אם התסריט שונה מהדיבור, היישור עלול להחזיר זמנים שגויים שנראים סבירים - לכן נדרשת בדיקה לפני ייבוא."));
            panel.Children.Add(Label("עברית: אין כרגע ל־MFA מודל אקוסטי ומילון הגייה רשמיים לעברית. השירות יציג רק שפות שבאמת מותקנות בו."));
            panel.Children.Add(Label($"כתוביות מוכנות לשליחה: {cues.Count} · שמע מתוך: {Path.GetFileName(media)}"));
            var preview = Field(string.Join("\n\n", cues.Take(40).Select(c => c.Id + ". " + c.Text)) + (cues.Count > 40 ? "\n\n…" : ""), true);
            preview.IsReadOnly = true; preview.FlowDirection = FlowDirection.LeftToRight; preview.MaxHeight = 220; AutomationProperties.SetName(preview, "תצוגה מקדימה של הכתוביות שיישלחו");
            panel.Children.Add(preview);

            panel.Children.Add(Label("כתובת השירות (https)"));
            var url = Field(config.ServiceUrl); url.FlowDirection = FlowDirection.LeftToRight; AutomationProperties.SetName(url, "כתובת שירות היישור בענן"); panel.Children.Add(url);
            panel.Children.Add(Label("אסימון גישה - נשמר מוצפן בחשבון Windows (DPAPI), לא ב־Settings.json ולא בחבילות העדכון. השאר ריק כדי להשתמש באסימון השמור."));
            var tokenBox = Field(); tokenBox.PasswordChar = '●'; tokenBox.FlowDirection = FlowDirection.LeftToRight; AutomationProperties.SetName(tokenBox, "אסימון גישה לשירות היישור"); panel.Children.Add(tokenBox);
            var language = new ComboBox { FontSize = 16, MinWidth = 320, IsEnabled = false }; AutomationProperties.SetName(language, "שפת היישור");
            var languages = new List<CloudMfaLanguage>();
            var serviceText = Label("לא נבדק חיבור.");
            var consentText = Label("");
            var consent = new CheckBox { Content = Label("אני מאשר לשלוח את השמע והטקסט האלה לשירות הזה"), IsEnabled = false, FontSize = 16 };
            AutomationProperties.SetName(consent, "אישור שליחת השמע והטקסט לשירות הענן");
            ActionButton(panel, "שמור חיבור ובדוק שפות זמינות", async () =>
            {
                var token = string.IsNullOrWhiteSpace(tokenBox.Text) ? CustomWindowsIntegration.ReadCloudMfaToken() : tokenBox.Text.Trim();
                var probe = new CloudMfaClient(http, url.Text ?? "", token);
                var found = await probe.InfoAsync(closing.Token);
                if (!string.IsNullOrWhiteSpace(tokenBox.Text)) { CustomWindowsIntegration.SaveCloudMfaToken(tokenBox.Text); tokenBox.Text = ""; }
                config.ServiceUrl = CloudMfaClient.ValidateUrl(url.Text ?? "").ToString().TrimEnd('/'); SaveCloudMfaConfig(config);
                client = probe; info = found;
                languages = found.Languages.Where(l => l.Available).ToList();
                language.ItemsSource = languages.Select(l => l.Label).ToList();
                language.SelectedIndex = Math.Max(0, languages.FindIndex(l => l.Id == config.Language));
                language.IsEnabled = languages.Count > 0;
                var missing = found.Languages.Where(l => !l.Available).Select(l => l.Label + ": " + l.Reason);
                serviceText.Text = $"שירות: {found.ServiceName} · מחיקה אוטומטית אחרי {found.RetentionHours} שעות לכל היותר · עד {found.MaxAudioSeconds / 60} דקות שמע." +
                                   (languages.Count == 0 ? "\nאין בשירות שפה זמינה ליישור." : "") + (missing.Any() ? "\nלא זמין: " + string.Join("\n", missing) : "");
                consentText.Text = ConsentText(); consent.IsEnabled = languages.Count > 0;
            });
            panel.Children.Add(serviceText);
            panel.Children.Add(Label("שפת התסריט והשמע")); panel.Children.Add(language);

            string ConsentText() => info == null ? "" :
                $"בלחיצה על שליחה יישלחו אל {CloudMfaClient.ValidateUrl(config.ServiceUrl).Host} ({info.ServiceName}): פס קול בלבד - WAV מונו 16kHz שיחולץ כעת מהקובץ במחשב שלך (לא הווידאו), והטקסט של {cues.Count} הכתוביות. " +
                $"השמע והטקסט נמחקים מהשירות בסיום העבודה, והתוצאה נמחקת בייבוא, בסגירת החלון או לכל המאוחר אחרי {info.RetentionHours} שעות. השירות בחשבון הענן שלך ועשוי לגרום לחיוב לפי השימוש.";
            panel.Children.Add(consentText); panel.Children.Add(consent);

            var progressText = Label(""); var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 18, IsVisible = false };
            AutomationProperties.SetName(progress, "התקדמות היישור");
            var resultBox = Field("", true); resultBox.IsReadOnly = true; resultBox.IsVisible = false; resultBox.MaxHeight = 320; AutomationProperties.SetName(resultBox, "תוצאת היישור לבדיקה");
            var reviewed = new CheckBox { Content = Label("האזנתי ובדקתי את התזמון, כולל השורות המסומנות"), IsVisible = false, FontSize = 16 };
            AutomationProperties.SetName(reviewed, "אישור שבדקתי את תוצאת היישור");

            ActionButton(panel, "שלח ליישור בענן", async () =>
            {
                if (busy) return;
                if (client == null || info == null) throw new InvalidOperationException("בדוק תחילה חיבור ושפות זמינות.");
                if (consent.IsChecked != true) throw new InvalidOperationException("סמן אישור שליחה לפני השליחה.");
                if (language.SelectedIndex < 0 || language.SelectedIndex >= languages.Count) throw new InvalidOperationException("בחר שפה זמינה.");
                var lang = languages[language.SelectedIndex]; config.Language = lang.Id; SaveCloudMfaConfig(config);
                if (!File.Exists(media) || new FileInfo(media).Length != mediaBytes || new FileInfo(media).LastWriteTimeUtc != mediaWrite) throw new InvalidOperationException("קובץ המדיה השתנה. פתח שוב את כלי התסריט.");
                busy = true; consent.IsEnabled = false; run = CancellationTokenSource.CreateLinkedTokenSource(closing.Token); var token = run.Token; result = null; resultBox.IsVisible = false; reviewed.IsVisible = false; reviewed.IsChecked = false;
                progress.IsVisible = true; progress.IsIndeterminate = true;
                try
                {
                    if (wavPath == null)
                    {
                        progressText.Text = "מחלץ פס קול מקומית (FFmpeg)…";
                        workFolder = Path.Combine(Path.GetTempPath(), "se-cloud-mfa-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workFolder);
                        wavPath = Path.Combine(workFolder, "audio.wav");
                        await ExtractCloudMfaAudio(media, wavPath, token);
                        audioSha = await Task.Run(() => CloudMfaClient.Sha256File(wavPath), token);
                    }
                    var seconds = (new FileInfo(wavPath).Length - 44) / 32000.0;
                    if (seconds > info.MaxAudioSeconds) throw new InvalidDataException($"השמע ארוך מהמותר בשירות ({info.MaxAudioSeconds / 60} דקות). פצל את העבודה לחלקים.");
                    fingerprint = CloudMfaImport.Fingerprint(config.ServiceUrl, lang.Id, cues, media, mediaBytes, mediaWrite);
                    var pending = LoadCloudMfaPending().GetValueOrDefault(fingerprint);
                    var requestId = Guid.TryParse(pending?.RequestId, out var saved) ? saved : Guid.NewGuid();
                    UpdateCloudMfaPending(fingerprint, new CloudMfaPending { RequestId = requestId.ToString(), Created = DateTime.UtcNow });
                    progressText.Text = pending != null ? "מתחבר מחדש למשימה קיימת…" : "יוצר משימה בשירות…";
                    try { job = await client.CreateAsync(requestId, lang.Id, cues, audioSha!, new FileInfo(wavPath).Length, token); }
                    catch (CloudMfaException e) when (e.Code == "request_id_reused")
                    {
                        requestId = Guid.NewGuid(); UpdateCloudMfaPending(fingerprint, new CloudMfaPending { RequestId = requestId.ToString(), Created = DateTime.UtcNow });
                        job = await client.CreateAsync(requestId, lang.Id, cues, audioSha!, new FileInfo(wavPath).Length, token);
                    }
                    UpdateCloudMfaPending(fingerprint, new CloudMfaPending { RequestId = requestId.ToString(), JobId = job.JobId, Created = DateTime.UtcNow });
                    if (job.Status == "awaiting_upload")
                    {
                        progress.IsIndeterminate = false;
                        job = await client.UploadAsync(job, wavPath, info.MaxChunkBytes, new Progress<double>(p => { progress.Value = p; progressText.Text = $"מעלה פס קול: {p:P0}"; }), token);
                    }
                    progress.IsIndeterminate = true;
                    var final = await client.WaitAsync(job.JobId, s => progressText.Text = StageText(s), token);
                    if (final.Status == "cancelled") { progressText.Text = "היישור בוטל. לא שונו כתוביות."; UpdateCloudMfaPending(fingerprint, null); return; }
                    if (final.Status != "succeeded") { progressText.Text = "היישור נכשל: " + final.ErrorMessage + "\nלא שונו כתוביות. אם התסריט שונה מהדיבור, השתמש בהשוואה לתמלול."; UpdateCloudMfaPending(fingerprint, null); return; }
                    result = await client.ResultAsync(job.JobId, token);
                    var lines = new List<string> { StatusText(result), result.SummaryText, result.Engine + " · " + result.Method, "" };
                    for (var i = 0; i < Math.Min(cues.Count, result.Cues.Count); i++) lines.Add(CloudMfaImport.Describe(result.Cues[i], cues[i].Text));
                    resultBox.Text = string.Join("\n", lines); resultBox.IsVisible = true;
                    reviewed.IsVisible = result.Status is "complete" or "needs_review";
                    progressText.Text = "התקבלה תוצאה לבדיקה. המסמך הפתוח לא שונה.";
                }
                catch (OperationCanceledException) when (!closing.IsCancellationRequested) { progressText.Text = "הפעולה בוטלה. לא שונו כתוביות."; }
                finally { busy = false; consent.IsEnabled = info != null; progress.IsVisible = false; run?.Dispose(); run = null; }
            });
            ActionButton(panel, "בטל יישור", async () =>
            {
                run?.Cancel();
                if (client == null || job == null) return;
                var s = await client.CancelAsync(job.JobId, CancellationToken.None);
                progressText.Text = s.Status == "cancelled" ? "בוטל. לא שונו כתוביות." : "נשלחה בקשת ביטול…";
            });
            panel.Children.Add(progress); panel.Children.Add(progressText); panel.Children.Add(resultBox); panel.Children.Add(reviewed);
            ActionButton(panel, "התסריט שונה מהדיבור? פתח השוואה לתמלול…", async () => { dialog.Close(); await OpenScriptTranscriptReview(prepared, media); });
            ActionButton(panel, "בדקתי - ייבא למסמך חדש", () =>
            {
                if (result == null || job == null || audioSha == null) throw new InvalidOperationException("אין תוצאה תקינה לייבוא.");
                if (reviewed.IsChecked != true) throw new InvalidOperationException("סמן שבדקת את התוצאה לפני ייבוא.");
                if (generation != _ostDocumentGeneration || document != _subtitleFileName || hash != GetFastHash()) throw new InvalidOperationException("המסמך השתנה מאז ההכנה. פתח שוב את כלי התסריט.");
                if (!File.Exists(media) || new FileInfo(media).Length != mediaBytes || new FileInfo(media).LastWriteTimeUtc != mediaWrite) throw new InvalidOperationException("קובץ המדיה השתנה. התוצאה אינה תקפה.");
                approved = CloudMfaImport.Validate(result, cues, audioSha, job.CuesSha256, reviewed.IsChecked == true);
                dialog.Close();
                return Task.CompletedTask;
            });

            string StageText(CloudMfaStatus s) => s.Status switch
            {
                "reconnecting" => "החיבור נותק - מנסה שוב על אותה משימה…",
                "queued" => "ממתין בתור בשירות…",
                "running" => s.Stage switch
                {
                    "aligning_pass1" => "MFA: יישור ראשון של כל הטקסט",
                    "segmenting" => "MFA: זיהוי קטעי דיבור",
                    "aligning_pass2" => "MFA: יישור חוזר בתוך קטעי הדיבור",
                    "finishing" => "מכין תוצאה…",
                    _ => "MFA: מכין את הקורפוס…",
                } + (s.StageIndex is int i && s.StageCount > 0 ? $" (שלב {i} מתוך {s.StageCount})" : ""),
                _ => s.Status,
            };
            static string StatusText(CloudMfaResult r) => r.Status switch
            {
                "complete" => "כל הכתוביות יושרו. זה אינו אישור שהזמנים נכונים - האזן לבדיקה.",
                "needs_review" => "כל המילים יושרו, אבל חלק מהכתוביות סומנו לבדיקה. ייתכן שהתסריט שונה מהדיבור.",
                _ => "היישור חלקי: יש מילים או כתוביות שלא יושרו. ייבוא אינו אפשרי; השתמש בהשוואה לתמלול.",
            };
        });

        if (approved == null || Window == null) return;
        if (generation != _ostDocumentGeneration || document != _subtitleFileName || hash != GetFastHash()) { ShowStatus("המסמך השתנה. התוצאה לא יובאה.", 7000); return; }
        if (!await HasChangesContinue()) return;
        _subtitleFileName = string.Empty;
        ResetSubtitle();
        foreach (var cue in approved)
        {
            // Hebrew cues use the existing Hebrew line-break rules; other languages keep the approved preview breaks.
            var text = Regex.IsMatch(cue.Text, @"\p{IsHebrew}") ? Utilities.AutoBreakLine(cue.Text, Se.Settings.General.SubtitleLineMaximumLength, 0, "he") : cue.Text;
            Subtitles.Add(new SubtitleLineViewModel { Text = text, StartTime = TimeSpan.FromSeconds(cue.Start), EndTime = TimeSpan.FromSeconds(cue.End) });
        }
        Renumber(); _updateAudioVisualizer = true;
        await AutoOpenVideoFile(media);
        ShowStatus("היישור מ־MFA יובא למסמך חדש. בדוק מול השמע ושמור בשם חדש.", 9000);
    }

    private static async Task ExtractCloudMfaAudio(string media, string wav, CancellationToken token)
    {
        var ffmpeg = File.Exists(Se.Settings.General.FfmpegPath) ? Se.Settings.General.FfmpegPath : "ffmpeg";
        var info = new ProcessStartInfo(ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        // Argument list: file names are never interpreted by a shell.
        foreach (var a in new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", media, "-vn", "-map", "0:a:0", "-af", "aresample=async=1:first_pts=0", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", wav })
            info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("לא ניתן להפעיל FFmpeg.");
        var err = process.StandardError.ReadToEndAsync(); var outp = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
        await Task.WhenAll(err, outp);
        if (process.ExitCode != 0 || !File.Exists(wav) || new FileInfo(wav).Length <= 44) throw new InvalidDataException("חילוץ פס הקול נכשל. ודא שבקובץ יש ערוץ שמע.");
    }
}

