using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Logic.Config;
using Xunit;

namespace UITests.Features.Main;

public partial class OstTableTests
{
    [AvaloniaFact]
    public void CustomRulesApplyKeepsExistingPlayerAndMainMenuOrder()
    {
        var (window, vm) = ProEditor();
        var old = Se.Settings.General.SubtitleLineMaximumLength;
        try
        {
            var player = window.GetVisualDescendants().OfType<Nikse.SubtitleEdit.Controls.VideoPlayer.VideoPlayerControl>().First();
            typeof(MainViewModel).GetField("_customPlayerSettingsBaseline", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, SettingsChangeSnapshot.Take());
            Se.Settings.General.SubtitleLineMaximumLength = old + 1;
            vm.ApplySettings();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(player, window.GetVisualDescendants().OfType<Nikse.SubtitleEdit.Controls.VideoPlayer.VideoPlayerControl>().First());
            var menus = vm.Menu.Items.OfType<MenuItem>().ToList();
            Assert.NotEqual("ProFrameMainMenu", menus[0].Name);
            Assert.NotEqual("CustomProjectMainMenu", menus[0].Name);
            Assert.True(menus.FindIndex(x => x.Name == "ProFrameMainMenu") < menus.FindIndex(x => x.Name == "CustomProjectMainMenu"));
        }
        finally { Se.Settings.General.SubtitleLineMaximumLength = old; window.Close(); }
    }

    private static (Window Window, MainViewModel Vm) ProEditor()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window;
        var main = new MainView(); window.Content = main; window.Show(); Dispatcher.UIThread.RunJobs();
        var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
        vm.Subtitles.Clear(); return (window, vm);
    }
    private static SubtitleLineViewModel ProRow(int number, double start, double end, string text = "<i>שלום!</i>") => new() { Number = number, Text = text, StartTime = TimeSpan.FromSeconds(start), EndTime = TimeSpan.FromSeconds(end) };
    private static int ProGeneration(MainViewModel vm) => (int)typeof(MainViewModel).GetField("_ostDocumentGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    private static void ProSelect(MainViewModel vm, params SubtitleLineViewModel[] rows)
    {
        vm.SubtitleGrid.SelectedItems.Clear(); foreach (var row in rows) vm.SubtitleGrid.SelectedItems.Add(row);
        vm.SubtitleGrid.SelectedItem = rows.FirstOrDefault(); Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(25, 40)]
    [InlineData(23.976, 42)]
    [InlineData(29.97, 33)]
    [InlineData(50, 20)]
    [InlineData(60, 17)]
    public void ProFrameMovesUseNativeFrameRatePreserveTextAndUndo(double fps, int milliseconds)
    {
        var (window, vm) = ProEditor(); var oldFps = Se.Settings.General.CurrentFrameRate;
        var oldShorten = Se.Settings.General.MoveLinesShortenNeighbor;
        try
        {
            Se.Settings.General.CurrentFrameRate = fps; Se.Settings.General.MoveLinesShortenNeighbor = false;
            var row = ProRow(1, 10, 12); vm.Subtitles.Add(row); ProSelect(vm, row);
            var id = row.Id; vm.ExecuteProFrame("cue", 1);
            Assert.Equal(TimeSpan.FromMilliseconds(10000 + milliseconds), row.StartTime);
            Assert.Equal(TimeSpan.FromMilliseconds(12000 + milliseconds), row.EndTime);
            Assert.Equal(id, row.Id); Assert.Equal("<i>שלום!</i>", row.Text);
            vm.UndoCommand.Execute(null); Assert.Equal(TimeSpan.FromSeconds(10), vm.Subtitles[0].StartTime); Assert.Equal(TimeSpan.FromSeconds(12), vm.Subtitles[0].EndTime);
        }
        finally { window.Close(); Se.Settings.General.CurrentFrameRate = oldFps; Se.Settings.General.MoveLinesShortenNeighbor = oldShorten; }
    }

    [AvaloniaFact]
    public void ProFrameStartEndAreIndependentLockedAndVideoWithoutMediaIsNonDirty()
    {
        var (window, vm) = ProEditor(); var fps = Se.Settings.General.CurrentFrameRate;
        try
        {
            Se.Settings.General.CurrentFrameRate = 25;
            var row = ProRow(1, 10, 12); vm.Subtitles.Add(row); ProSelect(vm, row);
            vm.ExecuteProFrame("start", 1); Assert.Equal(TimeSpan.FromMilliseconds(10040), row.StartTime); Assert.Equal(TimeSpan.FromSeconds(12), row.EndTime);
            vm.ExecuteProFrame("end", -1); Assert.Equal(TimeSpan.FromMilliseconds(10040), row.StartTime); Assert.Equal(TimeSpan.FromMilliseconds(11960), row.EndTime);
            var hash = vm.GetFastHash(); vm.LockTimeCodes = true;
            foreach (var target in new[] { "start", "end", "cue", "video" }) vm.ExecuteProFrame(target, 1);
            Assert.Equal(hash, vm.GetFastHash()); vm.LockTimeCodes = false;
            Assert.Throws<ArgumentOutOfRangeException>(() => vm.ExecuteProFrame("cue", 2));
        }
        finally { window.Close(); Se.Settings.General.CurrentFrameRate = fps; }
    }

    [AvaloniaFact]
    public void ProRealFrameMenuHasExplicitTargetsRtlTextAndNativeClickAction()
    {
        var (window, vm) = ProEditor(); var fps = Se.Settings.General.CurrentFrameRate;
        try
        {
            Se.Settings.General.CurrentFrameRate = 25; var row = ProRow(1, 10, 12); vm.Subtitles.Add(row); ProSelect(vm, row);
            var menu = Assert.Single(vm.Menu.Items.OfType<MenuItem>().Where(m => m.Name == "ProFrameMainMenu"));
            Assert.Single(vm.Menu.Items.OfType<MenuItem>().Where(m => m.Name == "CustomProjectMainMenu"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "ProFrameToolbarButton" || b.Name == "CustomProjectToolbarButton");
            menu.IsSubMenuOpen = true; Dispatcher.UIThread.RunJobs();
            var items = menu.Items.OfType<MenuItem>().ToArray();
            var item = Assert.Single(items.Where(i => i.Name == "ProStartFrameForward"));
            Assert.Equal(FlowDirection.LeftToRight, item.FlowDirection);
            Assert.Equal(FlowDirection.RightToLeft, Assert.IsType<TextBlock>(item.Header).FlowDirection);
            Assert.Equal("כניסה: פריים מאוחר יותר", AutomationProperties.GetName(item));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.Equal(TimeSpan.FromMilliseconds(10040), row.StartTime);
            menu.IsSubMenuOpen = false;
        }
        finally { window.Close(); Se.Settings.General.CurrentFrameRate = fps; }
    }

    [AvaloniaFact]
    public void ProReviewCheckpointFindsOnlyActualChangesAndCannotCrossDocuments()
    {
        var (window, vm) = ProEditor();
        try
        {
            var a = ProRow(1, 1, 2); var b = ProRow(2, 3, 4); vm.Subtitles.Add(a); vm.Subtitles.Add(b);
            Assert.Throws<InvalidOperationException>(() => vm.GetProReviewRows("changed"));
            var hash = vm.GetFastHash(); vm.SetProReviewBaseline(); Assert.Equal(hash, vm.GetFastHash()); Assert.Empty(vm.GetProReviewRows("changed"));
            a.Text = "שינוי"; Assert.Same(a, Assert.Single(vm.GetProReviewRows("changed")));
            a.Text = "<i>שלום!</i>"; b.SetTimes(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4.1)); Assert.Same(b, Assert.Single(vm.GetProReviewRows("changed")));
            typeof(MainViewModel).GetField("_ostDocumentGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, ProGeneration(vm) + 1);
            Assert.Throws<InvalidOperationException>(() => vm.GetProReviewRows("changed"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ProShotApplyChangesOnlyApprovedTimesHasUndoAndRejectsStaleOrLocked()
    {
        var (window, vm) = ProEditor();
        try
        {
            var row = ProRow(1, 10, 12); var other = ProRow(2, 15, 17); vm.Subtitles.Add(row); vm.Subtitles.Add(other);
            var proposal = new ProTimingProposal(row.Id, row.Text, row.StartTime, row.EndTime, TimeSpan.FromSeconds(9.9), TimeSpan.FromSeconds(12.1));
            var hash = vm.GetFastHash(); var gen = ProGeneration(vm);
            vm.LockTimeCodes = true; Assert.Throws<InvalidOperationException>(() => vm.ApplyProTiming(new[] { proposal }, gen, hash)); vm.LockTimeCodes = false;
            Assert.Throws<InvalidOperationException>(() => vm.ApplyProTiming(new[] { proposal }, gen + 1, hash));
            row.Text = "changed"; Assert.Throws<InvalidOperationException>(() => vm.ApplyProTiming(new[] { proposal }, gen, hash)); row.Text = proposal.Text;
            vm.ApplyProTiming(new[] { proposal }, gen, vm.GetFastHash());
            Assert.Equal(proposal.Start, row.StartTime); Assert.Equal(proposal.End, row.EndTime); Assert.Equal(proposal.Text, row.Text); Assert.Equal(TimeSpan.FromSeconds(15), other.StartTime);
            vm.UndoCommand.Execute(null); Assert.Equal(proposal.BeforeStart, vm.Subtitles[0].StartTime); Assert.Equal(proposal.BeforeEnd, vm.Subtitles[0].EndTime);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ProShotRejectsNewNonAdjacentOverlapAndInvalidTimeAtomically()
    {
        var (window, vm) = ProEditor();
        try
        {
            var outer = ProRow(1, 0, 10); var nested = ProRow(2, 1, 2); var row = ProRow(3, 12, 14);
            vm.Subtitles.Add(outer); vm.Subtitles.Add(nested); vm.Subtitles.Add(row);
            var p = new ProTimingProposal(row.Id, row.Text, row.StartTime, row.EndTime, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(14));
            var hash = vm.GetFastHash(); var gen = ProGeneration(vm);
            Assert.Throws<InvalidOperationException>(() => vm.ApplyProTiming(new[] { p }, gen, hash)); Assert.Equal(hash, vm.GetFastHash());
            Assert.Throws<InvalidOperationException>(() => vm.ApplyProTiming(new[] { p with { Start = TimeSpan.FromSeconds(-1) } }, gen, hash));
            Assert.Throws<InvalidOperationException>(() => vm.ApplyProTiming(new[] { p with { Start = p.End } }, gen, hash));
            Assert.Equal(hash, vm.GetFastHash());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ProWidthScanUsesNativeShapingWithoutTurningOnColumnsOrChangingText()
    {
        var oldColumn = Se.Settings.General.ShowColumnPixelWidth; var oldColor = Se.Settings.General.ColorTextTooWide;
        try
        {
            Se.Settings.General.ShowColumnPixelWidth = false; Se.Settings.General.ColorTextTooWide = false;
            var row = ProRow(1, 1, 2, "<i>שלום עולם</i>\nHello"); var text = row.Text;
            var width = row.MeasureProPixelWidth(); Assert.True(width > 0); Assert.Equal(text, row.Text);
            Assert.False(Se.Settings.General.ShowColumnPixelWidth); Assert.False(Se.Settings.General.ColorTextTooWide);
            Se.Settings.General.ShowColumnPixelWidth = true; Assert.Equal(row.PixelWidth, width);
        }
        finally { Se.Settings.General.ShowColumnPixelWidth = oldColumn; Se.Settings.General.ColorTextTooWide = oldColor; }
    }

    [AvaloniaFact]
    public async Task ProReviewAndWidthDialogsOpenAndCancelWithoutChangingDocument()
    {
        var (window, vm) = ProEditor();
        try
        {
            vm.Subtitles.Add(ProRow(1, 1, 2)); var hash = vm.GetFastHash();
            foreach (var entry in new[] { (Method: "OpenProFocusedReview", Title: "סקירה ממוקדת — ניגון בלי הרווחים בין הכתוביות"), (Method: "OpenProWidthReview", Title: "בדיקת רוחב לפי גופן ואזור בטוח") })
            {
                var task = (Task)typeof(MainViewModel).GetMethod(entry.Method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
                Dispatcher.UIThread.RunJobs();
                var dialog = Assert.Single(window.OwnedWindows.Where(w => w.Title == entry.Title));
                Assert.Equal(FlowDirection.LeftToRight, dialog.FlowDirection);
                dialog.Close(); Dispatcher.UIThread.RunJobs(); await task;
                Assert.Equal(hash, vm.GetFastHash());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProAssistedSplitAndMoveRespectTimeLockWithoutOpeningDialog()
    {
        var (window, vm) = ProEditor();
        try
        {
            var row = ProRow(1, 1, 4, "משפט ארוך מספיק לבדיקה של פיצול טקסט");
            vm.Subtitles.Add(row); vm.Subtitles.Add(ProRow(2, 5, 7)); ProSelect(vm, row);
            var hash = vm.GetFastHash(); var count = window.OwnedWindows.Count;
            vm.LockTimeCodes = true;
            await vm.ExecuteProAssistedEdit(false); await vm.ExecuteProAssistedEdit(true);
            Assert.Equal(hash, vm.GetFastHash()); Assert.Equal(count, window.OwnedWindows.Count);
            vm.LockTimeCodes = false;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void VideoControlsHaveTwoFrameArrowsAfterPlayAndWaveformHasNone()
    {
        var (window, vm) = ProEditor(); var rtl = Se.Settings.Appearance.RightToLeft;
        var toolbar = vm.IsWaveformToolbarVisible; var locked = vm.LockTimeCodes;
        try
        {
            vm.IsWaveformToolbarVisible = true; vm.IsVideoLoaded = false;
            vm.Subtitles.Add(ProRow(1, 1, 2)); var hash = vm.GetFastHash();
            // The waveform keeps its own controls only; the arrows moved to the video player.
            window.Content = Nikse.SubtitleEdit.Features.Main.Layout.InitWaveform.MakeWaveform(vm);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Name is "CustomWaveformFrameControls" or "CustomVideoFrameControls"));

            var video = new Nikse.SubtitleEdit.Controls.VideoPlayer.VideoPlayerControl(new Nikse.SubtitleEdit.Controls.VideoPlayer.EmptyVideoPlayer());
            video.CustomFrameControls = vm.MakeVideoFrameControls();
            window.Content = video; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var pair = Assert.IsType<StackPanel>(video.CustomFrameControls);
            Assert.Equal("CustomVideoFrameControls", pair.Name);
            Assert.Equal(2, pair.Children.Count);
            var back = Assert.IsType<Nikse.SubtitleEdit.Controls.NonSpaceButton>(pair.Children[0]);
            var forward = Assert.IsType<Nikse.SubtitleEdit.Controls.NonSpaceButton>(pair.Children[1]);
            Assert.Same(vm.VideoOneFrameBackCommand, back.Command); Assert.Same(vm.VideoOneFrameForwardCommand, forward.Command);
            Assert.Equal("וידאו: פריים אחד אחורה", AutomationProperties.GetName(back));
            Assert.Equal("וידאו: פריים אחד קדימה", AutomationProperties.GetName(forward));
            Assert.False(back.IsEnabled); Assert.False(forward.IsEnabled);
            var grid = Assert.IsType<Grid>(pair.Parent);
            var play = grid.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == Se.Language.General.Play);
            Assert.Equal(Grid.GetColumn(play) + 1, Grid.GetColumn(pair));

            // A hidden Play stays hidden; the arrows keep its slot.
            var items = SeVideoControlsItem.MakeDefaults();
            items.Single(i => i.Type == SeVideoControlsItemType.Play).IsVisible = false;
            video.ApplyControlsLayout(items); window.UpdateLayout();
            Assert.DoesNotContain((Control)play, grid.Children);
            Assert.Contains((Control)pair, grid.Children.Cast<Control>());
            video.ApplyControlsLayout(SeVideoControlsItem.MakeDefaults()); window.UpdateLayout();

            foreach (var direction in new[] { false, true, false })
            {
                Se.Settings.Appearance.RightToLeft = direction;
                Nikse.SubtitleEdit.Features.Main.MainHelpers.RightToLeftHelper.SetRightToLeftForDataGridAndText(window);
                window.UpdateLayout();
                Assert.Equal(FlowDirection.LeftToRight, pair.FlowDirection);
                Assert.Equal("←", Assert.IsType<TextBlock>(back.Content).Text);
                Assert.Equal("→", Assert.IsType<TextBlock>(forward.Content).Text);
                Assert.True(back.Bounds.X < forward.Bounds.X);
            }
            vm.LockTimeCodes = true; back.Command?.Execute(null); forward.Command?.Execute(null);
            Assert.Equal(hash, vm.GetFastHash()); vm.LockTimeCodes = false;
            video.CloseAndDisposePlayer();
        }
        finally { vm.IsWaveformToolbarVisible = toolbar; vm.LockTimeCodes = locked; Se.Settings.Appearance.RightToLeft = rtl; window.Close(); }
    }
}

