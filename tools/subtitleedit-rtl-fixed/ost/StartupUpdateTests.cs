using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Features.Help.CheckForUpdates;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Logic.Config;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    private static async Task<CheckForUpdatesWindow> AwaitStartupUpdateWindow(Window owner)
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (owner.OwnedWindows.OfType<CheckForUpdatesWindow>().FirstOrDefault() is { } dialog) return dialog;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The startup update dialog did not open.");
    }

    [AvaloniaFact]
    public async Task StartupUpdateOpensRealDialogOnceWithoutAnotherNetworkCheckOrAutomaticInstall()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var enabled = Se.Settings.General.CheckForUpdatesOnStartup;
        Se.Settings.General.CheckForUpdatesOnStartup = true;
        var owner = new Window { Width = 1500, Height = 900 };
        MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main; owner.Show();
        var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork();
        var checks = 0;
        var result = new UpdateCheckResult { IsNewVersionAvailable = true, LatestVersion = "startup-" + Guid.NewGuid(), ChangeLogText = "synthetic custom update" };
        Task<UpdateCheckResult> Check() { checks++; return Task.FromResult(result); }
        try
        {
            var pending = vm.CustomCheckForUpdatesAtStartupAsync(Check, _ => Task.CompletedTask);
            var dialog = await AwaitStartupUpdateWindow(owner);
            var updateVm = Assert.IsType<CheckForUpdatesViewModel>(dialog.DataContext);
            Assert.True(updateVm.IsInstallVisible);
            Assert.Equal(result.ChangeLogText, updateVm.ChangeLogText);
            Assert.False(updateVm.IsDownloading);
            Assert.False(CustomUpdateChannel.IsInstallPending);
            await vm.CustomCheckForUpdatesAtStartupAsync(Check, _ => Task.CompletedTask);
            Assert.Equal(1, checks); // other windows/startup callbacks cannot compete with this dialog
            dialog.Close(); await pending;
            await vm.CustomCheckForUpdatesAtStartupAsync(Check, _ => Task.CompletedTask);
            Assert.Empty(owner.OwnedWindows.OfType<CheckForUpdatesWindow>());
            Assert.True(Se.Settings.General.CheckForUpdatesOnStartup);
        }
        finally
        {
            foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close();
            Se.Settings.General.CheckForUpdatesOnStartup = enabled;
        }
    }

    [AvaloniaFact]
    public async Task StartupUpdateDefersBehindModalAndDoesNotOpenForClosedOwner()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var enabled = Se.Settings.General.CheckForUpdatesOnStartup;
        Se.Settings.General.CheckForUpdatesOnStartup = true;
        var owner = new Window { Width = 1500, Height = 900 };
        MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main; owner.Show();
        var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork();
        var blocker = new Window(); var modal = blocker.ShowDialog(owner);
        var waiting = new TaskCompletionSource(); var resume = new TaskCompletionSource();
        Task Delay(TimeSpan duration)
        {
            if (duration.TotalSeconds == 15) return Task.CompletedTask;
            waiting.TrySetResult(); return resume.Task;
        }
        var result = new UpdateCheckResult { IsNewVersionAvailable = true, LatestVersion = "deferred-" + Guid.NewGuid() };
        try
        {
            var pending = vm.CustomCheckForUpdatesAtStartupAsync(() => Task.FromResult(result), Delay);
            await waiting.Task;
            Assert.Empty(owner.OwnedWindows.OfType<CheckForUpdatesWindow>());
            blocker.Close(); await modal; resume.SetResult();
            var dialog = await AwaitStartupUpdateWindow(owner); dialog.Close(); await pending;
            owner.Close();
            var checks = 0;
            await vm.CustomCheckForUpdatesAtStartupAsync(() => { checks++; return Task.FromResult(result); }, _ => Task.CompletedTask);
            Assert.Equal(0, checks);
        }
        finally
        {
            resume.TrySetResult(); blocker.Close(); foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close();
            Se.Settings.General.CheckForUpdatesOnStartup = enabled;
        }
    }

    [AvaloniaFact]
    public async Task StartupUpdateRespectsDisabledPreferenceAndHandlesNoUpdateAndOffline()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var enabled = Se.Settings.General.CheckForUpdatesOnStartup;
        var owner = new Window { Width = 1500, Height = 900 };
        MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main; owner.Show();
        var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork(); var checks = 0;
        Task<UpdateCheckResult> Check() { checks++; return Task.FromResult(new UpdateCheckResult()); }
        try
        {
            Se.Settings.General.CheckForUpdatesOnStartup = false;
            await vm.CustomCheckForUpdatesAtStartupAsync(Check, _ => Task.CompletedTask);
            Assert.Equal(0, checks);
            Se.Settings.General.CheckForUpdatesOnStartup = true;
            await vm.CustomCheckForUpdatesAtStartupAsync(Check, _ => Task.CompletedTask);
            Assert.Equal(1, checks);
            await vm.CustomCheckForUpdatesAtStartupAsync(() => throw new InvalidOperationException("offline fixture"), _ => Task.CompletedTask);
            Assert.Empty(owner.OwnedWindows.OfType<CheckForUpdatesWindow>());
            Assert.False(CustomUpdateChannel.IsInstallPending);
        }
        finally { owner.Close(); Se.Settings.General.CheckForUpdatesOnStartup = enabled; }
    }
}
