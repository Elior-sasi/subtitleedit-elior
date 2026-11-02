using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Net.Http;
using Nikse.SubtitleEdit.Core.Common;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.UiLogic.AutoTranslate;

namespace Nikse.SubtitleEdit.Features.Translate;

public partial class AutoTranslateViewModel
{
    private ProjectProfile? _customGuideProfile;
    [ObservableProperty] private bool _customGuideAvailable;
    [ObservableProperty] private bool _customGuideEnabled;
    [ObservableProperty] private string _customGuideStatus = "ללא מדריך פרויקט. לשימוש במדריך בחר פרויקט דרך פרויקט ← מילון והנחיות הפרויקט, ואז פתח את התרגום מחדש.";

    internal void ConfigureCustomGuide(ProjectProfile? profile)
    {
        _customGuideProfile = profile == null ? null : JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(profile));
        CustomGuideAvailable = _customGuideProfile != null;
        CustomGuideEnabled = CustomGuideAvailable;
        RefreshCustomGuideStatus();
    }
    partial void OnCustomGuideEnabledChanged(bool value) => RefreshCustomGuideStatus();
    private void RefreshCustomGuideStatus() => CustomGuideStatus = _customGuideProfile == null
        ? "ללא מדריך פרויקט. לשימוש במדריך בחר פרויקט דרך פרויקט ← מילון והנחיות הפרויקט, ואז פתח את התרגום מחדש."
        : CustomGuideEnabled
            ? "המדריך פעיל: " + _customGuideProfile.Name + ". הוראות, מילון וקטעי רקע רלוונטיים יצורפו לבקשות Gemini במודל שנבחר בחלון. לחיצה על אישור תבצע ביקורת נוספת בתשלום באותו מודל, במקטעים של עד 40 שורות, על הנוסח הסופי שמוצג בחלון. חריגה, אי־ודאות או ביקורת חסרה חוסמות את ההצעה; אין הבטחת דיוק מוחלט. תרגום נשלח בלחיצה על תרגם; ביקורת נשלחת בלחיצה על אישור."
            : "המדריך כבוי: התרגום יישלח בלי מדריך הפרויקט, המילון ופרקי הרקע.";

    internal Control MakeCustomGuideStrip()
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(10, 5), Spacing = 4, FlowDirection = FlowDirection.LeftToRight };
        var check = new CheckBox { Name = "CustomNativeGuideEnabled", Content = new TextBlock { Text = "השתמש במדריך הפרויקט בתרגום האוטומטי", FlowDirection = FlowDirection.RightToLeft },
            [!CheckBox.IsCheckedProperty] = new Binding(nameof(CustomGuideEnabled)) { Mode = BindingMode.TwoWay },
            [!Control.IsEnabledProperty] = new Binding(nameof(CustomGuideAvailable)) };
        // A running request must keep the choice it started with.
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(IsTranslateEnabled) or nameof(CustomGuideAvailable)) check.IsEnabled = CustomGuideAvailable && IsTranslateEnabled; };
        Avalonia.Automation.AutomationProperties.SetName(check, "השתמש במדריך הפרויקט בתרגום האוטומטי");
        panel.Children.Add(check);
        panel.Children.Add(new TextBlock { Name = "CustomNativeGuideStatus", TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.RightToLeft,
            [!TextBlock.TextProperty] = new Binding(nameof(CustomGuideStatus)) });
        return panel;
    }

    internal bool TryConfigureCustomGuideEngine(IAutoTranslator translator, out string error)
    {
        foreach (var gemini in AutoTranslators.OfType<GeminiTranslate>()) gemini.CustomGuidePrompt = null;
        error = "";
        if (!CustomGuideEnabled || _customGuideProfile == null) return true;
        if (translator is not GeminiTranslate selected)
        {
            error = "מדריך הפרויקט פעיל, אך החיבור למדריך נתמך כאן רק במנוע Google Gemini. בחר Google Gemini בחלון זה, או בטל במפורש את השימוש במדריך כדי לתרגם בלעדיו.";
            return false;
        }
        var snapshot = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(_customGuideProfile))!;
        try { GuideVerification.Rules(snapshot); }
        catch (InvalidDataException e) { error = e.Message; return false; }
        selected.CustomGuidePrompt = (nativePrompt, text) => CustomNativeTranslation.BuildPrompt(snapshot, nativePrompt, text);
        return true;
    }
    private bool _customGuideAuditBusy;
    internal async Task<bool> ApproveCustomGuideResults()
    {
        if (!CustomGuideEnabled || _customGuideProfile == null) return true;
        if (_customGuideAuditBusy || _translationInProgress || _translationStarting) { StatusText = "המתן לסיום התרגום או הביקורת."; return false; }
        if (SelectedAutoTranslator is not GeminiTranslate gemini) { StatusText = "ביקורת המדריך דורשת Google Gemini, או כיבוי מפורש של המדריך."; return false; }
        if ((ModelText ?? "").Trim() != Configuration.Settings.Tools.GeminiModel || (ApiKeyText ?? "").Trim() != (Configuration.Settings.Tools.GeminiProApiKey ?? "").Trim())
        { StatusText = "המודל או המפתח בחלון השתנו מאז התרגום. הפעל תרגום עם ההגדרות שבחרת ואז אשר לביקורת."; return false; }
        var snapshot = Rows.Select((row, index) => (Row: row, Source: row.Text ?? "", Translation: row.TranslatedText ?? "", Key: "row" + (index + 1))).ToArray();
        var candidates = snapshot.Where(x => !string.IsNullOrWhiteSpace(x.Translation)).Select(x => new GuideCandidate(x.Key, x.Source, x.Translation)).ToArray();
        if (candidates.Length == 0) { StatusText = "אין הצעות לתרגום לבדיקה."; return false; }
        var profile = _customGuideProfile;
        var model = Configuration.Settings.Tools.GeminiModel; var modelText = ModelText; var keyText = ApiKeyText;
        bool Fresh() => CustomGuideEnabled && ReferenceEquals(_customGuideProfile, profile) && ReferenceEquals(SelectedAutoTranslator, gemini) && model == Configuration.Settings.Tools.GeminiModel && ModelText == modelText && ApiKeyText == keyText &&
            Rows.Count == snapshot.Length && snapshot.Select((s, i) => ReferenceEquals(Rows[i], s.Row) && Rows[i].Text == s.Source && (Rows[i].TranslatedText ?? "") == s.Translation).All(v => v);
        _customGuideAuditBusy = true; IsTranslateEnabled = false;
        if (_cancellationTokenSource.IsCancellationRequested) _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        try
        {
            GuideVerification.Rules(profile);
            for (var offset = 0; offset < candidates.Length; offset += 40)
            {
                token.ThrowIfCancellationRequested();
                if (!Fresh()) throw new InvalidOperationException("התוצאות או המודל השתנו בזמן הביקורת. בדוק ואשר מחדש.");
                StatusText = $"ביקורת מדריך בתשלום: שורות {offset + 1}–{Math.Min(offset + 40, candidates.Length)} מתוך {candidates.Length}. אפשר לבטל.";
                var batch = candidates.Skip(offset).Take(40).ToArray();
                var report = await GuideVerification.ReviewAsync(profile, batch, gemini.RequestCustomGuideReview, token);
                token.ThrowIfCancellationRequested();
                if (!Fresh()) throw new InvalidOperationException("התוצאות או המודל השתנו בזמן הביקורת. לא הוחלו כתוביות.");
                var problems = batch.Select(c => (c.Key, Problem: report.Problem(c.Key))).Where(c => c.Problem != null).ToArray();
                if (problems.Length > 0)
                {
                    CustomGuideStatus = "הביקורת לא אישרה את ההצעות; הן נשארו בחלון לעריכה. אין שכתוב אוטומטי.\n" + string.Join("\n", problems.Select(c => c.Key.Replace("row", "שורה ") + ": " + c.Problem));
                    StatusText = "נמצאו חריגות או אי־ודאות. לא הוחל תרגום. ערוך ובדוק שוב; כל אישור נוסף יוצר בקשת ביקורת נוספת בתשלום.";
                    return false;
                }
            }
            CustomGuideStatus = "לא אותרה חריגה בביקורת המודל על הנוסח הסופי. זו אינה הבטחת דיוק; נדרשת בדיקה שלך.";
            return true;
        }
        catch (OperationCanceledException) { StatusText = "ביקורת המדריך בוטלה. לא הוחל תרגום."; return false; }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or InvalidOperationException or JsonException or System.Collections.Generic.KeyNotFoundException)
        { StatusText = "ביקורת המדריך לא הושלמה; לא הוחל תרגום. " + e.Message; return false; }
        finally { _customGuideAuditBusy = false; IsTranslateEnabled = true; }
    }

    private async Task<bool> PrepareCustomGuideTranslation(IAutoTranslator translator)
    {
        if (TryConfigureCustomGuideEngine(translator, out var error)) return true;
        StatusText = error;
        await MessageBox.Show(Window!, "מדריך הפרויקט בתרגום אוטומטי", error, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }
}
