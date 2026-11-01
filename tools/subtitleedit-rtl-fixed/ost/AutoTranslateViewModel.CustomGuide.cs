using System;
using System.Linq;
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
            ? "המדריך פעיל: " + _customGuideProfile.Name + ". הוראות, מילון וקטעי רקע רלוונטיים יצורפו לבקשות Gemini במודל שנבחר בחלון. שליחה רק בלחיצה על תרגם."
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
        selected.CustomGuidePrompt = (nativePrompt, text) => CustomNativeTranslation.BuildPrompt(snapshot, nativePrompt, text);
        return true;
    }
    private async Task<bool> PrepareCustomGuideTranslation(IAutoTranslator translator)
    {
        if (TryConfigureCustomGuideEngine(translator, out var error)) return true;
        StatusText = error;
        await MessageBox.Show(Window!, "מדריך הפרויקט בתרגום אוטומטי", error, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }
}
