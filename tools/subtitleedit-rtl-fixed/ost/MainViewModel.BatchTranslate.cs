using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

// Batch mode of "review against source / translate with the guide": same project, guide,
// glossary, approved memory and Gemini key; nothing changes before an explicit approval.
public partial class MainViewModel
{
    internal sealed record BatchSnapshot(Guid Id, string Text);

    private async Task ReviewEnglishBatch()
    {
        if (!EnsureCustomProject() || ActiveProfile == null) { ShowStatus("בחר פרויקט ומדריך תחילה."); return; }
        if (string.IsNullOrEmpty(_customWork.ReferenceText) || !_customWork.AlignmentConfirmed) { ShowStatus("טען מקור באנגלית ואשר התאמת זמנים תחילה.", 8000); await OpenEnglishReference(); return; }
        var document = _customDocument;
        var profile = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(ActiveProfile))!;
        var work = JsonSerializer.Deserialize<ProjectWork>(JsonSerializer.Serialize(_customWork))!;
        var english = CustomProject.ReadSrt(work.ReferenceText);
        var selectedIds = SubtitleGridSelectedItems.Where(r => !r.IsReferenceOnly).Select(r => r.Id).ToHashSet();
        var model = GeminiModel();

        await ProjectDialog("תרגום באצווה לפי המדריך - " + profile.Name, (p, w) =>
        {
            var cts = new CancellationTokenSource(); var requestSeq = 0; var busy = false;
            w.Closed += (_, _) => { cts.Cancel(); cts.Dispose(); };
            p.Children.Add(Label("בחר במפורש אילו שורות לתרגם. טקסט קיים אינו נחשב אנגלית ואינו מוחלף מעצמו. לכל שורה נדרש אישור או בחירה של משפט המקור באנגלית. ההתאמה נעשית לפי זמן אחרי התאמת הזמנים שאישרת - לעולם לא לפי מספר שורה."));
            var modeSelected = new RadioButton { GroupName = "batchMode", Content = Label($"השורות שנבחרו ברשימה ({selectedIds.Count})"), IsChecked = selectedIds.Count > 0, FontSize = 16 };
            var modeEmpty = new RadioButton { GroupName = "batchMode", Content = Label("שורות ריקות (ללא טקסט) בקובץ"), IsChecked = selectedIds.Count == 0, FontSize = 16 };
            AutomationProperties.SetName(modeSelected, "מצב: השורות שנבחרו"); AutomationProperties.SetName(modeEmpty, "מצב: שורות ריקות");
            p.Children.Add(modeSelected); p.Children.Add(modeEmpty);

            var pairingPanel = new StackPanel { Spacing = 8 };
            var choices = new List<(BatchPairing Pairing, CheckBox Confirm, ComboBox Pick)>();
            var info = Label(""); var results = new StackPanel { Spacing = 8 };
            var proposals = new List<(BatchItem Item, BatchProposal Proposal, CheckBox Apply)>();
            var snapshot = new List<BatchSnapshot>();

            void BuildPairing()
            {
                pairingPanel.Children.Clear(); choices.Clear(); results.Children.Clear(); proposals.Clear(); requestSeq++;
                var source = Subtitles.Where(r => !r.IsReferenceOnly).ToList();
                var chosen = modeSelected.IsChecked == true ? source.Where(r => selectedIds.Contains(r.Id)).ToList() : source.Where(r => string.IsNullOrWhiteSpace(r.Text)).ToList();
                if (chosen.Count > CustomTranslateBatch.MaxRows) { info.Text = $"נבחרו {chosen.Count} שורות. בכל שליחה עד {CustomTranslateBatch.MaxRows}; מוצגות {CustomTranslateBatch.MaxRows} הראשונות לפי סדר הקובץ."; chosen = chosen.Take(CustomTranslateBatch.MaxRows).ToList(); }
                else info.Text = chosen.Count == 0 ? "אין שורות במצב שבחרת." : $"{chosen.Count} שורות.";
                var rows = chosen.Select(r => { var i = source.IndexOf(r); return new BatchRow(r.Id, r.StartTime.TotalMilliseconds, r.EndTime.TotalMilliseconds, r.Text ?? "", i > 0 ? source[i - 1].Text ?? "" : "", i + 1 < source.Count ? source[i + 1].Text ?? "" : ""); }).ToList();
                foreach (var pairing in CustomTranslateBatch.Pair(work, rows, english))
                {
                    var head = Label(TimeSpan.FromMilliseconds(pairing.Row.Start).ToString(@"hh\:mm\:ss\,fff") + " · עברית קיימת: " + (pairing.Row.Text.Length == 0 ? "(ריקה)" : pairing.Row.Text.Replace('\n', ' ')) + "\n" + pairing.Reason);
                    var confirm = new CheckBox { Content = Label("מקור: " + (pairing.Proposed?.Text.Replace('\n', ' ') ?? "")), IsVisible = pairing.Proposed != null, FontSize = 16 };
                    AutomationProperties.SetName(confirm, "אישור משפט המקור לשורה " + TimeSpan.FromMilliseconds(pairing.Row.Start).ToString(@"hh\:mm\:ss"));
                    var pick = new ComboBox { FontSize = 16, MinWidth = 420, IsVisible = pairing.Proposed == null, PlaceholderText = "בחר משפט מקור, או השאר ריק לדילוג",
                        ItemsSource = pairing.Candidates.Select(c => TimeSpan.FromMilliseconds(c.Start).ToString(@"hh\:mm\:ss") + " · " + c.Text.Replace('\n', ' ')).ToList() };
                    AutomationProperties.SetName(pick, "בחירת משפט מקור לשורה " + TimeSpan.FromMilliseconds(pairing.Row.Start).ToString(@"hh\:mm\:ss"));
                    var box = new StackPanel { Spacing = 4, Children = { head, confirm, pick } };
                    pairingPanel.Children.Add(new Border { BorderThickness = new Avalonia.Thickness(0, 0, 0, 1), BorderBrush = Brushes.Gray, Padding = new Avalonia.Thickness(0, 4), Child = box });
                    choices.Add((pairing, confirm, pick));
                }
            }
            modeSelected.IsCheckedChanged += (_, _) => { if (!busy) BuildPairing(); };
            modeEmpty.IsCheckedChanged += (_, _) => { if (!busy) BuildPairing(); };
            p.Children.Add(info);
            ActionButton(p, "סמן אישור לכל ההתאמות החד-משמעיות שמוצגות", () => { foreach (var c in choices.Where(c => c.Pairing.IsUnambiguous)) c.Confirm.IsChecked = true; return Task.CompletedTask; });
            p.Children.Add(pairingPanel);

            List<BatchItem> Items()
            {
                var items = new List<BatchItem>(); var n = 0;
                foreach (var (pairing, confirm, pick) in choices)
                {
                    SourceCue? cue = pairing.Proposed != null ? (confirm.IsChecked == true ? pairing.Proposed : null)
                        : pick.SelectedIndex >= 0 && pick.SelectedIndex < pairing.Candidates.Count ? pairing.Candidates[pick.SelectedIndex] : null;
                    if (cue == null) continue;
                    items.Add(new BatchItem("r" + ++n, pairing.Row, cue.Text, ProjectArchive.Find(profile.Memory, cue.Text).Take(3).Select(m => m.Entry).ToList()));
                }
                return items;
            }

            var consent = Label(""); p.Children.Add(consent);
            ActionButton(p, "הצג מה יישלח והערכת עלות", async () =>
            {
                var items = Items(); var prompt = await Task.Run(() => CustomTranslateBatch.BuildPrompt(profile, items), cts.Token);
                RequireEpisodeContext(profile);
                consent.Text = $"יישלחו ל־Google (Gemini API, מודל {model}): {items.Count} משפטי מקור באנגלית שאישרת, העברית הקיימת של השורות האלה והשורות הסמוכות להן, ההנחיות, המדריך, המילון המאושר, צמדי זיכרון מאושרים תואמים וקטעים מפרקי הרקע שסימנת בפרויקט. לא יישלחו קובץ הווידאו, הערות פנימיות ושאלות לבמאי.\n" +
                               CustomTranslateBatch.Estimate(model, prompt, CustomProject.GeminiMaxOutputTokens, DateTime.UtcNow) + "\nכל לחיצה היא בקשה אחת בתשלום; אין שליחה חוזרת אוטומטית.";
            });
            ActionButton(p, "שלח אצווה אחת ל־Gemini (API בתשלום)", async () =>
            {
                if (busy) return;
                var items = Items(); var prompt = await Task.Run(() => CustomTranslateBatch.BuildPrompt(profile, items), cts.Token);
                RequireEpisodeContext(profile);
                var key = CustomWindowsIntegration.ReadKey(); if (key.Length == 0) throw new InvalidOperationException("הגדר מפתח Gemini בתפריט הפרויקט.");
                if (consent.Text is not { Length: > 0 }) throw new InvalidOperationException("לחץ תחילה על \"הצג מה יישלח והערכת עלות\".");
                if (items.Any(i => !Subtitles.Any(r => r.Id == i.Row.Id && (r.Text ?? "") == i.Row.Text))) throw new InvalidOperationException("הכתוביות השתנו; בנה שוב את ההתאמות.");
                busy = true; var seq = ++requestSeq; results.Children.Clear(); proposals.Clear();
                snapshot = Subtitles.Where(r => items.Any(i => i.Row.Id == r.Id)).Select(r => new BatchSnapshot(r.Id, r.Text ?? "")).ToList();
                results.Children.Add(Label("ממתין לתשובת Gemini… אפשר לבטל."));
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                    var json = await CustomProject.AskAsync(http, key, prompt, cts.Token, model);
                    RequireEpisodeContext(profile);
                    if (seq != requestSeq) return; // a newer selection replaced this request: its answer is discarded
                    var parsed = CustomTranslateBatch.Parse(json, items);
                    results.Children.Clear();
                    results.Children.Add(Label("סמן רק הצעות שבדקת. ההחלה היא פעולה אחת שניתן לבטל ב־Undo. מונחים וזיכרון תרגום אינם נשמרים מעצמם."));
                    foreach (var prop in parsed)
                    {
                        var item = items.First(i => i.Key == prop.Key);
                        results.Children.Add(Label("מקור: " + item.English.Replace('\n', ' ') + "\nקיים: " + (item.Row.Text.Length == 0 ? "(ריקה)" : item.Row.Text) + "\nהצעה: " + prop.Text + (prop.Note.Length > 0 ? "\nהערה: " + prop.Note : "") + (prop.Problem != null ? "\n⚠ לא ניתן להחיל: " + prop.Problem : "")));
                        var apply = new CheckBox { Content = Label("החל הצעה זו"), IsEnabled = prop.Problem == null, FontSize = 16 };
                        AutomationProperties.SetName(apply, "החלת ההצעה לשורה " + TimeSpan.FromMilliseconds(item.Row.Start).ToString(@"hh\:mm\:ss"));
                        results.Children.Add(apply); proposals.Add((item, prop, apply));
                    }
                }
                catch (OperationCanceledException) { results.Children.Clear(); results.Children.Add(Label("הבקשה בוטלה. לא בוצע שינוי.")); }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException or System.IO.InvalidDataException or JsonException or KeyNotFoundException)
                {
                    results.Children.Clear(); results.Children.Add(Label("הבקשה נכשלה: " + e.Message + "\nלא בוצע שינוי. אין שליחה חוזרת אוטומטית; ייתכן ש־Google חייבה על בקשה שהתחילה."));
                }
                finally { busy = false; }
            });
            ActionButton(p, "בטל בקשה", () => { requestSeq++; cts.Cancel(); cts = new CancellationTokenSource(); return Task.CompletedTask; });
            p.Children.Add(results);
            ActionButton(p, "החל את ההצעות המסומנות", () =>
            {
                var approved = proposals.Where(x => x.Apply.IsChecked == true && x.Proposal.Problem == null).ToList();
                if (approved.Count == 0) throw new InvalidOperationException("לא סומנו הצעות להחלה.");
                RequireEpisodeContext(profile);
                ApplyBatchTranslations(document, snapshot, approved.Select(x => (x.Item.Row.Id, x.Proposal.Text)).ToList());
                ShowStatus($"הוחלו {approved.Count} הצעות שאישרת. ניתן לבטל ב־Undo.", 8000);
                w.Close();
                return Task.CompletedTask;
            });
            BuildPairing();
        });
    }

    // Applies only approved texts to rows that are still exactly as sent. Ids, times, order,
    // notes, OST markers, director questions and review states are left untouched.
    internal void ApplyBatchTranslations(string document, IReadOnlyList<BatchSnapshot> sent, IReadOnlyList<(Guid Id, string Text)> approved)
    {
        if (document != (_subtitleFileName ?? "")) throw new InvalidOperationException("הקובץ הפעיל השתנה. לא בוצע שינוי.");
        var changes = new List<(SubtitleLineViewModel Row, string Text)>();
        foreach (var (id, proposed) in approved)
        {
            var before = sent.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("שורה שלא נשלחה. לא בוצע שינוי.");
            var row = Subtitles.FirstOrDefault(r => r.Id == id);
            if (row == null || !CustomProject.CanApply(before.Text, row.Text ?? "", id, row.Id, document, _subtitleFileName ?? ""))
                throw new InvalidOperationException("שורה השתנתה מאז השליחה. לא בוצע שינוי; שלח שוב את השורות שהשתנו.");
            var expectedTags = string.IsNullOrWhiteSpace(before.Text) ? null : CustomTranslateBatch.Tags(before.Text);
            if (expectedTags != null && CustomTranslateBatch.Tags(proposed) != expectedTags) throw new System.IO.InvalidDataException("הצעה שינתה תגיות. לא בוצע שינוי.");
            // Existing Hebrew line-break rules, applied after the wording was approved; kept only if tags and words are unchanged.
            var text = proposed;
            if (CustomTranslateBatch.IsHebrew(text))
            {
                var broken = Utilities.AutoBreakLine(text, Se.Settings.General.SubtitleLineMaximumLength, 0, "he");
                string Words(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (CustomTranslateBatch.Tags(broken) == CustomTranslateBatch.Tags(text) && Words(broken) == Words(text)) text = broken;
            }
            changes.Add((row, text));
        }
        var beforeSnapshot = MakeUndoRedoObject("לפני תרגום אצווה שאושר");
        if (_undoRedoManager.UndoList.LastOrDefault()?.Hash != beforeSnapshot.Hash) _undoRedoManager.Do(beforeSnapshot);
        var change = BeginToolApply("תרגום אצווה שאושר");
        EndToolApply(change, () => { foreach (var (row, text) in changes) row.Text = text; });
        RefreshOstMarkers();
    }
}


