using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Features.Help.CheckForUpdates;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private static readonly SemaphoreSlim CustomStartupUpdateGate = new(1, 1);
    // Process-only: dismissing a prompt never changes the user's saved preferences.
    private static readonly HashSet<string> CustomStartupUpdatePrompts = new();

    internal async Task CustomCheckForUpdatesAtStartupAsync(
        Func<Task<UpdateCheckResult>> check, Func<TimeSpan, Task> delay)
    {
        if (!Se.Settings.General.CheckForUpdatesOnStartup ||
            !await CustomStartupUpdateGate.WaitAsync(0)) return;
        try
        {
            await delay(TimeSpan.FromSeconds(15));
            var owner = await Dispatcher.UIThread.InvokeAsync(() => Window);
            if (owner == null || !await Dispatcher.UIThread.InvokeAsync(() => owner.IsVisible)) return;
            var result = await check();
            if (!result.IsNewVersionAvailable || CustomUpdateChannel.IsInstallPending) return;
            var key = CustomUpdateChannel.AvailablePackage?.RunId.ToString() ?? result.LatestVersion;
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var state = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!owner.IsVisible || Window != owner || !Se.Settings.General.CheckForUpdatesOnStartup ||
                        CustomUpdateChannel.IsInstallPending || CustomStartupUpdatePrompts.Contains(key)) return -1;
                    UpdateAvailableText = string.Format(Se.Language.Help.CheckForUpdatesNewVersionAvailable, result.LatestVersion);
                    IsUpdateAvailable = true;
                    return owner.IsEnabled && owner.WindowState != WindowState.Minimized &&
                           !owner.OwnedWindows.Any(w => w.IsVisible) ? 1 : 0;
                });
                if (state < 0) return;
                if (state == 1)
                {
                    var opened = await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        // Recheck on the UI thread immediately before opening, with no intervening await.
                        if (!owner.IsVisible || !owner.IsEnabled || owner.WindowState == WindowState.Minimized ||
                            owner.OwnedWindows.Any(w => w.IsVisible) || !Se.Settings.General.CheckForUpdatesOnStartup ||
                            CustomUpdateChannel.IsInstallPending || !CustomStartupUpdatePrompts.Add(key)) return false;
                        // Use the update dialog directly: the generic editor-dialog wrapper pauses video.
                        await _windowService.ShowDialogAsync<CheckForUpdatesWindow, CheckForUpdatesViewModel>(
                            owner, vm => vm.UseStartupResult(result));
                        _shortcutManager.ClearKeys();
                        if (owner.IsVisible) RestoreFocusIfLost();
                        return true;
                    });
                    if (opened) return;
                }
                await delay(TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception ex)
        {
            // Offline/startup failures must not interrupt editing. Manual update checking remains available.
            Se.LogError(ex, "Custom startup update check");
        }
        finally { CustomStartupUpdateGate.Release(); }
    }
}
