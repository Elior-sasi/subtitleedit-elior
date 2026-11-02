using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    internal void ApplyNiqqudRemoval(IReadOnlyDictionary<Guid, string> originals, string document, int generation, int hash)
    {
        if (document != (_subtitleFileName ?? "") || generation != _ostDocumentGeneration || hash != GetFastHash())
            throw new InvalidOperationException("המסמך השתנה. סרוק מחדש לפני הסרת ניקוד.");
        var rows = originals.Select(p => Subtitles.FirstOrDefault(r => r.Id == p.Key)).ToArray();
        if (rows.Any(r => r == null || r.IsReferenceOnly || r.IsDefault || r.IsComment || originals[r.Id] != r.Text))
            throw new InvalidOperationException("שורת יעד השתנתה או אינה שורת עבודה.");
        if (!rows.Any(r => HebrewNiqqud.Count(r!.Text ?? "") > 0)) return;
        var change = BeginProToolApply("הסרת ניקוד באישור");
        EndToolApply(change, () => { foreach (var row in rows) row!.Text = HebrewNiqqud.Remove(row.Text ?? ""); });
        RefreshOstMarkers();
    }
    private async Task OpenNiqqudRemoval()
    {
        Nikse.SubtitleEdit.Logic.TableViewExtras.SyncSelectedItemsWithSelection(SubtitleGrid);
        var selected = SubtitleGridSelectedItemsWithReference.Where(r => !r.IsReferenceOnly && !r.IsDefault && !r.IsComment).Select(r => r.Id).ToHashSet();
        var document = _subtitleFileName ?? ""; var generation = _ostDocumentGeneration; var hash = GetFastHash();
        var captured = Subtitles.Where(r => !r.IsReferenceOnly && !r.IsDefault && !r.IsComment).Select(r => new { r.Id, r.Number, Before = r.Text ?? "" }).ToArray();
        if (captured.Length > 10000 || captured.Sum(r => (long)r.Before.Length) > 1000000) throw new InvalidOperationException("המסמך גדול מדי לבדיקה זו (עד 10,000 שורות ומיליון תווים).");
        await ProjectDialog("זיהוי והסרת ניקוד — ללא AI", (panel, window) =>
        {
            window.Name = "NiqqudRemovalDialog";
            panel.Children.Add(Label("מסיר נקודות ניקוד עבריות בלבד: תנועות, דגש/מפיק, מתג, רפה ונקודות שין/שׂין. טעמי מקרא, מקף עברי, סוף פסוק, אותיות, פיסוק, רווחים, סימני כיוון ותגיות עיצוב נשמרים. אין שינוי עד ללחיצה על הסר; אפשר לשחזר ב־Undo."));
            var all = new CheckBox { Content = Label("כל שורות העבודה — במקום הבחירה בלבד"), IsChecked = false }; panel.Children.Add(all);
            var preview = Field("", true); preview.IsReadOnly = true; preview.Name = "NiqqudRemovalPreview"; panel.Children.Add(preview);
            var status = Label(""); status.Name = "NiqqudRemovalStatus"; panel.Children.Add(status);
            var checkRows = captured.Where(r => HebrewNiqqud.Count(r.Before) > 0).ToArray();
            void Update()
            {
                var targets = checkRows.Where(r => all.IsChecked == true || selected.Contains(r.Id)).ToArray();
                status.Text = $"זוהו {targets.Length} שורות עם {targets.Sum(r => HebrewNiqqud.Count(r.Before))} סימני ניקוד בהיקף הנבחר.";
                preview.Text = string.Join("\n\n", targets.Take(100).Select(r => $"כתובית {r.Number}\nלפני: {r.Before}\nאחרי: {HebrewNiqqud.Remove(r.Before)}"));
                if (targets.Length > 100) preview.Text += "\n\nהתצוגה מציגה 100 שורות ראשונות; האישור חל על כל השורות שנספרו.";
            }
            all.IsCheckedChanged += (_, _) => Update(); Update();
            ActionButton(panel, "הסר ניקוד בהיקף המוצג", () =>
            {
                var targets = checkRows.Where(r => all.IsChecked == true || selected.Contains(r.Id)).ToDictionary(r => r.Id, r => r.Before);
                ApplyNiqqudRemoval(targets, document, generation, hash); window.Close(); return Task.CompletedTask;
            });
            ActionButton(panel, "סגור ללא שינוי", () => { window.Close(); return Task.CompletedTask; });
        });
    }
}
