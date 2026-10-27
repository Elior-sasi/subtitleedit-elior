using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private ProjectWork _customWork = new();
    private List<ProjectProfile> _customProfiles = new();
    private string _customDocument = "";
    private int _customGeneration = -1;
    private bool _customOstOnly;
    private readonly Dictionary<Guid, WorkNote?> _customNoteCache = new();
    private Button? _customProjectButton;
    private string ProfilePath => Path.Combine(Se.DataFolder, "CustomProjects", "profiles.json");
    private ProjectProfile? ActiveProfile => _customProfiles.FirstOrDefault(p => p.Id == _customWork.ProfileId);
    private string WorkPath => Path.ChangeExtension(_customDocument, ".sework.json");

    internal Control MakeCustomProjectToolbar()
    {
        CustomWindowsIntegration.Initialize();
        var menu = new MenuFlyout();
        void Item(string title, Func<Task> action) { var item = new MenuItem { Header = MenuCaption(title), FlowDirection = FlowDirection.LeftToRight }; AutomationProperties.SetName(item, title); item.Click += async (_, _) => { await Task.Yield(); try { await action(); } catch (Exception e) { ShowStatus(e.Message, 8000); } }; menu.Items.Add(item); }
        Item("תסריט לכתוביות…", OpenScriptToSubtitles);
        Item("תסריט שונה מהדיבור — השוואה ואישור…", () => OpenScriptTranscriptReview());
        Item("השוואת גרסאות — טקסט או זמנים…", ShowCompare);
        Item("סקירה ממוקדת — ניגון שורות לבדיקה…", OpenProFocusedReview);
        Item("קבע נקודת בדיקה לשינויים הבאים", () => { SetProReviewBaseline(); return Task.CompletedTask; });
        Item("הצמדה לחילופי שוטים — הצעות לאישור…", OpenProShotPreview);
        Item("רוחב טקסט ואזור בטוח — בדיקה…", OpenProWidthReview);
        Item("מילון והנחיות הפרויקט…", OpenProjectProfile);
        Item("פרקים קודמים — חומר רקע…", OpenEpisodeContext);
        Item("טען וסנכרן מקור באנגלית…", OpenEnglishReference);
        Item("בדוק מול המקור / תרגם עם המדריך…", ReviewEnglish);
        Item("אתר שמות ומונחים בקובץ…", FindProjectTerms);
        Item("משפט חוזר / זיכרון תרגום…", SearchTranslationMemory);
        Item("התאם כתוביות לגרסת וידאו אחרת…", OpenVideoEditionSync);
        Item("תיקון תזמוני CC לפי הדיבור…", OpenCcTiming);
        Item("גיבוי לתיקיית ענן מסונכרנת…", OpenProjectBackup);
        Item("ייבא מדריך וזיכרון מגיבוי…", ImportArchivedGuide);
        Item("הערה ומצב בדיקה לשורה…", EditProjectNote);
        Item("שאלה לבמאי על השורה הנבחרת…", () => EditProjectNoteCore(true));
        Item("טבלת שאלות לבמאי / ייצוא…", OpenDirectorQuestions);
        Item("הקודמת לבדיקה", () => { NavigateProjectReview(-1); return Task.CompletedTask; });
        Item("הבאה לבדיקה", () => { NavigateProjectReview(1); return Task.CompletedTask; });
        Item("בדוק עקביות שמות ומונחים…", CheckProjectTerms);
        Item("דוח מסירה קצר…", ProjectDeliveryReport);
        Item("חיפוש בשורות OST…", SearchOstRows);
        Item("גיבוי ושחזור הגדרות…", OpenSettingsBackups);
        Item("Gemini - מפתח ובחירת מודל…", ConfigureGemini);
        _customProjectButton = new Button { Content = "פרויקט", FontSize = 16, MinHeight = 36, Flyout = menu, Name = "CustomProjectToolbarButton" };
        AutomationProperties.SetName(_customProjectButton, "מדריך, מקור באנגלית, הערות ובדיקת הפרויקט");
        return _customProjectButton;
    }
    internal MenuItem MakeCustomProjectMenu()
    {
        var button = (Button)MakeCustomProjectToolbar();
        var flyout = (MenuFlyout)button.Flyout!;
        var items = flyout.Items.ToArray();
        flyout.Items.Clear();
        var menu = new MenuItem { Name = "CustomProjectMainMenu", Header = "פרויקט", FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(menu, "פרויקט");
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    internal void DistributeCustomProjectActions(MenuItem sync, MenuItem translate, MenuItem tools, MenuItem options)
    {
        var project = Menu.Items.OfType<MenuItem>().Single(m => m.Name == "CustomProjectMainMenu");
        var syncLabels = new[] { "תסריט לכתוביות…", "תסריט שונה מהדיבור — השוואה ואישור…", "הצמדה לחילופי שוטים — הצעות לאישור…", "טען וסנכרן מקור באנגלית…", "התאם כתוביות לגרסת וידאו אחרת…", "תיקון תזמוני CC לפי הדיבור…" };
        var translateLabels = new[] { "בדוק מול המקור / תרגם עם המדריך…", "אתר שמות ומונחים בקובץ…", "משפט חוזר / זיכרון תרגום…" };
        var toolsLabels = new[] { "השוואת גרסאות — טקסט או זמנים…", "סקירה ממוקדת — ניגון שורות לבדיקה…", "קבע נקודת בדיקה לשינויים הבאים", "רוחב טקסט ואזור בטוח — בדיקה…", "הקודמת לבדיקה", "הבאה לבדיקה", "בדוק עקביות שמות ומונחים…", "דוח מסירה קצר…", "חיפוש בשורות OST…" };
        var optionsLabels = new[] { "גיבוי ושחזור הגדרות…", "Gemini - מפתח ובחירת מודל…" };
        foreach (var (target, labels) in new[] { (sync, syncLabels), (translate, translateLabels), (tools, toolsLabels), (options, optionsLabels) })
        {
            target.Items.Add(new Separator());
            foreach (var label in labels)
            {
                var item = project.Items.OfType<MenuItem>().Single(i => (i.Header as TextBlock)?.Text == label);
                project.Items.Remove(item);
                target.Items.Add(item);
            }
        }
    }

    private static TextBlock MenuCaption(string text) => new() { Text = text, FlowDirection = FlowDirection.RightToLeft, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 16, FlowDirection = FlowDirection.RightToLeft, TextWrapping = TextWrapping.Wrap };
    private static TextBox Field(string text = "", bool multiline = false) => new() { Text = text, FontSize = 16, FlowDirection = FlowDirection.RightToLeft, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 110 : 36 };
    private void ActionButton(StackPanel panel, string text, Func<Task> action)
    {
        var b = new Button { Content = text, FontSize = 16, MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetName(b, text);
        b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch (Exception e) { ShowStatus(e.Message, 10000); } finally { b.IsEnabled = true; } }; panel.Children.Add(b);
    }
    private Task ProjectDialog(string title, Action<StackPanel, Window> build)
    {
        if (Window == null) return Task.CompletedTask;
        var dialog = new Window { Title = title, Width = 780, Height = 680, MinWidth = 550, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true, FlowDirection = FlowDirection.LeftToRight };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 }; build(panel, dialog);
        var close = new Button { Content = "סגור", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => dialog.Close();
        var dock = new DockPanel(); DockPanel.SetDock(close, Dock.Bottom); dock.Children.Add(close); dock.Children.Add(new ScrollViewer { Content = panel }); dialog.Content = dock;
        return dialog.ShowDialog(Window);
    }
    private bool EnsureCustomProject()
    {
        if (string.IsNullOrWhiteSpace(_subtitleFileName)) { ShowStatus("שמור תחילה את קובץ הכתוביות בשם קבוע.", 6000); return false; }
        if (_customDocument == _subtitleFileName && _customGeneration == _ostDocumentGeneration) return true;
        var profiles = CustomProject.Load<List<ProjectProfile>>(ProfilePath);
        var work = CustomProject.Load<ProjectWork>(Path.ChangeExtension(_subtitleFileName, ".sework.json"));
        _customProfiles = profiles; _customWork = work; _customNoteCache.Clear();
        _customDocument = _subtitleFileName; _customGeneration = _ostDocumentGeneration; _customOstOnly = false;
        if (string.IsNullOrEmpty(_customWork.ProfileId))
        {
            var matches = CustomProject.Match(_customProfiles, _customDocument);
            if (matches.Count == 1) { _customWork.ProfileId = matches[0].Id; SaveCustomWork(); }
            else if (matches.Count > 1) ShowStatus("יותר מפרויקט אחד מתאים לשם הקובץ. בחר פרויקט ידנית.", 8000);
        }
        CustomWindowsIntegration.RecentFile(_customDocument);
        return true;
    }
    private void SaveCustomWork() { if (!string.IsNullOrWhiteSpace(_customDocument)) CustomProject.Save(WorkPath, _customWork); }
    private void SaveCustomProfiles() => CustomProject.Save(ProfilePath, _customProfiles);
    internal void MonitorCustomProject()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_subtitleFileName)) return;
            if (!EnsureCustomProject()) return;
            var dirty = false;
            foreach (var row in Subtitles)
            {
                if (!_customNoteCache.TryGetValue(row.Id, out var n)) { n = CustomProject.FindNote(_customWork, row.Id, row.Text ?? "", row.StartTime.TotalMilliseconds, row.EndTime.TotalMilliseconds); _customNoteCache[row.Id] = n; }
                row.CustomWorkMarker = n == null ? "" : (n.NeedsReview ? " ◇" : n.Reviewed ? " ☑" : "") + (!string.IsNullOrWhiteSpace(n.Note) ? " ✎" : "") + (n.DirectorQuestion && !n.DirectorResolved ? " ?" : "");
                row.CustomWorkDescription = n == null ? "" : (n.NeedsReview ? "דורש בדיקה. " : n.Reviewed ? "נבדק. " : "") + (n.DirectorQuestion ? (n.DirectorResolved ? "שאלה לבמאי טופלה. " : "ממתין לתשובת הבמאי. ") : "") + (string.IsNullOrWhiteSpace(n.Note) ? "" : "הערה פנימית: " + n.Note);
                row.IsOstFilterExcluded = _customOstOnly && !row.IsInOst;
                if (n != null && (n.Text != row.Text || n.Start != row.StartTime.TotalMilliseconds || n.End != row.EndTime.TotalMilliseconds))
                { if (n.Text != row.Text && n.Reviewed) { n.Reviewed = false; n.NeedsReview = true; } DirectorQuestions.RefreshNote(n, row.Text ?? "", row.StartTime.TotalMilliseconds, row.EndTime.TotalMilliseconds); dirty = true; }
            }
            if (dirty) SaveCustomWork();
            MonitorProjectBackup();
            if (_customProjectButton != null)
            {
                _customProjectButton.Content = _customOstOnly ? "פרויקט · OST בלבד" : "פרויקט";
                AutomationProperties.SetName(_customProjectButton, "פרויקט פעיל: " + (ActiveProfile?.Name ?? "לא שויך") + (_customOstOnly ? ". מוצגות שורות OST בלבד; הצג הכול בתפריט OST." : ""));
            }
        }
        catch (Exception e) { ShowStatus("לא ניתן לטעון או לשמור את פרטי הפרויקט: " + e.Message, 10000); }
    }
    private async Task<string?> PickFile(string title)
    {
        if (Window == null) return null;
        var files = await Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.Path.LocalPath;
    }
    private async Task OpenProjectProfile()
    {
        if (!EnsureCustomProject()) return;
        await ProjectDialog("מילון והנחיות הפרויקט", (p, w) =>
        {
            var choices = new ComboBox { ItemsSource = _customProfiles, DisplayMemberBinding = new Avalonia.Data.Binding("Name"), SelectedItem = ActiveProfile, FontSize = 16, MinWidth = 300 };
            p.Children.Add(Label("פרויקט פעיל — בדוק לפני כל פעולת AI")); p.Children.Add(choices);
            ActionButton(p, "טען את הפרויקט הנבחר", () => { if (choices.SelectedItem is ProjectProfile s) { _customWork.ProfileId = s.Id; SaveCustomWork(); w.Close(); return OpenProjectProfile(); } return Task.CompletedTask; });
            var edit = ActiveProfile ?? new ProjectProfile();
            var name = Field(edit.Name); var alias = Field(edit.Alias); var folder = Field(edit.Folder); var prompt = Field(edit.Prompt, true); var guide = Field(edit.Guide, true);
            p.Children.Add(Label("שם הפרויקט")); p.Children.Add(name); p.Children.Add(Label("כינוי בשם הקובץ, למשל Avatar (ללא עונה ופרק)")); p.Children.Add(alias);
            p.Children.Add(Label("או תיקייה לשיוך אוטומטי — התאמה לא ברורה מחייבת בחירה")); p.Children.Add(folder);
            p.Children.Add(Label("פרומפט כללי")); p.Children.Add(prompt); p.Children.Add(Label("מדריך תרגום — ניתן לערוך לפני אישור")); p.Children.Add(guide);
            ActionButton(p, "ייבא מדריך תרגום…", async () => { var path = await PickFile("מדריך: DOCX, TXT, MD או CSV"); if (path != null) guide.Text = CustomProject.GuideText(path); });
            ActionButton(p, "שמור פרויקט והנחיות", () => { if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidDataException("הזן שם לפרויקט."); edit.Name = name.Text.Trim(); edit.Alias = alias.Text?.Trim() ?? ""; edit.Folder = folder.Text?.Trim() ?? ""; edit.Prompt = prompt.Text ?? ""; edit.Guide = guide.Text ?? ""; if (!_customProfiles.Contains(edit)) _customProfiles.Add(edit); _customWork.ProfileId = edit.Id; SaveCustomProfiles(); SaveCustomWork(); w.Close(); return Task.CompletedTask; });
            if (ActiveProfile != null) ActionButton(p, $"נהל פרקים קודמים ({ActiveProfile.PreviousEpisodes?.Count ?? 0})…", OpenEpisodeContext);
            ActionButton(p, "צור פרויקט נפרד חדש", () => { _customWork.ProfileId = ""; SaveCustomWork(); w.Close(); return OpenProjectProfile(); });
            foreach (var term in edit.Terms.ToList())
            {
                p.Children.Add(Label($"{term.Source} ← {term.Preferred} | חלופות: {string.Join(", ", term.Variants)}"));
                ActionButton(p, "ערוך: " + term.Preferred, () => EditTerm(term, false));
            }
            ActionButton(p, "הוסף שם או מונח ידנית", () => EditTerm(new ProjectTerm(), true));
        });
    }
    private Task EditTerm(ProjectTerm term, bool add) => ProjectDialog("אישור שם או מונח למילון", (p, w) =>
    {
        var preferred = Field(term.Preferred); var source = Field(term.Source); var variants = Field(string.Join("|", term.Variants));
        p.Children.Add(Label("איות / תרגום מאושר")); p.Children.Add(preferred); p.Children.Add(Label("מקור באנגלית, אם יש")); p.Children.Add(source); p.Children.Add(Label("חלופות לזיהוי, מופרדות ב־|")); p.Children.Add(variants);
        ActionButton(p, "אשר למילון בלבד — הכתוביות לא ישתנו", () => { if (ActiveProfile == null) throw new InvalidOperationException("בחר פרויקט תחילה."); if (string.IsNullOrWhiteSpace(preferred.Text)) throw new InvalidDataException("הזן מונח מאושר."); term.Preferred = preferred.Text.Trim(); term.Source = source.Text ?? ""; term.Variants = (variants.Text ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); if (add) { var existing = ActiveProfile.Terms.FirstOrDefault(t => t.Preferred == term.Preferred); if (existing == null) ActiveProfile.Terms.Add(term); else { existing.Variants = existing.Variants.Concat(term.Variants).Distinct().ToArray(); if (string.IsNullOrEmpty(existing.Source)) existing.Source = term.Source; } } SaveCustomProfiles(); w.Close(); return Task.CompletedTask; });
        if (!add) ActionButton(p, "הסר מהמילון", () => { ActiveProfile?.Terms.Remove(term); SaveCustomProfiles(); w.Close(); return Task.CompletedTask; });
    });
    private async Task OpenEnglishReference()
    {
        if (!EnsureCustomProject()) return;
        var document = _customDocument;
        await ProjectDialog("מקור באנגלית והתאמת זמנים", (p, w) =>
        {
            p.Children.Add(Label("טען קובץ SRT באנגלית. טקסט המקור והזמנים בעברית לא ישתנו. בכל בדיקה תאשר את משפט המקור."));
            var info = Label(_customWork.ReferenceFile); p.Children.Add(info);
            ActionButton(p, "טען כתוביות באנגלית…", async () => { var path = await PickFile("קובץ SRT באנגלית"); if (path == null) return; if (new FileInfo(path).Length > 20_000_000) throw new InvalidDataException("קובץ המקור גדול מדי; בחר קובץ כתוביות עד 20 MB."); var text = File.ReadAllText(path); if (CustomProject.ReadSrt(text).Count == 0) throw new InvalidDataException("לא נמצאו כתוביות SRT תקינות."); if (document != _subtitleFileName) throw new InvalidOperationException("הקובץ הפעיל השתנה."); _customWork.ReferenceText = text; _customWork.ReferenceFile = path; _customWork.AlignmentConfirmed = false; SaveCustomWork(); info.Text = path; });
            p.Children.Add(Label("נקודות סנכרון: הזמן באנגלית מול הזמן המקביל בעברית, בפורמט 00:00:00,000. לפער קבוע מלא רק נקודה ראשונה. לפער שמשתנה מלא גם נקודה שנייה בהמשך הסרט. הכתוביות עצמן לא ישתנו."));
            var fields = new[] { Field(TimeSpan.FromMilliseconds(_customWork.SourceAnchor1).ToString(@"hh\:mm\:ss\,fff")), Field(TimeSpan.FromMilliseconds(_customWork.TargetAnchor1).ToString(@"hh\:mm\:ss\,fff")), Field(TimeSpan.FromMilliseconds(_customWork.SourceAnchor2).ToString(@"hh\:mm\:ss\,fff")), Field(TimeSpan.FromMilliseconds(_customWork.TargetAnchor2).ToString(@"hh\:mm\:ss\,fff")) };
            var labels = new[] { "נקודה 1 — זמן באנגלית", "נקודה 1 — זמן בעברית", "נקודה 2 — זמן באנגלית (אופציונלי)", "נקודה 2 — זמן בעברית (אופציונלי)" };
            for (var i = 0; i < fields.Length; i++) { p.Children.Add(Label(labels[i])); p.Children.Add(fields[i]); }
            ActionButton(p, "אשר התאמת זמנים למקור בלבד", () => { var v = fields.Select(f => string.IsNullOrWhiteSpace(f.Text) ? 0 : CustomProject.Time(f.Text)).ToArray(); var test = new ProjectWork { SourceAnchor1 = v[0], TargetAnchor1 = v[1], SourceAnchor2 = v[2], TargetAnchor2 = v[3] }; _ = test.Map(1); _customWork.SourceAnchor1 = v[0]; _customWork.TargetAnchor1 = v[1]; _customWork.SourceAnchor2 = v[2]; _customWork.TargetAnchor2 = v[3]; _customWork.AlignmentConfirmed = true; SaveCustomWork(); w.Close(); return Task.CompletedTask; });
        });
    }
    private static string GeminiModelPath => Path.Combine(Se.DataFolder, "CustomProjects", "gemini-model.json");
    // The model is an explicit user choice stored beside the encrypted key, never in Settings.json.
    internal static string GeminiModel()
    {
        try { var id = File.Exists(GeminiModelPath) ? JsonSerializer.Deserialize<string>(File.ReadAllText(GeminiModelPath)) ?? "" : ""; return CustomTranslateBatch.IsValidModelId(id) ? id : CustomTranslateBatch.DefaultModel; }
        catch { return CustomTranslateBatch.DefaultModel; }
    }
    private async Task ConfigureGemini() => await ProjectDialog("Gemini - מפתח ומודל", (p, w) =>
    {
        p.Children.Add(Label("המדריך והטקסט שבחרת יישלחו ל־Google רק בלחיצה על הפעלת AI. שירות API בתשלום לפי החשבון שלך. המפתח מוצפן בחשבון Windows ואינו כלול בגיבוי ההגדרות."));
        var key = Field(); key.PasswordChar = '●'; AutomationProperties.SetName(key, "מפתח Gemini API"); p.Children.Add(key);
        var status = Label("מודל נוכחי: " + GeminiModel() + ". המודל המבוקש: Gemini 4 Argon, מזהה gemini-4-argon (לפי תיעוד Google). זמינות בחשבון שלך נבדקת רק בלחיצה למטה.");
        ActionButton(p, "שמור מפתח מוצפן", () => { if (string.IsNullOrWhiteSpace(key.Text)) throw new InvalidDataException("הזן מפתח API."); CustomWindowsIntegration.SaveKey(key.Text); key.Text = ""; status.Text = "המפתח נשמר מוצפן."; return Task.CompletedTask; });
        p.Children.Add(status);
        var models = new ComboBox { FontSize = 16, MinWidth = 360, IsVisible = false }; AutomationProperties.SetName(models, "בחירת מודל Gemini זמין");
        var available = new List<GeminiModelInfo>();
        var cts = new CancellationTokenSource(); w.Closed += (_, _) => { cts.Cancel(); cts.Dispose(); };
        ActionButton(p, "בדוק זמינות gemini-4-argon בחשבון שלי (רשימת מודלים, ללא שליחת טקסט)", async () =>
        {
            var k = CustomWindowsIntegration.ReadKey(); if (k.Length == 0) throw new InvalidOperationException("שמור מפתח תחילה.");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var argon = await CustomTranslateBatch.GetModelAsync(http, k, CustomTranslateBatch.DefaultModel, cts.Token);
            available = await CustomTranslateBatch.ListModelsAsync(http, k, cts.Token);
            models.ItemsSource = available.Select(m => m.Id + " - " + m.DisplayName).ToList(); models.IsVisible = available.Count > 0;
            models.SelectedIndex = available.FindIndex(m => m.Id == GeminiModel());
            status.Text = argon != null
                ? $"gemini-4-argon זמין בחשבון שלך ({argon.DisplayName}, עד {argon.InputTokenLimit:N0} טוקנים בקלט). אפשר להשאיר אותו או לבחור מודל אחר במפורש."
                : "gemini-4-argon אינו זמין בחשבון או במפתח הזה. לא הוחלף מודל. בחר במפורש מודל זמין מהרשימה, או בדוק בחשבון Google.";
        });
        p.Children.Add(models);
        ActionButton(p, "השתמש במודל שבחרתי ברשימה", () =>
        {
            if (models.SelectedIndex < 0 || models.SelectedIndex >= available.Count) throw new InvalidOperationException("בחר מודל מהרשימה אחרי בדיקת הזמינות.");
            var id = available[models.SelectedIndex].Id; CustomProject.Save(GeminiModelPath, id);
            status.Text = "נבחר המודל " + id + ". התמחור שלו עשוי להיות שונה; התוכנה מציגה הערכת עלות רק עבור gemini-4-argon.";
            return Task.CompletedTask;
        });
    });
    private async Task ReviewEnglish()
    {
        if (!EnsureCustomProject() || ActiveProfile == null) { ShowStatus("בחר פרויקט ומדריך תחילה."); return; }
        if (SubtitleGridSelectedItems.Count(r => !r.IsReferenceOnly) > 1) { await ReviewEnglishBatch(); return; }
        var row = SubtitleGridSelectedItems.FirstOrDefault(); if (row == null) { ShowStatus("סמן כתובית תחילה, או כמה כתוביות לתרגום באצווה."); return; }
        if (string.IsNullOrEmpty(_customWork.ReferenceText)) { await OpenEnglishReference(); return; }
        var expectedText = row.Text ?? ""; var document = _customDocument; var profile = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(ActiveProfile))!;
        var candidates = CustomProject.Candidates(_customWork, row.StartTime.TotalMilliseconds, row.EndTime.TotalMilliseconds);
        await ProjectDialog("בדיקה מול המקור — " + profile.Name, (p, w) =>
        {
            p.Children.Add(Label("בחר את המשפט האנגלי המתאים; אפשר לסמן כמה שורות מקור. ההתאמה היא לפי זמן משוער ואינה אישור שזה אותו משפט."));
            var checks = new List<(CheckBox Check, SourceCue Cue)>();
            foreach (var c in candidates) { var check = new CheckBox { Content = Label(TimeSpan.FromMilliseconds(c.Start).ToString(@"hh\:mm\:ss") + " · " + c.Text) }; checks.Add((check, c)); p.Children.Add(check); }
            p.Children.Add(Label("העברית הנוכחית: " + expectedText));
            p.Children.Add(Label($"פרקי רקע מסומנים: {profile.PreviousEpisodes?.Count(e => e.IncludeInAi) ?? 0}. קטעים רלוונטיים יצורפו בשליחה ל־Gemini."));
            ActionButton(p, "השמע את הכתובית בלולאה", () => { SubtitleGrid.SelectedItems?.Clear(); SubtitleGrid.SelectedItem = row; PlaySelectedLinesWithLoopCommand.Execute(null); return Task.CompletedTask; });
            ActionButton(p, "הפסק השמעה", () => { PauseCommand.Execute(null); return Task.CompletedTask; });
            w.Closed += (_, _) => PauseCommand.Execute(null);
            var suggestions = new StackPanel { Spacing = 10 }; p.Children.Add(suggestions);
            var cancel = new CancellationTokenSource(); w.Closed += (_, _) => { cancel.Cancel(); cancel.Dispose(); };
            ActionButton(p, "תרגום כמה שורות באצווה (שורות שנבחרו או שורות ריקות)…", async () => { w.Close(); await ReviewEnglishBatch(); });
            ActionButton(p, "חפש את המקור שנבחר בזיכרון התרגום", () => { var english = string.Join("\n", checks.Where(c => c.Check.IsChecked == true).Select(c => c.Cue.Text)); if (english.Length == 0) throw new InvalidOperationException("בחר משפט מקור תחילה."); suggestions.Children.Clear(); AddMemoryMatches(suggestions, english, row.Id, expectedText, document, profile, w); return Task.CompletedTask; });
            ActionButton(p, "אשר ושמור את המקור שנבחר והעברית הנוכחית בזיכרון", () => { var english = string.Join("\n", checks.Where(c => c.Check.IsChecked == true).Select(c => c.Cue.Text)); if (!CustomProject.CanApply(expectedText, row.Text ?? "", row.Id, row.Id, document, _subtitleFileName ?? "")) throw new InvalidOperationException("הכתובית השתנתה; פתח את הבדיקה מחדש."); var live = ActiveProfile; if (live == null || live.Id != profile.Id) throw new InvalidOperationException("הפרויקט השתנה."); ProjectArchive.Approve(live, english, expectedText, document); SaveCustomProfiles(); profile.Memory = live.Memory.ToList(); ShowStatus("צמד מקור ותרגום אושר בזיכרון הפרויקט. הטקסט לא שונה.", 7000); return Task.CompletedTask; });
            ActionButton(p, "שלח ל־Gemini לפי המדריך (API בתשלום)", async () =>
            {
                var english = string.Join("\n", checks.Where(c => c.Check.IsChecked == true).Select(c => c.Cue.Text)); if (english.Length == 0) throw new InvalidOperationException("בחר משפט מקור תחילה.");
                var key = CustomWindowsIntegration.ReadKey(); if (key.Length == 0) throw new InvalidOperationException("הגדר מפתח Gemini בתפריט הפרויקט.");
                var context = Subtitles.Skip(Math.Max(0, Subtitles.IndexOf(row) - 2)).Take(5).Select(r => r.Text).ToArray();
                var prompt = await Task.Run(() => CustomProject.BuildPrompt(profile, "Offer up to 3 Hebrew translation alternatives and Hebrew explanations. Preserve ALL original markup bytes. Return {\"suggestions\":[{\"text\":\"...\",\"reason\":\"...\"}]}.", JsonSerializer.Serialize(new { english, currentHebrew = expectedText, context })), cancel.Token);
                RequireEpisodeContext(profile);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) }; var json = await CustomProject.AskAsync(http, key, prompt, cancel.Token, GeminiModel()); RequireEpisodeContext(profile); var offers = CustomProject.Offers(json); suggestions.Children.Clear();
                foreach (var offer in offers)
                {
                    suggestions.Children.Add(Label("הצעה: " + offer.Text + "\nהסבר: " + offer.Reason));
                    ActionButton(suggestions, "החל הצעה על השורה הנבחרת בלבד", () => { RequireEpisodeContext(profile); ApplyCustomOffer(row.Id, expectedText, document, offer.Text); w.Close(); return Task.CompletedTask; });
                }
            });
        });
    }
    internal void ApplyCustomOffer(Guid id, string before, string document, string proposed)
    {
        var row = Subtitles.FirstOrDefault(r => r.Id == id);
        if (row == null || !CustomProject.CanApply(before, row.Text ?? "", id, row.Id, document, _subtitleFileName ?? "")) throw new InvalidOperationException("הכתובית השתנתה מאז הבדיקה. הפעל בדיקה מחדש.");
        // Do not let a suggestion rewrite formatting metadata or smuggle new subtitle rows.
        string Tags(string text) => string.Join("\n", Regex.Matches(text, @"<(?:[^<>""']|""[^""]*""|'[^']*')+>|\{\\[^}]*\}").Select(m => m.Value));
        if (Tags(before) != Tags(proposed) || proposed.Contains("-->")) throw new InvalidDataException("ההצעה שינתה תגיות או מבנה כתוביות ולכן לא הוחלה.");
        var beforeSnapshot = MakeUndoRedoObject("לפני הצעת תרגום שאושרה");
        if (_undoRedoManager.UndoList.LastOrDefault()?.Hash != beforeSnapshot.Hash) _undoRedoManager.Do(beforeSnapshot);
        var change = BeginToolApply("הצעת תרגום שאושרה"); EndToolApply(change, () => row.Text = proposed);
        RefreshOstMarkers();
    }
    private async Task FindProjectTerms()
    {
        if (!EnsureCustomProject() || ActiveProfile == null) { ShowStatus("בחר פרויקט תחילה."); return; }
        var profile = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(ActiveProfile))!; var document = _customDocument;
        var documentHash = GetFastHash(); var generation = _ostDocumentGeneration;
        var snapshots = Subtitles.Where(r => !r.IsReferenceOnly && !r.IsComment && !r.IsDefault).Select(r => (Text: r.Text ?? "", Start: r.StartTime.TotalMilliseconds, End: r.EndTime.TotalMilliseconds)).ToArray();
        var text = string.Join("\n", snapshots.Select(r => r.Text));
        var reference = JsonSerializer.Deserialize<ProjectWork>(JsonSerializer.Serialize(_customWork))!;
        await ProjectDialog("אתר שמות ומונחים בקובץ", (p, w) =>
        {
            p.Children.Add(Label("פרויקט: " + profile.Name + ". הסריקה מזהה שמות אוטומטית מתוך המדריך והפרק ומסמנת ליד הכתוביות: ✓ תואם למדריך; ✕ חשד AI לכתיב שונה מהמדריך; ? שם ללא כתיב מוסכם. המילון והטקסט לא ישתנו. הסריקה בתשלום API, מחולקת למקטעים ונעצרת בסגירת החלון."));
            p.Children.Add(Label($"פרקי רקע מסומנים: {profile.PreviousEpisodes?.Count(e => e.IncludeInAi) ?? 0}; קטעים רלוונטיים יצורפו לסריקה."));
            var results = new StackPanel { Spacing = 8 }; p.Children.Add(results); var cts = new CancellationTokenSource(); w.Closed += (_, _) => cts.Cancel();
            ActionButton(p, "זהה שמות מהמדריך ומהפרק וסמן ברשימה (Gemini API)", async () =>
            {
                var key = CustomWindowsIntegration.ReadKey(); if (key.Length == 0) throw new InvalidOperationException("הגדר מפתח Gemini תחילה.");
                var found = new Dictionary<string, ProjectTerm>(StringComparer.Ordinal); using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                var lines = text.Split('\n');
                var englishCues = CustomProject.ReadSrt(reference.ReferenceText);
                for (var i = 0; i < snapshots.Length; i += 80)
                {
                    var group = snapshots.Skip(i).Take(80).ToArray();
                    var chunk = string.Join("\n", group.Select(r => r.Text));
                    var sourceContext = englishCues.Where(c => reference.Map(c.End) >= group[0].Start && reference.Map(c.Start) <= group[^1].End).Take(100).Select(c => c.Text).ToArray();
                    var prompt = await Task.Run(() => CustomProject.BuildPrompt(profile, "Automatically identify person/place/organization names and recurring story terms from both TRANSLATION GUIDE and Hebrew episode text. For an entity explicitly named in the guide, preferred must be its exact Hebrew spelling copied verbatim from the guide; variants must be actual observed episode spellings. For an entity absent from the guide, preferred must be an observed episode spelling; do not claim it is correct. Suggest similar observed spellings cautiously, do not merge distinct entities. Do not invent names or variants. Return {\"terms\":[{\"preferred\":\"exact guide spelling if available, otherwise observed Hebrew spelling\",\"source\":\"\",\"variants\":[\"observed variant\"],\"kind\":\"Hebrew category\"}]}. English reference is tentative context; different timing/cuts can mismatch. Do not invent matches.", JsonSerializer.Serialize(new { hebrew = chunk, englishReference = sourceContext })), cts.Token);
                    RequireEpisodeContext(profile);
                    var json = await CustomProject.AskAsync(http, key, prompt, cts.Token, GeminiModel());
                    foreach (var term in CustomProject.Terms(json).Where(t => CustomQc.ContainsName(text, t.Preferred) || t.Variants.Any(v => CustomQc.ContainsName(text, v))))
                    {
                        term.Variants = term.Variants.Where(v => CustomQc.ContainsName(text, v) && v != term.Preferred).ToArray();
                        if (found.TryGetValue(term.Preferred, out var existing)) existing.Variants = existing.Variants.Concat(term.Variants).Distinct().ToArray(); else found.Add(term.Preferred, term);
                    }
                    ShowStatus($"סריקת שמות ומונחים: {Math.Min(i + 80, snapshots.Length)} מתוך {snapshots.Length}");
                }
                cts.Token.ThrowIfCancellationRequested();
                if (document != _subtitleFileName || generation != _ostDocumentGeneration || documentHash != GetFastHash() ||
                    ActiveProfile?.Id != profile.Id || ActiveProfile.Guide != profile.Guide)
                    throw new InvalidOperationException("הכתוביות או מדריך הפרויקט השתנו בזמן הסריקה; הפעל שוב. לא הוחלו סימונים ישנים.");
                RequireEpisodeContext(profile);
                SetDetectedNameSuggestions(found.Values.Select(t => new QcTerm(t.Preferred, t.Variants)));
                results.Children.Clear();
                results.Children.Add(Label("סומנו שמות ברשימה הראשית לפי המדריך והפרק. ✕ הוא חשד AI לבדיקה; ? אינו שגיאה. עריכת הכתוביות או המדריך מבטלת תוצאות ישנות. אפשר לאשר מועמד למילון לשימוש קבוע, בלי להקליד אותו מחדש."));
                foreach (var term in found.Values)
                {
                    var examples = lines.Where(l => CustomQc.ContainsName(l, term.Preferred) || term.Variants.Any(v => CustomQc.ContainsName(l, v))).Take(4);
                    results.Children.Add(Label(term.Kind + ": " + term.Preferred + "\nחלופות שנצפו: " + string.Join(", ", term.Variants) + "\n" + string.Join("\n", examples)));
                    ActionButton(results, "בדוק ואשר למילון: " + term.Preferred, async () => { if (document != _subtitleFileName || ActiveProfile?.Id != profile.Id) throw new InvalidOperationException("הפרויקט הפעיל השתנה."); RequireEpisodeContext(profile); await EditTerm(term, true); });
                }
                if (found.Count == 0) results.Children.Add(Label("לא נמצאו מועמדים. אין זו הוכחה שאין שמות או מונחים בקובץ."));
            });
        });
    }
    private Task EditProjectNote() => EditProjectNoteCore(false);
    private async Task EditProjectNoteCore(bool forDirector)
    {
        if (!EnsureCustomProject()) return; var row = SubtitleGridSelectedItems.FirstOrDefault(); if (row == null) return;
        var document = _customDocument; var generation = _ostDocumentGeneration; var n = CustomProject.FindNote(_customWork, row.Id, row.Text ?? "", row.StartTime.TotalMilliseconds, row.EndTime.TotalMilliseconds);
        await ProjectDialog("הערה פנימית ומצב בדיקה", (p, w) =>
        {
            p.Children.Add(Label(row.Number + ": " + row.Text)); var note = Field(n?.Note ?? "", true); p.Children.Add(note);
            var director = new CheckBox { Name = "DirectorQuestionToggle", Content = Label("לבדוק עם הבמאי — הצג בטבלת השאלות"), IsChecked = forDirector || n?.DirectorQuestion == true }; p.Children.Add(director);
            var answer = Field(n?.DirectorAnswer ?? "", true); p.Children.Add(Label("תשובת הבמאי (אופציונלי)")); p.Children.Add(answer);
            var resolved = new CheckBox { Content = Label("השאלה לבמאי טופלה — ללא אישור כל הכתובית"), IsChecked = n?.DirectorResolved == true }; p.Children.Add(resolved);
            var review = new CheckBox { Content = Label("דורש בדיקה"), IsChecked = n?.NeedsReview ?? false }; p.Children.Add(review);
            var reviewed = new CheckBox { Content = Label("נבדק"), IsChecked = n?.Reviewed ?? false }; p.Children.Add(reviewed);
            review.IsCheckedChanged += (_, _) => { if (review.IsChecked == true) reviewed.IsChecked = false; };
            reviewed.IsCheckedChanged += (_, _) => { if (reviewed.IsChecked == true) review.IsChecked = false; };
            var exception = new CheckBox { Content = Label("חריגה מכוונת מהמילון בשורה זו"), IsChecked = n?.ExceptionText == row.Text && !string.IsNullOrWhiteSpace(n?.ExceptionReason) }; p.Children.Add(exception);
            var reason = Field(n?.ExceptionReason ?? ""); p.Children.Add(Label("סיבת החריגה — נדרשת כדי להשתיק התראה על הנוסח הזה")); p.Children.Add(reason);
            ActionButton(p, "שמור הערה ומצב בלבד", () => { if (_subtitleFileName != document || generation != _ostDocumentGeneration || !Subtitles.Contains(row)) throw new InvalidOperationException("הקובץ הפעיל השתנה."); if (director.IsChecked == true && string.IsNullOrWhiteSpace(note.Text)) throw new InvalidDataException("כתוב שאלה לבמאי לפני השמירה."); if (exception.IsChecked == true && string.IsNullOrWhiteSpace(reason.Text)) throw new InvalidDataException("כתוב סיבה לחריגה."); n ??= new WorkNote { Id = row.Id }; if (!_customWork.Notes.Contains(n)) _customWork.Notes.Add(n); n.Text = row.Text ?? ""; n.Start = row.StartTime.TotalMilliseconds; n.End = row.EndTime.TotalMilliseconds; n.Note = note.Text ?? ""; n.DirectorQuestion = director.IsChecked == true; n.DirectorAnswer = answer.Text ?? ""; n.DirectorResolved = resolved.IsChecked == true; n.NeedsReview = review.IsChecked == true; n.Reviewed = reviewed.IsChecked == true; _customNoteCache[row.Id] = n; n.ExceptionText = exception.IsChecked == true ? row.Text ?? "" : ""; n.ExceptionReason = exception.IsChecked == true ? reason.Text ?? "" : ""; SaveCustomWork(); MonitorCustomProject(); w.Close(); return Task.CompletedTask; });
        });
    }
    private void NavigateProjectReview(int direction)
    {
        if (!EnsureCustomProject()) return; var rows = Subtitles.Where(r => CustomProject.FindNote(_customWork, r.Id, r.Text ?? "", r.StartTime.TotalMilliseconds, r.EndTime.TotalMilliseconds)?.NeedsReview == true).ToList();
        if (rows.Count == 0) { ShowStatus("אין שורות שסומנו לבדיקה."); return; }
        var current = SubtitleGridSelectedItems.FirstOrDefault(); var index = current == null ? -1 : Subtitles.IndexOf(current);
        var next = direction > 0 ? rows.FirstOrDefault(r => Subtitles.IndexOf(r) > index) ?? rows[0] : rows.LastOrDefault(r => Subtitles.IndexOf(r) < index) ?? rows[^1];
        _customOstOnly = false; MonitorCustomProject(); SubtitleGrid.SelectedItem = next; SubtitleGrid.ScrollIntoView(next);
    }
    private Task CheckProjectTerms()
    {
        if (!EnsureCustomProject() || ActiveProfile == null) return Task.CompletedTask;
        var profile = ActiveProfile;
        return ProjectDialog("בדיקת עקביות מול מילון הפרויקט", (p, w) =>
        {
            var count = 0; var hits = 0;
            foreach (var row in Subtitles.Where(r => !r.IsReferenceOnly))
            {
                hits += profile.Terms.Sum(t => CustomProject.CountWord(row.Text ?? "", t.Preferred));
                var n = CustomProject.FindNote(_customWork, row.Id, row.Text ?? "", row.StartTime.TotalMilliseconds, row.EndTime.TotalMilliseconds);
                if (n?.ExceptionText == row.Text && !string.IsNullOrWhiteSpace(n?.ExceptionReason)) continue;
                foreach (var term in profile.Terms)
                    foreach (var variant in term.Variants.Where(v => v != term.Preferred && !string.IsNullOrWhiteSpace(v) && CustomProject.ContainsWord(row.Text ?? "", v)))
                    {
                        count++; var before = row.Text ?? ""; var document = _customDocument;
                        p.Children.Add(Label($"שורה {row.Number}: {before}\n{variant} ← {term.Preferred}"));
                        ActionButton(p, "עבור לשורה " + row.Number, () => { _customOstOnly = false; MonitorCustomProject(); SubtitleGrid.SelectedItem = row; SubtitleGrid.ScrollIntoView(row); return Task.CompletedTask; });
                        ActionButton(p, "החל תיקון בשורה זו בלבד", () => { ApplyCustomOffer(row.Id, before, document, CustomProject.ReplaceTerm(before, variant, term.Preferred)); w.Close(); return Task.CompletedTask; });
                    }
            }
            p.Children.Insert(0, Label($"נבדקו {profile.Terms.Count} שמות ומונחים מאושרים; {hits} הופעות של הנוסח המאושר. " + (count == 0 ? "לא נמצאו חריגות מול המילון. שמות שלא הוגדרו אינם מכוסים בבדיקה." : $"נמצאו {count} חריגות לבדיקה.")));
        });
    }
    private Task SearchOstRows()
    {
        if (!EnsureOstProject()) return Task.CompletedTask;
        RefreshOstMarkers();
        return ProjectDialog("חיפוש בשורות OST", (p, w) =>
        {
            var query = Field(); p.Children.Add(Label("חפש בכיתוב שנכלל ב־OST")); p.Children.Add(query);
            var results = new StackPanel { Spacing = 8 }; p.Children.Add(results);
            void Refresh()
            {
                results.Children.Clear();
                foreach (var row in Subtitles.Where(r => r.IsInOst && (r.Text ?? "").Contains(query.Text ?? "", StringComparison.OrdinalIgnoreCase)).Take(100))
                    ActionButton(results, row.Number + ": " + row.Text, () => { SubtitleGrid.SelectedItem = row; SubtitleGrid.ScrollIntoView(row); w.Close(); return Task.CompletedTask; });
            }
            query.TextChanged += (_, _) => Refresh(); Refresh();
        });
    }
    private Task ProjectDeliveryReport()
    {
        if (!EnsureCustomProject()) return Task.CompletedTask;
        ScanCustomQc();
        return ProjectDialog("דוח מסירה קצר", (p, w) =>
        {
            var profile = ActiveProfile;
            var warnings = Subtitles.Where(r => !r.IsReferenceOnly).Where(r => { var note = CustomProject.FindNote(_customWork, r.Id, r.Text ?? "", r.StartTime.TotalMilliseconds, r.EndTime.TotalMilliseconds); return note?.ExceptionText != r.Text || string.IsNullOrWhiteSpace(note?.ExceptionReason); }).SelectMany(r => profile == null ? Array.Empty<string>() : CustomProject.TermWarnings(profile, r.Text ?? "").Select(t => $"שורה {r.Number}: {t}").ToArray()).ToList();
            var pending = Subtitles.Count(r => CustomProject.FindNote(_customWork, r.Id, r.Text ?? "", r.StartTime.TotalMilliseconds, r.EndTime.TotalMilliseconds)?.NeedsReview == true);
            var report = $"קובץ: {Path.GetFileName(_subtitleFileName)}\nפרויקט: {profile?.Name ?? "לא שויך"}\nכתוביות: {Subtitles.Count(r => !r.IsReferenceOnly)}\nשורות שסומנו לבדיקה: {pending}\nהתראות טכניות: {CustomQcIssues.Count}\nחריגות מילון: {warnings.Count}\n\n" + string.Join("\n", CustomQcIssues.Select(i => $"שורה {i.Number}: {i.Message}").Concat(warnings));
            p.Children.Add(Label(report));
            p.Children.Add(Label("זה דוח של הבדיקות המוגדרות; הוא אינו אישור שכל התרגום נכון."));
            ActionButton(p, "שמור דוח טקסט…", async () => { var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "שמור דוח מסירה", SuggestedFileName = Path.GetFileNameWithoutExtension(_subtitleFileName) + "-QC.txt" }); if (file != null) await File.WriteAllTextAsync(file.Path.LocalPath, report); });
        });
    }
    private Task OpenSettingsBackups() => CustomSettingsUi.Show(Window!, () => ApplySettings());
    private Task EditOstDetails()
    {
        if (!EnsureOstProject()) return Task.CompletedTask;
        var details = _ostView!.GetDetails();
        return ProjectDialog("כותרת ופרטי OST", (p, w) =>
        {
            var title = Field(details.Title); var season = Field(details.Season); var episode = Field(details.Episode); var preview = Label("");
            void Preview() => preview.Text = string.Join(" — ", new[] { title.Text, string.IsNullOrWhiteSpace(season.Text) ? "" : "עונה " + season.Text, string.IsNullOrWhiteSpace(episode.Text) ? "" : "פרק " + episode.Text, "OST" }.Where(s => !string.IsNullOrWhiteSpace(s)));
            title.TextChanged += (_, _) => Preview(); season.TextChanged += (_, _) => Preview(); episode.TextChanged += (_, _) => Preview();
            p.Children.Add(Label("שם הפרויקט")); p.Children.Add(title); p.Children.Add(Label("עונה (אופציונלי)")); p.Children.Add(season); p.Children.Add(Label("פרק (אופציונלי)")); p.Children.Add(episode); p.Children.Add(Label("הכותרת ב־Word ושם הקובץ המוצע")); p.Children.Add(preview); Preview();
            ActionButton(p, "שמור", () => { _ostView.SetDetails(title.Text ?? "", season.Text ?? "", episode.Text ?? ""); if (!_ostView.TrySave()) throw new IOException("פרטי OST לא נשמרו. בדוק הרשאת כתיבה."); w.Close(); return Task.CompletedTask; });
        });
    }
    private void ToggleCustomOstFilter()
    {
        _customOstOnly = !_customOstOnly; RefreshOstMarkers(); MonitorCustomProject();
        if (_customOstOnly && SubtitleGridSelectedItems.Any(r => !r.IsInOst)) { SubtitleGrid.SelectedItems?.Clear(); SubtitleGrid.SelectedItem = Subtitles.FirstOrDefault(r => r.IsInOst && !r.IsHidden); }
        ShowStatus(_customOstOnly ? "מוצגות שורות OST בלבד. לחזרה: OST → הצג הכול." : "מוצגות כל הכתוביות.", 6000);
    }
}




