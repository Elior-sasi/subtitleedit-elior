using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private IReadOnlyList<LocalNameHit> _customLocalNames = Array.Empty<LocalNameHit>();
    private int _customLocalNamesHash, _customLocalNamesGeneration;
    private string _customLocalNamesDocument = "", _customLocalNamesFingerprint = "";
    private CancellationTokenSource? _customLocalNamesCancel;
    private string LocalNamesFingerprint() => JsonSerializer.Serialize(new { Profile = ActiveProfile?.Id, Guide = ActiveProfile?.Guide, Terms = ActiveProfile?.Terms, Rules = Se.Settings.General.CurrentProfile, Glossary = CustomQcGlossary() });
    private List<QcTerm> LocalNamesTerms()
    {
        CustomQc.TryParseTerms(CustomQcGlossary(), out var terms, out _);
        if (ActiveProfile != null) terms.AddRange(ActiveProfile.Terms.Select(t => new QcTerm(t.Preferred, t.Variants ?? Array.Empty<string>())));
        return terms.Select(t => new QcTerm(t.Preferred, (t.Variants ?? Array.Empty<string>()).ToArray())).ToList();
    }
    internal void SetLocalNameSuggestions(LocalNameScan scan)
    {
        _customLocalNames = scan.Hits.ToArray(); _customLocalNamesHash = GetFastHash(); _customLocalNamesGeneration = _ostDocumentGeneration;
        _customLocalNamesDocument = _subtitleFileName ?? ""; _customLocalNamesFingerprint = LocalNamesFingerprint();
        _customQcDirty = true; ScanCustomQc();
    }
    internal IReadOnlyList<LocalNameHit> CurrentLocalNameSuggestions()
    {
        if (_customLocalNames.Count > 0 && (_customLocalNamesHash != GetFastHash() || _customLocalNamesGeneration != _ostDocumentGeneration ||
            _customLocalNamesDocument != (_subtitleFileName ?? "") || _customLocalNamesFingerprint != LocalNamesFingerprint()))
        { _customLocalNames = Array.Empty<LocalNameHit>(); _customQcDirty = true; }
        return _customLocalNames;
    }
    private void ClearLocalNameSuggestions(bool cancelRunning = true)
    { if (cancelRunning) _customLocalNamesCancel?.Cancel(); _customLocalNames = Array.Empty<LocalNameHit>(); _customQcDirty = true; ScanCustomQc(); }

    private async Task OpenLocalNameSuggestions()
    {
        using var cancel = new CancellationTokenSource(); _customLocalNamesCancel = cancel; var token = cancel.Token;
        try
        {
            await ProjectDialog("כתיב שונה בשמות — בדיקה מקומית ללא AI", (panel, window) =>
            {
                var sequence = 0;
                window.Name = "LocalNameSuggestionsDialog"; window.Closed += (_, _) => cancel.Cancel();
                var actions = PinProjectActions(window);
                panel.Children.Add(Label("בדיקה מקומית של כתיב שונה מול שמות ידועים. ללא AI וללא שינוי בכתוביות."));
                DialogDetails(panel, "איך הבדיקה עובדת?", Label("נבדק הבדל של אות אחת או חילוף אותיות מול מילון ושדות שם מפורשים במדריך, כגון Naruto = נארוטו. לא מחלצים שמות מטקסט חופשי ולא קובעים שזו אותה דמות או טעות. שמות קצרים ומילים נפוצות מסוננים בבדיקת הדמיון. ייתכנו החמצות והתראות שווא; עריכת המסמך או שינוי מדריך/מילון מבטלים תוצאות ישנות."));
                var status = Label("לחץ על סרוק מקומית כדי להתחיל."); status.Name = "LocalNameSuggestionsStatus"; panel.Children.Add(status);
                var results = new StackPanel { Spacing = 8 }; panel.Children.Add(results);
                var scanButton = ActionButton(actions, "בדוק את כל השמות והצג כתיב שונה", async () =>
                {
                    var request = ++sequence;
                    var document = _subtitleFileName ?? ""; var hash = GetFastHash(); var generation = _ostDocumentGeneration; var fingerprint = LocalNamesFingerprint();
                    var rows = Subtitles.Where(r => !r.IsReferenceOnly && !r.IsComment && !r.IsDefault).Select(r => new QcRow(r.Id, r.Number, r.Text, r.StartTime, r.EndTime, r.CharactersPerSecond)).ToArray();
                    var terms = LocalNamesTerms(); var guide = ActiveProfile?.Guide ?? "";
                    bool Fresh() => request == sequence && !token.IsCancellationRequested && document == (_subtitleFileName ?? "") && hash == GetFastHash() && generation == _ostDocumentGeneration && fingerprint == LocalNamesFingerprint();
                    results.Children.Clear(); ClearLocalNameSuggestions(cancelRunning: false);
                    // Clearing previous findings must not cancel this dialog's new scan.
                    status.Text = "סורק מקומית…";
                    var scan = await Task.Run(() => LocalNameSuggestions.Scan(rows, terms, guide, token), token);
                    token.ThrowIfCancellationRequested();
                    if (!Fresh()) throw new InvalidOperationException("המסמך, המדריך או המילון השתנו. סרוק מחדש; התוצאות הישנות לא הוצגו.");
                    SetLocalNameSuggestions(scan);
                    var inventory = Field(string.Join("\n", (scan.KnownNames ?? Array.Empty<LocalNameAnchor>()).Select(n => n.Text + (n.GuideOnly ? " — שדה מדריך; לא אישור מילוני" : " — מילון מאושר"))), true); inventory.IsReadOnly = true; inventory.Height = 160;
                    results.Children.Add(new Expander { Header = Label($"רשימת השמות והמונחים שנבדקה ({scan.Anchors}) — עיון"), Content = inventory });
                    status.Text = scan.Anchors == 0 ? "אין שמות מתאימים במילון או בשדות מדריך מפורשים. אפשר להוסיף שם במילון הקיים או שדה שם מפורש במדריך."
                        : $"נמצאו {scan.Hits.Count} מקרים של כתיב שונה לפי {scan.Anchors} שמות/מונחים. אין כאן קביעה שהכתיב שגוי." + (scan.Limited ? " מוצגות 500 התוצאות הראשונות בלבד; יתר הקובץ לא נבדק." : "");
                    foreach (var hit in scan.Hits)
                        ActionButton(results, $"כתובית {hit.Number}: {hit.Message}", () =>
                        {
                            if (!Fresh()) throw new InvalidOperationException("התוצאה התיישנה. סגור וסרוק מחדש.");
                            var row = Subtitles.FirstOrDefault(r => r.Id == hit.RowId); if (row == null) throw new InvalidOperationException("השורה אינה קיימת עוד.");
                            SubtitleGrid.SelectedItem = row; SubtitleGrid.ScrollIntoView(row); ShowStatus(hit.Message, 10000); window.Close(); return Task.CompletedTask;
                        });
                });
                scanButton.Name = "LocalNamesScan";
                ActionButton(actions, "נקה סימוני כתיב שונה מקומיים", () => { sequence++; _customLocalNames = Array.Empty<LocalNameHit>(); _customQcDirty = true; ScanCustomQc(); results.Children.Clear(); status.Text = "סימוני החשד המקומיים נוקו. הכתוביות והמילון לא שונו."; return Task.CompletedTask; });

            });
        }
        finally { if (ReferenceEquals(_customLocalNamesCancel, cancel)) _customLocalNamesCancel = null; }
    }
}
