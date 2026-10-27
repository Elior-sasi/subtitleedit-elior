using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;
public partial class MainViewModel
{
    private ProjectBackupSettings? _projectBackupSettings;
    private DateTime _nextProjectBackup;
    private bool _projectBackupRunning;
    private string _projectBackupHash = "";
    private string _projectBackupStatus = "טרם נוצר גיבוי לתיקייה המסונכרנת.";
    private string ProjectBackupSettingsPath => Path.Combine(Se.DataFolder, "CustomProjects", "backup-folder.json");
    private ProjectBackupSettings BackupSettings => _projectBackupSettings ??= CustomProject.Load<ProjectBackupSettings>(ProjectBackupSettingsPath);
    private async Task<string?> PickBackupFolder(string title)
    {
        if (Window == null) return null;
        var selected = await Window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return selected.FirstOrDefault()?.Path.LocalPath;
    }
    private Task OpenProjectBackup() => ProjectDialog("גיבוי לתיקיית ענן מסונכרנת", (p, w) =>
    {
        p.Children.Add(Label("בחר תיקייה קיימת שמסונכרנת באמצעות Google Drive או OneDrive. ניצור עותקים מתוארכים; שירות הענן אחראי להעלאה. יצירת גיבוי כאן אינה אישור שההעלאה הושלמה. אין העלאה של מפתח Gemini או של קובצי וידאו."));
        p.Children.Add(Label("הגיבוי כולל עותק SRT מהעריכה הנוכחית, טבלת OST, הערות ומדריך הפרויקט עם זיכרון התרגום. קובץ המקור ושמירתו הרגילה אינם משתנים. ב־ASS, SSA ו־VTT נשמר גם עותק בפורמט המקורי; בפורמטים אחרים זהו גיבוי SRT ללא כל העיצוב המקורי."));
        var folder = Label(BackupSettings.Folder.Length == 0 ? "לא נבחרה תיקייה" : BackupSettings.Folder); p.Children.Add(folder);
        var status = Label(_projectBackupStatus); p.Children.Add(status);
        var automatic = new CheckBox { Content = Label("גבה שינויים אוטומטית בכל דקה בזמן שהמסמך פתוח"), IsChecked = BackupSettings.Automatic }; p.Children.Add(automatic);
        ActionButton(p, "בחר תיקייה מסונכרנת…", async () => { var path = await PickBackupFolder("בחר תיקיית ענן מסונכרנת"); if (path == null) return; BackupSettings.Folder = path; CustomProject.Save(ProjectBackupSettingsPath, BackupSettings); folder.Text = path; _projectBackupHash = ""; });
        ActionButton(p, "שמור העדפת גיבוי אוטומטי", () => { if (automatic.IsChecked == true && !Directory.Exists(BackupSettings.Folder)) throw new InvalidDataException("בחר תיקייה זמינה תחילה."); BackupSettings.Automatic = automatic.IsChecked == true; CustomProject.Save(ProjectBackupSettingsPath, BackupSettings); _nextProjectBackup = DateTime.UtcNow; status.Text = BackupSettings.Automatic ? "הגיבוי האוטומטי הופעל. בדוק את חיווי הסנכרון בשירות הענן." : "הגיבוי האוטומטי כבוי."; return Task.CompletedTask; });
        ActionButton(p, "גבה את הפרויקט עכשיו", async () => { await BackupProjectNow(true); status.Text = _projectBackupStatus; });
        ActionButton(p, "שחזר גיבוי לתיקייה חדשה…", async () => { var zip = await PickFile("בחר גיבוי ZIP שנוצר כאן"); if (zip == null) return; var destination = await PickBackupFolder("בחר היכן ליצור תיקיית שחזור חדשה"); if (destination == null) return; var offset = Se.Settings.General.CurrentVideoOffsetInMs; var restored = await Task.Run(() => ProjectArchive.RestoreSnapshot(zip, destination, offset)); status.Text = "שוחזר לתיקייה חדשה: " + restored + ". פתח מתוכה את קובץ הכתוביות בפורמט שבו עבדת, או subtitles.srt. את המדריך אפשר לייבא דרך תפריט הפרויקט."; });
    });
    private async Task ImportArchivedGuide()
    {
        if (!EnsureCustomProject()) return;
        var path = await PickFile("בחר guide-and-memory.json מתוך גיבוי ששוחזר"); if (path == null) return;
        if (new FileInfo(path).Length > 20_000_000) throw new InvalidDataException("קובץ מדריך גדול מדי.");
        var imported = CustomProject.Load<ProjectProfile>(path); var document = _customDocument;
        if (string.IsNullOrWhiteSpace(imported.Name) || imported.Terms == null || imported.Memory == null) throw new InvalidDataException("קובץ מדריך לא תקין.");
        imported.PreviousEpisodes = ProjectEpisodeContext.Merge(Array.Empty<EpisodeContextDocument>(), imported.PreviousEpisodes ?? new());
        await ProjectDialog("אישור ייבוא מדריך וזיכרון", (p, w) => {
            p.Children.Add(Label(imported.Name + "\nמונחים: " + imported.Terms.Count + " · תרגומים מאושרים: " + imported.Memory.Count + " · פרקי רקע: " + imported.PreviousEpisodes.Count + "\nפרקי רקע שסומנו ייכללו בהקשר של פעולות AI שתפעיל.\n" + imported.Guide));
            ActionButton(p, "ייבא כפרויקט חדש ושייך לקובץ הנוכחי", () => { if (_subtitleFileName != document) throw new InvalidOperationException("הקובץ הפעיל השתנה."); imported.Id = Guid.NewGuid().ToString("N"); imported.Alias = ""; imported.Folder = ""; _customProfiles.Add(imported); SaveCustomProfiles(); _customWork.ProfileId = imported.Id; SaveCustomWork(); w.Close(); return Task.CompletedTask; });
        });
    }
    private void MonitorProjectBackup()
    {
        if (_projectBackupRunning || DateTime.UtcNow < _nextProjectBackup) return;
        _nextProjectBackup = DateTime.UtcNow.AddMinutes(1);
        try { if (BackupSettings.Automatic && !string.IsNullOrWhiteSpace(_customDocument)) _ = BackupProjectNow(false); }
        catch (Exception e) { ShowStatus("הגיבוי לתיקייה המסונכרנת לא בוצע: " + e.Message, 10000); }
    }
    internal async Task BackupProjectNow(bool force)
    {
        if (_projectBackupRunning) { if (force) ShowStatus("גיבוי קודם עדיין מתבצע."); return; }
        _projectBackupRunning = true;
        try
        {
            if (!EnsureCustomProject() || !EnsureOstProject()) return;
            MonitorCustomProject();
            var folder = BackupSettings.Folder; var document = _customDocument;
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("בחר תיקיית גיבוי זמינה.");
            var videoOffset = Se.Settings.General.CurrentVideoOffsetInMs;
            var rows = Subtitles.Where(r => !r.IsReferenceOnly && !r.IsDefault && !r.IsComment).ToArray();
            if (rows.Any(r => r.StartTime + TimeSpan.FromMilliseconds(videoOffset) < TimeSpan.Zero || r.EndTime <= r.StartTime)) throw new InvalidDataException("תקן זמני כתוביות לא תקינים לפני גיבוי SRT.");
            var srt = string.Join("\n\n", rows.Select((r, i) => (i + 1) + "\n" + (r.StartTime + TimeSpan.FromMilliseconds(videoOffset)).ToString(@"hh\:mm\:ss\,fff") + " --> " + (r.EndTime + TimeSpan.FromMilliseconds(videoOffset)).ToString(@"hh\:mm\:ss\,fff") + "\n" + (r.Text ?? ""))) + "\n";
            var files = new Dictionary<string, string> { ["subtitles.srt"] = srt, ["project.sework.json"] = JsonSerializer.Serialize(_customWork) };
            var extension = SelectedSubtitleFormat.Extension.ToLowerInvariant();
            if (new[] { ".ass", ".ssa", ".vtt" }.Contains(extension)) files["subtitles" + extension] = GetSaveSubtitle().ToText(SelectedSubtitleFormat);
            if (ActiveProfile != null) files["guide-and-memory.json"] = JsonSerializer.Serialize(ActiveProfile);
            if (_ostView != null) files["table.ostproject"] = _ostView.ArchiveSnapshot();
            var hash = document + ProjectArchive.Fingerprint(files); if (!force && hash == _projectBackupHash) return;
            var result = await Task.Run(() => ProjectArchive.WriteSnapshot(folder, document, files, videoOffset));
            _projectBackupHash = hash; _projectBackupStatus = "נוצר עותק בתיקייה ב־" + DateTime.Now.ToString("HH:mm:ss") + ": " + result + ". מצב ההעלאה זמין בשירות הענן.";
            if (force) ShowStatus("נוצר גיבוי בתיקייה; בדוק את השלמת הסנכרון בשירות הענן.", 8000);
        }
        catch (Exception e) { _projectBackupStatus = "הגיבוי נכשל: " + e.Message; ShowStatus(_projectBackupStatus, 10000); }
        finally { _projectBackupRunning = false; }
    }
    private void AddMemoryMatches(StackPanel panel, string english, Guid id, string before, string document, ProjectProfile profile, Window window)
    {
        var matches = ProjectArchive.Find(profile.Memory, english);
        if (matches.Count == 0) { panel.Children.Add(Label("לא נמצאה התאמה בזיכרון המאושר של הפרויקט. אפשר לאשר צמדי מקור ותרגום דרך בדיקה מול המקור.")); return; }
        foreach (var match in matches)
        {
            var entry = match.Entry;
            panel.Children.Add(Label((match.Similarity == 1 ? "מקור זהה לאחר נרמול רווחים ופיסוק" : "מקור דומה — נדרשת בדיקת ההקשר") + " · " + entry.Document + "\nמקור: " + entry.English + "\nתרגום קודם: " + entry.Hebrew));
            ActionButton(panel, "החל את התרגום הקודם על השורה שנבחרה בלבד", () => { ApplyCustomOffer(id, before, document, entry.Hebrew); window.Close(); return Task.CompletedTask; });
        }
    }
    private Task SearchTranslationMemory()
    {
        if (!EnsureCustomProject() || ActiveProfile == null) { ShowStatus("בחר פרויקט תחילה."); return Task.CompletedTask; }
        var row = SubtitleGridSelectedItems.FirstOrDefault(); if (row == null) { ShowStatus("בחר כתובית בתקציר תחילה."); return Task.CompletedTask; }
        var before = row.Text ?? ""; var document = _customDocument; var profile = ActiveProfile;
        return ProjectDialog("משפט חוזר / זיכרון תרגום", (p, w) =>
        {
            p.Children.Add(Label("הדבק את המשפט באנגלית או בחר אותו דרך בדיקה מול המקור. החיפוש אינו תלוי בזמני הפרק. נשמרים רק צמדי מקור ותרגום שאישרת; משפט דומה אינו הוכחה לאותה משמעות."));
            var source = Field("", true); source.FlowDirection = Avalonia.Media.FlowDirection.LeftToRight; p.Children.Add(source);
            var results = new StackPanel { Spacing = 10 }; p.Children.Add(results);
            ActionButton(p, "חפש בתרגומים שאישרתי בפרויקט", () => { results.Children.Clear(); AddMemoryMatches(results, source.Text ?? "", row.Id, before, document, profile, w); return Task.CompletedTask; });
        });
    }
}


