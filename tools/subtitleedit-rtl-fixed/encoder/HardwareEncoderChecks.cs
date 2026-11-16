using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Nikse.SubtitleEdit.Features.Video.BurnIn;

internal static class HardwareEncoderChecks
{
    // Called by the burn-in window's background worker, never on its UI thread.
    internal static IReadOnlySet<string> Check(string executable, IReadOnlySet<string> advertised)
    {
        var usable = new HashSet<string>(advertised, StringComparer.Ordinal);
        foreach (var codec in new[] { "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf" }.Where(advertised.Contains))
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=640x360:rate=24", "-frames:v", "1", "-an", "-c:v", codec, "-pix_fmt", "yuv420p", "-f", "null", "-" })
                info.ArgumentList.Add(arg);
            try
            {
                using var proc = Process.Start(info);
                if (proc == null) continue;
                var diagnostic = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(8000))
                {
                    if (!proc.HasExited) proc.Kill(true);
                    continue; // Timeout is inconclusive.
                }
                if (proc.ExitCode == 0) continue;
                var error = diagnostic.GetAwaiter().GetResult();
                if (error.Contains("No such filter", StringComparison.OrdinalIgnoreCase) || error.Contains("Unknown input format", StringComparison.OrdinalIgnoreCase)) continue;
                usable.Remove(codec);
                Nikse.SubtitleEdit.Logic.Config.Se.WriteToolsLog($"Burn-in: {codec} failed a real encoder initialization: {error.Trim()}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                Nikse.SubtitleEdit.Logic.Config.Se.WriteToolsLog($"Burn-in: could not verify {codec}: {ex.Message}");
            }
        }
        return usable;
    }
}
