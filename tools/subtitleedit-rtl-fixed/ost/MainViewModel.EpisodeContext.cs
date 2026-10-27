using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private int _episodeContextRevision;
    internal void SaveEpisodeContext(ProjectProfile profile, List<EpisodeContextDocument> documents)
    {
        if (ActiveProfile?.Id != profile.Id || !_customProfiles.Contains(profile)) throw new InvalidOperationException("הפרויקט הפעיל השתנה.");
        var validated = ProjectEpisodeContext.Merge(Array.Empty<EpisodeContextDocument>(), documents);
        var previous = profile.PreviousEpisodes;
        profile.PreviousEpisodes = validated;
        try { SaveCustomProfiles(); }
        catch { profile.PreviousEpisodes = previous; throw; }
        _episodeContextRevision++;
        _customQcDirty = true;
    }

    private void RequireEpisodeContext(ProjectProfile expected)
    {
        if (ActiveProfile?.Id != expected.Id || ProjectEpisodeContext.Fingerprint(ActiveProfile) != ProjectEpisodeContext.Fingerprint(expected))
            throw new InvalidOperationException("פרקי הרקע או הפרויקט השתנו. פתח את הפעולה מחדש; תוצאה ישנה לא תוחל.");
    }

    private async Task OpenEpisodeContext()
    {
        if (!EnsureCustomProject() || ActiveProfile == null) { ShowStatus("צור או טען פרויקט ושמור אותו תחילה."); return; }
        var profile = ActiveProfile;
        var originalContext = ProjectEpisodeContext.Fingerprint(profile);
        var document = _subtitleFileName;
        var generation = _ostDocumentGeneration;
        var draft = JsonSerializer.Deserialize<List<EpisodeContextDocument>>(JsonSerializer.Serialize(profile.PreviousEpisodes ?? new()))!;
        await ProjectDialog("פרקים קודמים — חומר רקע", (panel, window) =>
        {
            window.Name = "PreviousEpisodesDialog";
            var cancel = new CancellationTokenSource(); window.Closed += (_, _) => cancel.Cancel();
            panel.Children.Add(Label("פרויקט: " + profile.Name + ". צרף כתוביות SRT בעברית או באנגלית. פרקי הרקע נשמרים מקומית בפרויקט. בפעולת Gemini שתפעיל יצורפו קטעים רלוונטיים מפרקים שסימנת, לא הפרקים המלאים. צירוף פרק אינו מאשר תרגומים למילון או לזיכרון התרגום."));
            var list = new StackPanel { Spacing = 8 }; panel.Children.Add(list);
            var status = Label(""); panel.Children.Add(status);
            void Refresh()
            {
                list.Children.Clear();
                foreach (var episode in draft.ToArray())
                {
                    var include = new CheckBox { Content = Label(episode.Name + $" — {episode.CueCount} כתוביות"), IsChecked = episode.IncludeInAi };
                    AutomationProperties.SetName(include, "כלול בהקשר AI: " + episode.Name);
                    include.IsCheckedChanged += (_, _) => episode.IncludeInAi = include.IsChecked == true;
                    list.Children.Add(include);
                    ActionButton(list, "עיין בפרק: " + episode.Name, () => ProjectDialog("תצוגת פרק רקע — " + episode.Name, (p, _) =>
                    {
                        var text = Field(episode.Text, true); text.IsReadOnly = true; text.Height = 400; p.Children.Add(text);
                    }));
                    ActionButton(list, "הסר מהרקע: " + episode.Name, () => { draft.Remove(episode); Refresh(); return Task.CompletedTask; });
                }
                status.Text = $"{draft.Count} פרקים; {draft.Count(e => e.IncludeInAi)} מסומנים להקשר. השינויים יישמרו רק בלחיצה על שמור.";
            }
            ActionButton(panel, "בחר קובצי פרקים קודמים…", async () =>
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "בחר כתוביות SRT של פרקים קודמים", AllowMultiple = true,
                    FileTypeFilter = new[] { new FilePickerFileType("כתוביות SRT") { Patterns = new[] { "*.srt" } } },
                });
                if (files.Count == 0) return;
                if (files.Count > ProjectEpisodeContext.MaxDocuments) throw new InvalidOperationException("בחר עד 32 פרקים.");
                var imported = await Task.Run(() => files.Select(f => ProjectEpisodeContext.Read(f.Path.LocalPath, cancel.Token)).ToList(), cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                if (document != _subtitleFileName || generation != _ostDocumentGeneration || ActiveProfile?.Id != profile.Id)
                    throw new InvalidOperationException("המסמך או הפרויקט השתנו; הפרקים לא צורפו.");
                draft = ProjectEpisodeContext.Merge(draft, imported); Refresh();
            });
            ActionButton(panel, "הצג קטעי רקע לדוגמה מהבחירה הנוכחית", async () =>
            {
                var preview = new ProjectProfile { PreviousEpisodes = JsonSerializer.Deserialize<List<EpisodeContextDocument>>(JsonSerializer.Serialize(draft))! };
                var query = string.Join("\n", SubtitleGridSelectedItems.Select(r => r.Text));
                var content = await Task.Run(() => ProjectEpisodeContext.PromptContext(preview, query), cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                await ProjectDialog("תצוגת הקשר — ללא שליחה", (p, _) =>
                {
                    var text = Field(content, true); text.IsReadOnly = true; text.Height = 400; p.Children.Add(text);
                });
            });
            ActionButton(panel, "שמור פרקי רקע בפרויקט", () =>
            {
                if (document != _subtitleFileName || generation != _ostDocumentGeneration) throw new InvalidOperationException("המסמך השתנה; פתח את החלון מחדש.");
                if (ProjectEpisodeContext.Fingerprint(profile) != originalContext) throw new InvalidOperationException("פרקי הרקע השתנו בחלון אחר; פתח מחדש כדי לא לדרוס שינוי חדש.");
                SaveEpisodeContext(profile, draft); window.Close(); return Task.CompletedTask;
            });
            Refresh();
        });
    }
}

