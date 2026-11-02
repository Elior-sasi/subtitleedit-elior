using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Automation;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private async Task OpenGuideDraft()
    {
        if (!EnsureCustomProject() || ActiveProfile == null) { await ShowCustomActionNotice("יצירת מדריך", "בחר פרויקט תחילה.", OpenProjectProfile); return; }
        var profile = ActiveProfile; var original = JsonSerializer.Serialize(profile); var document = _subtitleFileName ?? ""; var generation = _ostDocumentGeneration;
        using var cancel = new CancellationTokenSource(); var token = cancel.Token;
        bool Fresh() => !token.IsCancellationRequested && generation == _ostDocumentGeneration && document == (_subtitleFileName ?? "") &&
            ReferenceEquals(ActiveProfile, profile) && original == JsonSerializer.Serialize(profile);
        string? reportPath = null;
        try
        {
            await ProjectDialog("צור מדריך לפי שם סדרה או סרט", (panel, window) =>
            {
                window.Name = "GuideDraftDialog"; window.Closed += (_, _) => cancel.Cancel();
                var actions = PinProjectActions(window);
                var heading = Label("1. איזו סדרה או סרט?"); heading.Name = "GuideDraftStep"; panel.Children.Add(heading);
                var title = Field(profile.Name); title.Name = "GuideDraftTitle"; panel.Children.Add(title);
                panel.Children.Add(Label("Gemini · חיפוש עם מקורות · API בתשלום. המדריך יישמר רק באישורך."));
                var options = new StackPanel { Spacing = 8 };
                var advanced = DialogDetails(panel, "אפשרויות נוספות", options); advanced.Name = "GuideDraftAdvanced";
                var edition = Field(); edition.Name = "GuideDraftEdition"; options.Children.Add(Label("שנה, עונה או גרסה — אם צריך להבחין בין יצירות")); options.Children.Add(edition);
                var source = Field("", true); source.Name = "GuideDraftSource"; options.Children.Add(Label("חומר מקור נוסף (אופציונלי)")); options.Children.Add(source);
                var status = Label("כתוב את שם היצירה ולחץ על צור מדריך."); status.Name = "GuideDraftStatus"; panel.Children.Add(status);
                var progress = new ProgressBar { Name = "GuideDraftProgress", IsIndeterminate = true, IsVisible = false, Height = 8 }; panel.Children.Add(progress);
                var reviewPanel = new StackPanel { Name = "GuideDraftResearchReview", Spacing = 8, IsVisible = false }; panel.Children.Add(reviewPanel);
                reviewPanel.Children.Add(Label("תוצאת המחקר והמקורות פתוחים בדפדפן. לאחר העיון, לחץ למטה על בדקתי את המקורות — המשך לעריכה."));
                var editor = new StackPanel { Name = "GuideDraftEditor", Spacing = 8, IsVisible = false }; panel.Children.Add(editor);
                editor.Children.Add(Label("בדוק וערוך את המדריך לפני שמירה."));
                var draft = Field("", true); draft.Name = "GuideDraftPreview"; draft.MinHeight = 260; editor.Children.Add(draft);
                bool busy = false; int stage = 0; string? preparedInputs = null;
                GuideResearchResult? researched = null; string? researchInputs = null; string? researchModel = null;
                Button? generate = null, acceptResearch = null, save = null;
                string Inputs() => JsonSerializer.Serialize(new { title = title.Text, edition = edition.Text, source = source.Text });
                void RequireFresh() { if (!Fresh()) throw new InvalidOperationException("הפרויקט או המסמך השתנו. סגור ופתח מחדש; המדריך לא נשמר."); }
                void Refresh()
                {
                    progress.IsVisible = busy;
                    if (generate != null) { generate.IsVisible = true; generate.IsEnabled = !busy && !string.IsNullOrWhiteSpace(title.Text); generate.Content = stage == 2 ? "צור מחדש עם חיפוש (API בתשלום)" : "צור מדריך עם חיפוש (API בתשלום)"; AutomationProperties.SetName(generate, generate.Content as string ?? "צור מדריך"); }
                    if (acceptResearch != null) { acceptResearch.IsVisible = stage == 1; acceptResearch.IsEnabled = !busy && researched != null && researchInputs == Inputs() && researchModel == GeminiModel(); }
                    if (save != null) { save.IsVisible = stage == 2; save.IsEnabled = !busy && preparedInputs != null && preparedInputs == Inputs(); }
                    reviewPanel.IsVisible = stage == 1; editor.IsVisible = stage == 2;
                    heading.Text = stage == 0 ? "1. איזו סדרה או סרט?" : stage == 1 ? "2. עיין במחקר ובמקורות" : "3. ערוך ושמור את המדריך";
                }
                void ShowDraft(string text)
                { draft.Text = text; preparedInputs = Inputs(); stage = 2; advanced.IsExpanded = false; status.Text = "טיוטה לבדיקה. שמירה תחליף את המדריך הקיים; שמות לא יאושרו למילון אוטומטית."; Refresh(); }
                foreach (var input in new[] { title, edition, source }) input.PropertyChanged += (_, e) =>
                { if (e.Property == TextBox.TextProperty) { Refresh(); if (stage > 0 && (stage == 1 ? researchInputs : preparedInputs) != Inputs()) status.Text = "פרטי היצירה השתנו. צור מדריך מחדש לפני שמירה."; } };
                ActionButton(options, "הוסף חומר מקור מקובץ…", async () =>
                {
                    RequireFresh(); if (busy) throw new InvalidOperationException("המתן לסיום הבקשה או סגור לביטול.");
                    var path = await PickFile("חומר מקור: DOCX, TXT, MD או CSV"); if (path == null || !Fresh()) return;
                    var material = await Task.Run(() => CustomProject.GuideText(path), token); RequireFresh();
                    if (busy) throw new InvalidOperationException("בקשת יצירה כבר מתבצעת; חומר המקור לא נוסף.");
                    var combined = (source.Text ?? "") + "\n" + material; GuideDraft.Validate(title.Text ?? "", edition.Text ?? "", combined); source.Text = combined;
                });
                ActionButton(options, "צור שלד מקומי ללא AI", () =>
                { RequireFresh(); if (busy) throw new InvalidOperationException("המתן לסיום הבקשה או סגור לביטול."); ShowDraft(GuideDraft.Template(title.Text ?? "", edition.Text ?? "")); return Task.CompletedTask; });
                ActionButton(options, "צור טיוטה ללא חיפוש (Gemini API בתשלום)", async () =>
                {
                    RequireFresh(); if (busy) return;
                    var key = CustomWindowsIntegration.ReadKey(); if (key.Length == 0) throw new InvalidOperationException("הגדר מפתח Gemini באפשרויות.");
                    var prompt = GuideDraft.Prompt(title.Text ?? "", edition.Text ?? "", source.Text ?? ""); var model = GeminiModel(); var inputs = Inputs();
                    busy = true; preparedInputs = null; status.Text = "מכין טיוטה ללא חיפוש… סגור לביטול."; Refresh();
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                        var response = await CustomProject.AskAsync(http, key, prompt, token, model); RequireFresh();
                        if (inputs != Inputs() || model != GeminiModel() || key != CustomWindowsIntegration.ReadKey()) throw new InvalidOperationException("פרטי היצירה או הגדרות Gemini השתנו; התוצאה הישנה לא הוצגה.");
                        ShowDraft(GuideDraft.Parse(response)); status.Text = "טיוטה ללא חיפוש ברשת — בדוק וערוך לפני שמירה.";
                    }
                    finally { busy = false; Refresh(); }
                });
                DialogDetails(options, "מה נשלח ואיך בודקים את התוצאה?", Label("נשלחים רק פרטי היצירה וחומר המקור שתוסיף כאן; אין העלאה אוטומטית של כתוביות או מדריכים אחרים. ייתכנו חיובי מודל וחיפוש. מקורות אינם הבטחת נכונות, ותעתיקים הם הצעות עד לאישור. אם אין חיפוש עם מקורות, הפעולה נעצרת ללא מעבר שקט לתשובת מודל בלבד. סגירה מבטלת את הבקשה בלי לסגור את הדפדפן."));
                generate = ActionButton(actions, "צור מדריך עם חיפוש (API בתשלום)", async () =>
                {
                    RequireFresh(); if (busy) return;
                    var key = CustomWindowsIntegration.ReadKey(); if (key.Length == 0) throw new InvalidOperationException("הגדר מפתח Gemini באפשרויות.");
                    var name = title.Text ?? ""; var version = edition.Text ?? ""; var material = source.Text ?? ""; GuideDraft.Validate(name, version, material);
                    var model = GeminiModel(); var inputs = Inputs(); busy = true; preparedInputs = null; researched = null;
                    status.Text = "מחפש מקורות ומכין מדריך… סגור לביטול."; Refresh();
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                        var result = await GuideResearch.AskAsync(http, key, name, version, material, model, token); RequireFresh();
                        if (inputs != Inputs() || model != GeminiModel() || key != CustomWindowsIntegration.ReadKey()) throw new InvalidOperationException("פרטי היצירה או הגדרות Gemini השתנו; התוצאה הישנה לא הוצגה.");
                        var path = Path.Combine(Path.GetTempPath(), "subtitleedit-guide-" + Guid.NewGuid().ToString("N") + ".html");
                        try
                        {
                            await File.WriteAllTextAsync(path, result.Html, new UTF8Encoding(false), token); RequireFresh();
                            if (inputs != Inputs() || model != GeminiModel() || key != CustomWindowsIntegration.ReadKey()) throw new InvalidOperationException("פרטי הבקשה השתנו; לא נפתחה תוצאה ישנה.");
                            using var browser = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        }
                        catch { try { File.Delete(path); } catch { } throw; }
                        if (reportPath != null) { try { File.Delete(reportPath); } catch { } }
                        reportPath = path; researched = result; researchInputs = inputs; researchModel = model;
                        stage = 1; advanced.IsExpanded = false; status.Text = $"המחקר מוכן עם {result.Citations.Count} הפניות. עיין במקורות לפני עריכה.";
                    }
                    finally { busy = false; Refresh(); }
                }); generate.Name = "GuideDraftGenerate";
                ActionButton(reviewPanel, "פתח שוב את המחקר והמקורות", () =>
                {
                    RequireFresh(); if (busy || researched == null || reportPath == null || researchInputs != Inputs() || researchModel != GeminiModel()) throw new InvalidOperationException("אין מחקר עדכני להצגה. צור מדריך מחדש.");
                    using var browser = Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true }); return Task.CompletedTask;
                });
                acceptResearch = ActionButton(actions, "בדקתי את המקורות — המשך לעריכה", () =>
                {
                    RequireFresh(); if (busy || researched == null || researchInputs != Inputs() || researchModel != GeminiModel()) throw new InvalidOperationException("בצע חיפוש לפרטים הנוכחיים ועיין במקורות לפני העתקה.");
                    ShowDraft(researched.UserCopy); return Task.CompletedTask;
                }); acceptResearch.Name = "GuideDraftAcceptResearch";
                save = ActionButton(actions, "אשר והחלף את מדריך הפרויקט", () =>
                {
                    RequireFresh(); if (busy || preparedInputs == null || preparedInputs != Inputs()) throw new InvalidOperationException("צור טיוטה לפרטי היצירה הנוכחיים והמתן לסיומה.");
                    if (string.IsNullOrWhiteSpace(draft.Text) || draft.Text.Length > 60000) throw new InvalidDataException("המדריך חייב להכיל 1–60,000 תווים.");
                    var old = profile.Guide; try { profile.Guide = draft.Text; SaveCustomProfiles(); } catch { profile.Guide = old; throw; }
                    _customQcDirty = true; ScanCustomQc(); window.Close(); return Task.CompletedTask;
                }); save.Name = "GuideDraftSave";
                actions.Children.Remove(acceptResearch); actions.Children.Insert(0, acceptResearch);
                actions.Children.Remove(save); actions.Children.Insert(0, save);
                Refresh();
            });
        }
        finally { if (reportPath != null) { try { File.Delete(reportPath); } catch { } } }
    }
}
