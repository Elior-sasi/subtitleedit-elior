using System;
using System.IO;
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
        await ProjectDialog("צור מדריך לפי שם סדרה או סרט", (panel, window) =>
        {
            window.Name = "GuideDraftDialog"; window.Closed += (_, _) => cancel.Cancel();
            panel.Children.Add(Label("הזן שם מדויק ושנה/עונה כדי להבחין בין יצירות. שלד מקומי אינו דורש AI. מילוי לפי ידע המודל שולח את השם וחומר המקור שהוספת ל־Gemini שנבחר, כרוך בעלות API ואינו חיפוש ברשת. קישורים לבדם לא נקראים. אין העלאה אוטומטית של כתוביות או של מדריכים אחרים."));
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
            string Inputs() => JsonSerializer.Serialize(new { title = title.Text, edition = edition.Text, source = source.Text });
            void RequireFresh() { if (!Fresh()) throw new InvalidOperationException("הפרויקט או המסמך השתנו. סגור ופתח מחדש; המדריך לא נשמר."); }
            ActionButton(panel, "צור שלד מקומי ללא AI", () =>
            { RequireFresh(); if (busy) throw new InvalidOperationException("המתן לסיום הבקשה או בטל."); draft.Text = GuideDraft.Template(title.Text ?? "", edition.Text ?? ""); preparedInputs = Inputs(); return Task.CompletedTask; });
            ActionButton(panel, "צור טיוטה באמצעות Gemini הנבחר (API בתשלום)", async () =>
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
}
