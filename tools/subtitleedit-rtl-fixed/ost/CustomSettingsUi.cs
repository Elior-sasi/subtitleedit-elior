using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;
internal static class CustomSettingsUi
{
    internal static Nikse.SubtitleEdit.Features.Options.Settings.SettingsPage? FindPage(Window window) =>
        window.GetVisualDescendants().OfType<Nikse.SubtitleEdit.Features.Options.Settings.SettingsPage>().FirstOrDefault();

    internal static Control Wrap(Control content, Window owner, Action reloaded)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(8) };
        var backup = new Button { Content = "גבה הגדרות עכשיו", FontSize = 16 };
        var restore = new Button { Content = "שחזר הגדרות…", FontSize = 16 };
        var status = new TextBlock { FontSize = 16, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.RightToLeft };
        backup.Click += (_, _) => { try { var path = CustomWindowsIntegration.BackupSettings(); status.Text = "נשמר גיבוי: " + Path.GetFileNameWithoutExtension(path); } catch (Exception e) { status.Text = e.Message; } };
        restore.Click += async (_, _) => await Show(owner, reloaded);
        buttons.Children.Add(backup); buttons.Children.Add(restore);
        var advance = new CheckBox {
            Name = "CustomAutoAdvanceOnSetEnd",
            Content = new TextBlock { Text = "עבור לכתובית הבאה בקביעת זמן יציאה", FontSize = 16, FlowDirection = FlowDirection.RightToLeft, TextWrapping = TextWrapping.Wrap },
            IsChecked = Se.Settings.Tools.AutoAdvanceOnSetEnd,
            Margin = new Thickness(8), FlowDirection = FlowDirection.RightToLeft
        };
        Avalonia.Automation.AutomationProperties.SetName(advance, "עבור לכתובית הבאה בקביעת זמן יציאה");
        ToolTip.SetTip(advance, "חל על פעולת קביעת סוף הכתובית ממיקום הנגן, בבחירה של שורה אחת. ההעדפה נשמרת מיד; עריכה ידנית של שדה הזמן אינה מעבירה שורה.");
        advance.IsCheckedChanged += (_, _) => {
            var previous = Se.Settings.Tools.AutoAdvanceOnSetEnd;
            try { Se.Settings.Tools.AutoAdvanceOnSetEnd = advance.IsChecked == true; Se.SaveSettings(); status.Text = "העדפת המעבר האוטומטי נשמרה."; }
            catch (Exception e) { Se.Settings.Tools.AutoAdvanceOnSetEnd = previous; status.Text = "שמירת ההעדפה נכשלה: " + e.Message; }
        };
        var bottom = new StackPanel { Children = { advance, buttons, status } }; var root = new DockPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(content); return root;
    }
    internal static Task Show(Window owner, Action reloaded)
    {
        var w = new Window { Title = "גיבוי ושחזור הגדרות", Width = 720, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var p = new StackPanel { Margin = new Thickness(18), Spacing = 12 }; var status = new TextBlock { Text = "הגיבוי שומר את ההגדרות שנשמרו. לפני שחזור ייווצר גיבוי נוסף. קובצי העבודה לא ישתנו.", FontSize = 16, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.RightToLeft }; p.Children.Add(status);
        var list = new ComboBox { FontSize = 16, MinWidth = 400 }; p.Children.Add(list);
        void Refresh() { var folder = Path.Combine(Se.DataFolder, "SettingsBackups"); list.ItemsSource = Directory.Exists(folder) ? Directory.GetFiles(folder, "Settings-*.json").Select(Path.GetFileName).OrderByDescending(n => n).ToList() : new(); }
        void Button(string text, Action action) { var b = new Button { Content = text, FontSize = 16 }; b.Click += (_, _) => { try { action(); } catch (Exception e) { status.Text = e.Message; } }; p.Children.Add(b); }
        Button("גבה הגדרות עכשיו", () => { CustomWindowsIntegration.BackupSettings(); Refresh(); status.Text = "הגיבוי נשמר עם תאריך ושעה."; });
        Button("שחזר את הגיבוי הנבחר — ההגדרות הנוכחיות יגובו תחילה", () => { if (list.SelectedItem is not string path) { status.Text = "בחר גיבוי מהרשימה."; return; } CustomWindowsIntegration.RestoreSettings(Path.Combine(Se.DataFolder, "SettingsBackups", path)); reloaded(); w.Close(); });
        Button("סגור", () => w.Close()); Refresh(); w.Content = new ScrollViewer { Content = p }; return w.ShowDialog(owner);
    }
}

