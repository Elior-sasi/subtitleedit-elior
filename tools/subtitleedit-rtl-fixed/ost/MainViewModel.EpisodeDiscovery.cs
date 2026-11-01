using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    internal sealed class EpisodeDiscoveryUiState { internal bool Busy; }
    private EpisodeDiscoveryUiState AttachEpisodeDiscoveryUi(StackPanel panel, Window window, ProjectProfile profile,
        Func<List<EpisodeContextDocument>> getDraft, Action<List<EpisodeContextDocument>> setDraft, Func<bool> fresh, CancellationToken lifetime)
    {
        var state = new EpisodeDiscoveryUiState();
        var preferences = Path.Combine(Se.DataFolder, "CustomProjects", "episode-discovery-folders.json");
        var folder = EpisodeContextDiscovery.LoadFolder(preferences, profile.Id);
        var folderLabel = Label(folder.Length == 0 ? "טרם נבחרה תיקיית סדרה." : "תיקיית הסדרה: " + folder);
        var status = Label(""); status.Name = "EpisodeDiscoveryStatus";
        var candidatesPanel = new StackPanel { Spacing = 6, Name = "EpisodeDiscoveryCandidates" };
        var candidates = new List<(EpisodeCandidate Candidate, CheckBox Check)>();
        CancellationTokenSource? operation = null;
        panel.Children.Add(Label("איתור מקומי לפי שמות S02E07 או 2x07. נסרקות עד שלוש רמות משנה, ללא מעקב אחר קישורים. הפרק הנוכחי, פרקים עתידיים ושמות לא ברורים אינם מוצעים. המועמדים אינם מסומנים מראש; שום קובץ אינו מצורף או נשלח ל־Gemini בלי בחירה ושמירה."));
        panel.Children.Add(folderLabel); panel.Children.Add(status); panel.Children.Add(candidatesPanel);
        void RequireFresh()
        {
            lifetime.ThrowIfCancellationRequested();
            if (!fresh()) throw new InvalidOperationException("המסמך, הפרויקט או פרקי הרקע השתנו. פתח את החלון מחדש; המועמדים לא צורפו.");
        }
        async Task Run(Func<CancellationToken, Task> action)
        {
            if (state.Busy) throw new InvalidOperationException("פעולה קודמת עדיין מתבצעת. המתן או לחץ על ביטול הסריקה.");
            RequireFresh();
            using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            operation = source; state.Busy = true; candidatesPanel.IsEnabled = false;
            try { await action(source.Token); }
            catch (OperationCanceledException) { status.Text = "האיתור בוטל. פרקי הרקע והכתוביות לא שונו."; }
            finally { operation = null; state.Busy = false; candidatesPanel.IsEnabled = true; }
        }
        async Task Scan()
        {
            await Run(async token =>
            {
                candidates.Clear(); candidatesPanel.Children.Clear(); status.Text = "סורק שמות קבצים בתיקיית הסדרה…";
                var selectedFolder = folder; var current = _subtitleFileName!;
                var result = await Task.Run(() => EpisodeContextDiscovery.Scan(selectedFolder, current, token), token);
                token.ThrowIfCancellationRequested(); RequireFresh();
                foreach (var candidate in result.Candidates)
                {
                    var text = $"S{candidate.Season:00}E{candidate.Episode:00} · {candidate.Name}" + (candidate.MultipleVersions ? " — כמה גרסאות לפרק; בדוק לפני בחירה" : "");
                    var check = new CheckBox { Content = Label(text), IsChecked = false, Name = "EpisodeDiscoveryCandidate" };
                    AutomationProperties.SetName(check, text); candidates.Add((candidate, check)); candidatesPanel.Children.Add(check);
                    ActionButton(candidatesPanel, "עיין לפני צירוף: " + candidate.Name, async () =>
                    {
                        await Run(async previewToken =>
                        {
                            var doc = await Task.Run(() => EpisodeContextDiscovery.ReadVerified(candidate, previewToken), previewToken); RequireFresh();
                            await ProjectDialog("תצוגת מועמד — " + candidate.Name, (p, _) => { var field = Field(doc.Text, true); field.IsReadOnly = true; field.Height = 400; p.Children.Add(field); });
                        });
                    });
                }
                status.Text = $"הפרק הנוכחי S{result.Current.Season:00}E{result.Current.Episode:00}: נמצאו {result.Candidates.Count} מועמדים קודמים. כולם ממתינים לבחירתך." + (result.SkippedLinks > 0 ? $" דולגו {result.SkippedLinks} קישורים." : "");
            });
        }
        ActionButton(panel, "בחר תיקיית סדרה וזכור אותה בפרויקט…", async () =>
        {
            if (state.Busy) throw new InvalidOperationException("המתן לסיום הפעולה או בטל אותה תחילה.");
            var picked = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "בחר תיקיית כתוביות של הסדרה", AllowMultiple = false });
            var path = picked.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
            RequireFresh(); EpisodeContextDiscovery.SaveFolder(preferences, profile.Id, path); folder = path;
            folderLabel.Text = "תיקיית הסדרה: " + folder; await Scan();
        });
        ActionButton(panel, "אתר פרקים קודמים בתיקייה השמורה", Scan);
        ActionButton(panel, "בטל סריקה או קריאת מועמדים", () => { operation?.Cancel(); return Task.CompletedTask; });
        ActionButton(panel, "צרף לטיוטת הרקע רק את המועמדים שסימנתי", async () =>
        {
            var selected = candidates.Where(c => c.Check.IsChecked == true).Select(c => c.Candidate).ToArray();
            await Run(async token =>
            {
                var documents = await Task.Run(() => EpisodeContextDiscovery.ImportApproved(selected, token), token);
                RequireFresh(); var next = ProjectEpisodeContext.Merge(getDraft(), documents); setDraft(next);
                foreach (var (_, check) in candidates) check.IsChecked = false;
                status.Text = "המועמדים שנבחרו צורפו לטיוטה בלבד. כדי להשתמש בהם בהקשר Gemini לחץ על שמור פרקי רקע בפרויקט. לא נשלחה בקשה לענן.";
            });
        });
        // Opening this user-requested dialog may rescan the remembered folder locally.
        // It never imports candidates or makes cloud calls on its own.
        if (folder.Length > 0) window.Opened += async (_, _) =>
        {
            try { await Scan(); }
            catch (Exception e) when (e is not OperationCanceledException) { status.Text = e.Message; }
        };
        return state;
    }
}
