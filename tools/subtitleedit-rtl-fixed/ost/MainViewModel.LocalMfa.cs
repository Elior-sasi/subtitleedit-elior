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
        configured = LocalMfa.FindPython(configured, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        List<CloudMfaImport.Cue>? approved = null;
        using var closing = new CancellationTokenSource();
        Task pending = Task.CompletedTask;
        await ProjectDialog("תסריט באנגלית לכתוביות באנגלית — MFA מקומי", (panel, dialog) =>
        {
            var closed = false; var busy = false; CancellationTokenSource? operation = null;
            List<CloudMfaImport.Cue>? result = null;
            var display = new LocalMfa.ProgressDisplay();
            dialog.Closed += (_, _) => { closed = true; closing.Cancel(); display.Stop(); };
            panel.Children.Add(Label("תזמון תסריט ושמע תואמים באנגלית, במחשב הזה בלבד. בסיום תוכל לבדוק את התוצאה לפני ייבוא למסמך חדש."));
            var environment = Label(""); panel.Children.Add(environment);
            var advancedPanel = new StackPanel();
            var advanced = new Expander { Name = "LocalMfaAdvanced", Header = Label("מתקדם — התקנה ומיקום המנוע"), Content = advancedPanel,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            advancedPanel.Children.Add(Label("אם MFA עדיין לא מותקן: התקן Miniforge מהאתר הרשמי https://conda-forge.org/download/ והריץ ב־Miniforge Prompt את הפקודה הבאה. לאחר מכן פתח שוב את החלון לזיהוי אוטומטי, או בחר את הסביבה כאן."));
            var command = Field("conda create -n subtitleedit-mfa -c conda-forge python=3.11 montreal-forced-aligner=3.4.3 -y");
            command.IsReadOnly = true; command.FlowDirection = FlowDirection.LeftToRight; advancedPanel.Children.Add(command);
            var python = Field(configured); python.FlowDirection = FlowDirection.LeftToRight;
            AutomationProperties.SetName(python, "python.exe מתוך סביבת MFA המקומית"); advancedPanel.Children.Add(python);
            var actions = new List<Button>();
            void AddAction(StackPanel target, string label, Func<Task> action)
            {
                ActionButton(target, label, action); actions.Add((Button)target.Children.Last());
            }
            AddAction(advancedPanel, "בחר מיקום התקנת MFA…", async () =>
            {
                if (busy) return;
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "בחר python.exe מתוך סביבת subtitleedit-mfa", AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Python של MFA") { Patterns = new[] { "python.exe" } } } });
                if (!closed && files.FirstOrDefault()?.TryGetLocalPath() is string path) python.Text = path;
            });
            panel.Children.Add(display);
            var setup = new StackPanel();
            var preview = Field("", true); preview.IsReadOnly = true; preview.IsVisible = false;
            preview.FlowDirection = FlowDirection.LeftToRight; preview.MaxHeight = 320;
            AutomationProperties.SetName(preview, "תזמוני MFA מקומי לבדיקה");
            var reviewed = new CheckBox { Content = Label("בדקתי את הנוסח והתזמונים מול השמע ואני מאשר ייבוא למסמך חדש"), IsVisible = false };
            Button? importButton = null;
            void RefreshActions()
            {
                foreach (var button in actions) button.IsEnabled = !busy;
                python.IsEnabled = !busy;
                if (importButton != null) importButton.IsEnabled = !busy && result != null && reviewed.IsChecked == true;
                var found = !string.IsNullOrWhiteSpace(python.Text) && File.Exists(python.Text.Trim());
                setup.IsVisible = !LocalMfa.ModelsReady(cache);
                environment.Text = !found ? "לא נמצאה התקנת MFA. פתח את מתקדם להגדרה ראשונית." :
                    LocalMfa.ModelsReady(cache) ? "נמצאה סביבה ומודלים מקומיים. המנוע ייבדק בתחילת התזמון." : "נמצאה סביבה מקומית. נדרשת הורדת מודלים ראשונית.";
            }
            python.TextChanged += (_, _) => { result = null; reviewed.IsChecked = false; reviewed.IsVisible = false; preview.IsVisible = false; RefreshActions(); };
            reviewed.PropertyChanged += (_, e) => { if (e.Property.Name == "IsChecked") RefreshActions(); };
            IProgress<string>? progress = null;
            int operationGeneration = 0;
            async Task Perform(Func<string, CancellationToken, Task> action)
            {
                if (busy) return;
                busy = true; python.IsEnabled = false; result = null; reviewed.IsChecked = false; reviewed.IsVisible = false; preview.IsVisible = false;
                var currentOperation = ++operationGeneration;
                progress = new Progress<string>(message => { if (!closed && busy && currentOperation == operationGeneration) display.Apply(message); });
                display.Begin("מכין את פעולת התזמון…"); RefreshActions();
                operation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
                var path = python.Text?.Trim() ?? "";
                try
                {
                    LocalMfa.PythonCommand(path, cache, "--check"); // validate path before persisting it
                    await LocalMfa.Exclusive(async () =>
                    {
                        display.ApplyStage("בודק את המנוע המקומי…");
                        await LocalMfa.Run(LocalMfa.PythonCommand(path, cache, "--check"), TimeSpan.FromMinutes(2), progress, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(data); File.WriteAllText(config, path);
                        await action(path, operation.Token);
                    }, operation.Token);
                }
                catch (OperationCanceledException) { if (!closed) display.Finish("בוטל. המסמך לא השתנה."); }
                catch (Exception e) { if (!closed) { display.Finish(e.Message); advanced.IsExpanded = string.IsNullOrWhiteSpace(path) || !File.Exists(path); } }
                finally { operation.Dispose(); operation = null; busy = false; display.Stop(); if (!closed) RefreshActions(); }
            }
            AddAction(advancedPanel, "בדוק התקנת MFA", async () =>
            {
                if (busy) return;
                pending = Perform((path, token) => { if (!closed) display.Finish("MFA 3.4.3 זמין מקומית."); return Task.CompletedTask; }); await pending;
            });
            setup.Children.Add(Label("הכנה ראשונית: הורדת מודל ומילון לאנגלית. התסריט והשמע אינם נשלחים; נדרש חיבור לאינטרנט."));
            AddAction(setup, "הורד את המודלים הנדרשים", async () =>
            {
                if (busy) return;
                pending = Perform(async (path, token) => { await LocalMfa.Run(LocalMfa.PythonCommand(path, cache, "--download-models"), TimeSpan.FromMinutes(60), progress, token); if (!closed) display.Finish("המודלים מוכנים. אפשר לתזמן את התסריט."); }); await pending;
            });
            panel.Children.Add(setup);
            AddAction(panel, "תזמן את התסריט", async () =>
            {
                if (busy) return;
                pending = Perform(async (path, token) =>
                {
                    if (!Fresh()) throw new InvalidOperationException("המסמך או המדיה השתנו. פתח שוב את כלי התסריט.");
                    if (!LocalMfa.ModelsReady(cache)) throw new InvalidOperationException("הורד תחילה את המודלים באמצעות כפתור ההכנה הראשונית.");
                    var work = Path.Combine(Path.GetTempPath(), "SubtitleEdit-LocalMfa-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(work);
                    try
                    {
                        var req = Path.Combine(work, "request.json"); var wav = Path.Combine(work, "audio.wav");
                        await File.WriteAllTextAsync(req, requestText, token);
                        var ffmpeg = File.Exists(Se.Settings.General.FfmpegPath) ? Se.Settings.General.FfmpegPath : "ffmpeg";
                        var extract = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", media, "-t", "7201", "-vn", "-map", "0:a:0", "-af", "aresample=async=1:first_pts=0", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav }) extract.ArgumentList.Add(a);
                        display.ApplyStage("מכין את פס הקול לתזמון…");
                        await LocalMfa.Run(extract, TimeSpan.FromMinutes(10), null, token);
                        display.ApplyStage("בודק את קובצי השמע והתסריט…");
                        var audioHash = await Task.Run(() => LocalMfa.Hash(wav), token);
                        var requestHash = await Task.Run(() => LocalMfa.Hash(req), token);
                        await LocalMfa.Run(LocalMfa.PythonCommand(path, cache, "--request", req, "--audio", wav, "--work", work), TimeSpan.FromMinutes(125), progress, token);
                        display.ApplyStage("בודק את תוצאת התזמון…");
                        var aligned = await Task.Run(() => LocalMfa.ReadResult(Path.Combine(work, "result.json"), cues, audioHash, requestHash), token);
                        token.ThrowIfCancellationRequested();
                        if (closed || !Fresh()) throw new InvalidOperationException("המסמך או המדיה השתנו. התוצאה לא יובאה.");
                        result = aligned.Cues;
                        preview.Text = aligned.ReviewText;
                        preview.IsVisible = reviewed.IsVisible = true;
                        display.Finish("התזמון הושלם. בדוק מול השמע ואשר ייבוא למסמך חדש.", true);
                    }
                    finally { try { Directory.Delete(work, true); } catch { } }
                }); await pending;
            });
            ActionButton(panel, "בטל יישור / הורדה", () => { operation?.Cancel(); return Task.CompletedTask; });
            panel.Children.Add(preview); panel.Children.Add(reviewed);
            AddAction(panel, "בדקתי — ייבא למסמך חדש", () =>
            {
                if (busy || result == null || reviewed.IsChecked != true) throw new InvalidOperationException("השלם יישור וסמן שבדקת את התוצאה.");
                if (!Fresh()) throw new InvalidOperationException("המסמך או המדיה השתנו. פתח מחדש את הכלי.");
                approved = result; dialog.Close(); return Task.CompletedTask;
            });
            importButton = actions.Last();
            panel.Children.Add(advanced);
            RefreshActions();
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

