using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private async Task OpenLocalMfaAlignment(string prepared, string media)
    {
        if (Window == null) return;
        var document = _subtitleFileName; var generation = _ostDocumentGeneration; var hash = GetFastHash();
        var mediaInfo = new FileInfo(media);
        if (!mediaInfo.Exists) throw new InvalidDataException("בחר קובץ מדיה קיים.");
        var bytes = mediaInfo.Length; var write = mediaInfo.LastWriteTimeUtc;
        bool Fresh() => generation == _ostDocumentGeneration && document == _subtitleFileName && hash == GetFastHash()
            && File.Exists(media) && new FileInfo(media).Length == bytes && new FileInfo(media).LastWriteTimeUtc == write;
        var cues = CloudMfaImport.CuesFromPrepared(prepared);
        var requestText = LocalMfa.Request(cues);
        var data = Path.Combine(Se.DataFolder, "LocalMfa");
        var cache = Path.Combine(data, "models");
        var config = Path.Combine(data, "python-path.txt");
        string configured;
        try { configured = File.Exists(config) ? File.ReadAllText(config) : ""; } catch { configured = ""; }
        List<CloudMfaImport.Cue>? approved = null;
        using var closing = new CancellationTokenSource();
        Task pending = Task.CompletedTask;
        await ProjectDialog("תסריט באנגלית לכתוביות באנגלית — MFA מקומי", (panel, dialog) =>
        {
            var closed = false; var busy = false; CancellationTokenSource? operation = null;
            List<CloudMfaImport.Cue>? result = null;
            dialog.Closed += (_, _) => { closed = true; closing.Cancel(); };
            panel.Children.Add(Label("התסריט ופס הקול מעובדים במחשב הזה בלבד. אין שליחה לשירות ענן. נדרשים תסריט ושמע תואמים באנגלית; זמנים שנראים תקינים עדיין דורשים בדיקה באוזן."));
            panel.Children.Add(Label("בהתקנה הראשונה: התקן Miniforge מהאתר הרשמי https://conda-forge.org/download/ ופתח Miniforge Prompt. הרץ את הפקודה שלמטה, ואז בחר כאן את python.exe מתוך סביבת subtitleedit-mfa שנוצרה. המנוע והמודלים אינם נכללים בעדכוני התוכנה."));
            var command = Field("conda create -n subtitleedit-mfa -c conda-forge python=3.11 montreal-forced-aligner=3.4.3 -y");
            command.IsReadOnly = true; command.FlowDirection = FlowDirection.LeftToRight; panel.Children.Add(command);
            var python = Field(configured); python.FlowDirection = FlowDirection.LeftToRight;
            AutomationProperties.SetName(python, "python.exe מתוך סביבת MFA המקומית"); panel.Children.Add(python);
            ActionButton(panel, "בחר python.exe של סביבת MFA…", async () =>
            {
                if (busy) return;
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "בחר python.exe מתוך סביבת subtitleedit-mfa", AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Python של MFA") { Patterns = new[] { "python.exe" } } } });
                if (!closed && files.FirstOrDefault()?.TryGetLocalPath() is string path) python.Text = path;
            });
            var status = Label("טרם הופעל יישור."); panel.Children.Add(status);
            var preview = Field("", true); preview.IsReadOnly = true; preview.IsVisible = false;
            preview.FlowDirection = FlowDirection.LeftToRight; preview.MaxHeight = 320;
            AutomationProperties.SetName(preview, "תזמוני MFA מקומי לבדיקה");
            var reviewed = new CheckBox { Content = Label("בדקתי את הנוסח והתזמונים מול השמע ואני מאשר ייבוא למסמך חדש"), IsVisible = false };
            python.TextChanged += (_, _) => { result = null; reviewed.IsChecked = false; reviewed.IsVisible = false; preview.IsVisible = false; };
            async Task Perform(Func<string, CancellationToken, Task> action)
            {
                if (busy) return;
                busy = true; python.IsEnabled = false; result = null; reviewed.IsChecked = false; reviewed.IsVisible = false; preview.IsVisible = false;
                operation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
                var path = python.Text?.Trim() ?? "";
                try
                {
                    LocalMfa.PythonCommand(path, cache, "--check"); // validate path before persisting it
                    Directory.CreateDirectory(data); File.WriteAllText(config, path);
                    await LocalMfa.Exclusive(() => action(path, operation.Token), operation.Token);
                }
                catch (OperationCanceledException) { if (!closed) status.Text = "בוטל. המסמך לא השתנה."; }
                catch (Exception e) { if (!closed) status.Text = e.Message; }
                finally { operation.Dispose(); operation = null; busy = false; if (!closed) python.IsEnabled = true; }
            }
            var progress = new Progress<string>(stage =>
            {
                if (closed) return;
                status.Text = stage switch { "preparing" => "מכין תסריט ושמע מקומיים…", "aligning_pass1" => "MFA: יישור ראשון…",
                    "segmenting" => "מזהה קטעי דיבור…", "aligning_pass2" => "MFA: יישור חוזר במקטעי הדיבור…",
                    "downloading_dictionary" => "מוריד מילון הגייה לאנגלית…", "downloading_acoustic" => "מוריד מודל אקוסטי לאנגלית…",
                    _ => status.Text };
            });
            ActionButton(panel, "בדוק התקנת MFA מקומית", async () =>
            {
                if (busy) return;
                pending = Perform(async (path, token) => { status.Text = "בודק מנוע מקומי…"; await LocalMfa.Run(LocalMfa.PythonCommand(path, cache, "--check"), TimeSpan.FromMinutes(2), progress, token); if (!closed) status.Text = "MFA 3.4.3 זמין מקומית."; }); await pending;
            });
            panel.Children.Add(Label("הכפתור הבא מוריד רק מודל אקוסטי ומילון הגייה רשמיים לאנגלית באמצעות MFA. שום תסריט או שמע אינם נשלחים. ההורדה דורשת חיבור לאינטרנט ונפח דיסק."));
            ActionButton(panel, "הורד מודלים לאנגלית — ללא שליחת תסריט או שמע", async () =>
            {
                if (busy) return;
                pending = Perform(async (path, token) => { await LocalMfa.Run(LocalMfa.PythonCommand(path, cache, "--download-models"), TimeSpan.FromMinutes(60), progress, token); if (!closed) status.Text = "המודלים לאנגלית הורדו."; }); await pending;
            });
            ActionButton(panel, "הפעל יישור MFA מקומי", async () =>
            {
                if (busy) return;
                pending = Perform(async (path, token) =>
                {
                    if (!Fresh()) throw new InvalidOperationException("המסמך או המדיה השתנו. פתח שוב את כלי התסריט.");
                    var work = Path.Combine(Path.GetTempPath(), "SubtitleEdit-LocalMfa-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(work);
                    try
                    {
                        var req = Path.Combine(work, "request.json"); var wav = Path.Combine(work, "audio.wav");
                        await File.WriteAllTextAsync(req, requestText, token);
                        var ffmpeg = File.Exists(Se.Settings.General.FfmpegPath) ? Se.Settings.General.FfmpegPath : "ffmpeg";
                        var extract = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", media, "-t", "7201", "-vn", "-map", "0:a:0", "-af", "aresample=async=1:first_pts=0", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav }) extract.ArgumentList.Add(a);
                        status.Text = "מחלץ פס קול מקומי (עד שעתיים)…";
                        await LocalMfa.Run(extract, TimeSpan.FromMinutes(10), null, token);
                        var audioHash = await Task.Run(() => LocalMfa.Hash(wav), token);
                        var requestHash = await Task.Run(() => LocalMfa.Hash(req), token);
                        await LocalMfa.Run(LocalMfa.PythonCommand(path, cache, "--request", req, "--audio", wav, "--work", work), TimeSpan.FromMinutes(125), progress, token);
                        var aligned = await Task.Run(() => LocalMfa.ReadResult(Path.Combine(work, "result.json"), cues, audioHash, requestHash), token);
                        token.ThrowIfCancellationRequested();
                        if (closed || !Fresh()) throw new InvalidOperationException("המסמך או המדיה השתנו. התוצאה לא יובאה.");
                        result = aligned.Cues;
                        preview.Text = aligned.ReviewText;
                        preview.IsVisible = reviewed.IsVisible = true;
                        status.Text = "היישור המקומי הושלם. האזן ובדוק לפני אישור; המסמך טרם השתנה.";
                    }
                    finally { try { Directory.Delete(work, true); } catch { } }
                }); await pending;
            });
            ActionButton(panel, "בטל יישור / הורדה", () => { operation?.Cancel(); return Task.CompletedTask; });
            panel.Children.Add(preview); panel.Children.Add(reviewed);
            ActionButton(panel, "בדקתי — ייבא למסמך חדש", () =>
            {
                if (busy || result == null || reviewed.IsChecked != true) throw new InvalidOperationException("השלם יישור וסמן שבדקת את התוצאה.");
                if (!Fresh()) throw new InvalidOperationException("המסמך או המדיה השתנו. פתח מחדש את הכלי.");
                approved = result; dialog.Close(); return Task.CompletedTask;
            });
        });
        closing.Cancel(); await pending;
        if (approved == null || Window == null || !Fresh()) return;
        if (!await HasChangesContinue() || !Fresh()) return;
        _subtitleFileName = string.Empty; ResetSubtitle();
        foreach (var c in approved) Subtitles.Add(new SubtitleLineViewModel { Text = c.Text, StartTime = TimeSpan.FromSeconds(c.Start), EndTime = TimeSpan.FromSeconds(c.End) });
        Renumber(); _updateAudioVisualizer = true; await AutoOpenVideoFile(media);
        ShowStatus("MFA מקומי: יובא למסמך חדש. בדוק מול השמע ושמור כ־SRT.", 9000);
    }
}
