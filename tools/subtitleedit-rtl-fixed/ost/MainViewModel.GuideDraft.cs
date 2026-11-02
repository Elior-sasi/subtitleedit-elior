using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
                panel.Children.Add(Label("הזן שם מדויק ושנה/עונה כדי להבחין בין יצירות. חיפוש Google באמצעות Gemini שנבחר מופעל רק בלחיצה, ושולח את השם והחומר שתוסיף; עשויות לחול עלויות מודל וחיפוש. תוצאת המחקר והצעות החיפוש מוצגות יחד בדפדפן ברירת המחדל, עם קישורי מקור. אין הבטחת נכונות; תעתיקים והנחיות תרגום טעונים אישור. אין העלאת כתוביות או מדריכים אחרים אוטומטית. שלד מקומי ללא AI נשאר זמין."));
                var title = Field(profile.Name); title.Name = "GuideDraftTitle"; panel.Children.Add(Label("שם היצירה המקורי או שם מזוהה")); panel.Children.Add(title);
                var edition = Field(); edition.Name = "GuideDraftEdition"; panel.Children.Add(Label("שנה, עונה, גרסה או פרטים מזהים")); panel.Children.Add(edition);
                var source = Field("", true); source.Name = "GuideDraftSource"; panel.Children.Add(Label("חומר מקור אופציונלי — טקסט שאתה מספק לקריאה, עד 60,000 תווים")); panel.Children.Add(source);
                ActionButton(panel, "הוסף חומר מקור מקובץ למדריך הטיוטה…", async () =>
                {
                    var path = await PickFile("חומר מקור: DOCX, TXT, MD או CSV");
                    if (path == null || !Fresh()) return;
                    var material = await Task.Run(() => CustomProject.GuideText(path), token);
                    if (!Fresh()) throw new InvalidOperationException("הפרויקט השתנה. פתח את הכלי מחדש.");
                    var combined = (source.Text ?? "") + "\n" + material; GuideDraft.Validate(title.Text ?? "", edition.Text ?? "", combined); source.Text = combined;
                });
                var draft = Field("", true); draft.Name = "GuideDraftPreview"; draft.MinHeight = 260;
                var status = Label("הטיוטה ניתנת לעריכה. שמירה מחייבת אישור מפורש ואינה מאשרת שמות למילון."); panel.Children.Add(status);
                bool busy = false; string? preparedInputs = null;
                GuideResearchResult? researched = null; string? researchInputs = null; string? researchModel = null;
                var reviewed = new CheckBox { Content = Label("עיינתי בתוצאת המחקר ובמקורות בדפדפן; צור עותק לעריכה"), IsChecked = false };
                string Inputs() => JsonSerializer.Serialize(new { title = title.Text, edition = edition.Text, source = source.Text });
                void RequireFresh() { if (!Fresh()) throw new InvalidOperationException("הפרויקט או המסמך השתנו. סגור ופתח מחדש; המדריך לא נשמר."); }
                ActionButton(panel, "צור שלד מקומי ללא AI", () =>
                { RequireFresh(); if (busy) throw new InvalidOperationException("המתן לסיום הבקשה או בטל."); draft.Text = GuideDraft.Template(title.Text ?? "", edition.Text ?? ""); preparedInputs = Inputs(); return Task.CompletedTask; });
                ActionButton(panel, "חפש ברשת וצור מדריך עם מקורות — הצגה בדפדפן (API בתשלום)", async () =>
                {
                    RequireFresh(); if (busy) return;
                    var key = CustomWindowsIntegration.ReadKey(); if (key.Length == 0) throw new InvalidOperationException("הגדר מפתח Gemini באפשרויות.");
                    var name = title.Text ?? ""; var version = edition.Text ?? ""; var material = source.Text ?? ""; GuideDraft.Validate(name, version, material);
                    var model = GeminiModel(); var inputs = Inputs();
                    busy = true; preparedInputs = null; researched = null; reviewed.IsChecked = false;
                    status.Text = "מחפש מקורות ומכין מדריך… אפשר לבטל ולסגור. אין ניסיונות חוזרים אוטומטיים.";
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
                        status.Text = $"השירות החזיר חיפוש ו־{result.Citations.Count} הפניות. התוצאה המקורית והצעות Google פתוחות בדפדפן; עיין בהן לפני העתקה למדריך. עצם ההפניה אינה מוכיחה שכל פרט נכון.";
                    }
                    finally { busy = false; }
                });
                ActionButton(panel, "הצג שוב את תוצאת המחקר והמקורות בדפדפן", () =>
                {
                    RequireFresh(); if (busy || researched == null || reportPath == null || researchInputs != Inputs() || researchModel != GeminiModel()) throw new InvalidOperationException("אין מחקר עדכני להצגה. הפעל חיפוש.");
                    using var browser = Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true }); return Task.CompletedTask;
                });
                panel.Children.Add(reviewed);
                ActionButton(panel, "העתק את המחקר שבדקתי לטיוטת המדריך", () =>
                {
                    RequireFresh(); if (busy || researched == null || reviewed.IsChecked != true || researchInputs != Inputs() || researchModel != GeminiModel()) throw new InvalidOperationException("בצע חיפוש לפרטים הנוכחיים ועיין במקורות לפני העתקה.");
                    draft.Text = researched.UserCopy; preparedInputs = Inputs(); status.Text = "נוצר עותק לעריכה עם רשימת מקורות. מדריך הפרויקט לא הוחלף; שמירה עדיין דורשת אישור מפורש."; return Task.CompletedTask;
                });
                ActionButton(panel, "צור טיוטה ללא חיפוש באמצעות Gemini הנבחר (API בתשלום)", async () =>
                {
                    RequireFresh(); if (busy) return;
                    var key = CustomWindowsIntegration.ReadKey(); if (key.Length == 0) throw new InvalidOperationException("הגדר מפתח Gemini באפשרויות.");
                    var prompt = GuideDraft.Prompt(title.Text ?? "", edition.Text ?? "", source.Text ?? ""); var model = GeminiModel(); var inputs = Inputs();
                    busy = true; preparedInputs = null; status.Text = "מכין טיוטה… אפשר לבטל ולסגור.";
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                        var response = await CustomProject.AskAsync(http, key, prompt, token, model); RequireFresh();
                        if (inputs != Inputs() || model != GeminiModel() || key != CustomWindowsIntegration.ReadKey()) throw new InvalidOperationException("פרטי היצירה או הגדרות Gemini השתנו. הטיוטה הישנה לא הוצגה.");
                        draft.Text = GuideDraft.Parse(response); preparedInputs = inputs; status.Text = "התקבלה טיוטה לא מאומתת. בדוק וערוך לפני שמירה; לא בוצע חיפוש ברשת.";
                    }
                    finally { busy = false; }
                });
                panel.Children.Add(Label("תצוגה מקדימה ועריכת טיוטה")); panel.Children.Add(draft);
                ActionButton(panel, "אשר והחלף את מדריך הפרויקט בטיוטה הערוכה", () =>
                {
                    RequireFresh(); if (busy || preparedInputs == null || preparedInputs != Inputs()) throw new InvalidOperationException("צור טיוטה לפרטי היצירה הנוכחיים והמתן לסיומה.");
                    if (string.IsNullOrWhiteSpace(draft.Text) || draft.Text.Length > 60000) throw new InvalidDataException("המדריך חייב להכיל 1–60,000 תווים.");
                    var old = profile.Guide;
                    try { profile.Guide = draft.Text; SaveCustomProfiles(); }
                    catch { profile.Guide = old; throw; }
                    _customQcDirty = true; ScanCustomQc(); window.Close(); return Task.CompletedTask;
                });
                ActionButton(panel, "בטל וסגור ללא שמירה", () => { cancel.Cancel(); window.Close(); return Task.CompletedTask; });
            });
        }
        finally { if (reportPath != null) { try { File.Delete(reportPath); } catch { } } }
    }
}
