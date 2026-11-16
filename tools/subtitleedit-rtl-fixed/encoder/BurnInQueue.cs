using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Features.Shared;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
namespace Nikse.SubtitleEdit.Features.Video.BurnIn;

public partial class BurnInViewModel
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _pauseBetweenJobs;
    private TaskCompletionSource? _queueResume;
    partial void OnPauseBetweenJobsChanged(bool value)
    {
        if (!value) _queueResume?.TrySetResult();
    }
    private void ReleaseQueuePause()
    {
        PauseBetweenJobs = false;
        _queueResume?.TrySetResult();
        _queueResume = null;
    }

    private string _reportedFps = "", _reportedSpeed = "", _reportedSize = "";
    private void ResetEncoderStats() { _reportedFps = _reportedSpeed = _reportedSize = ""; }
    private void ReadEncoderStats(string line)
    {
        int sep = line.IndexOf('=');
        if (sep < 1) return;
        var key = line[..sep];
        if (key is not ("fps" or "speed" or "total_size")) return;
        var raw = line[(sep+1)..].Trim().TrimEnd('x');
        if (!double.TryParse(raw,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value) || !double.IsFinite(value) || value < 0) return;
        if (key == "fps") _reportedFps = $" · {value:0.#} FPS";
        if (key == "speed") _reportedSpeed = $" · {value:0.##}×";
        if (key == "total_size") _reportedSize = $" · {value/1048576:0.0} MiB";
    }
    private string FormatEncoderStats(int percent, TimeSpan elapsed)
    {
        var queue = JobItems.Count <= 1 ? "" : $" · {QueueLabel("Queue (by file)", "תור (לפי קובץ)")} {(_jobItemIndex * 100d + percent) / JobItems.Count:0}%";
        return $" · {elapsed:hh\\:mm\\:ss}{_reportedFps}{_reportedSpeed}{_reportedSize}{queue}";
    }

    private sealed record QueueFile(int Version, QueueEntry[] Items);
    private sealed record QueueEntry(string Video, string Subtitle, int Width, int Height);
    internal static string QueueLabel(string en, string he) =>
        Se.Settings.General.Language.Contains("Hebrew", StringComparison.OrdinalIgnoreCase) || Se.Settings.General.Language.StartsWith("he", StringComparison.OrdinalIgnoreCase) ? he : en;

    internal static string SerializeQueue(System.Collections.Generic.IEnumerable<BurnInJobItem> items)
    {
            var doc = new QueueFile(1, items.Select(x => new QueueEntry(x.InputVideoFileName,x.SubtitleFileName,x.Width,x.Height)).ToArray());
            return JsonSerializer.Serialize(doc);
    }
    internal static BurnInJobItem[] DeserializeQueue(string json)
    {
            var doc = JsonSerializer.Deserialize<QueueFile>(json);
            if (doc?.Version != 1 || doc.Items is not { Length: > 0 and <= 10000 }) throw new InvalidDataException("Invalid queue");
            return doc.Items.Select(x => {
                if (!File.Exists(x.Video)) throw new FileNotFoundException(x.Video);
                if (!string.IsNullOrEmpty(x.Subtitle) && !File.Exists(x.Subtitle)) throw new FileNotFoundException(x.Subtitle);
                var job = new BurnInJobItem(x.Video,x.Width,x.Height);
                job.AddSubtitleFileName(x.Subtitle);
                return job;
            }).ToArray();
    }

    internal string AlternateSoftTrack { get; private set; } = "";
    private string SoftTrackFor(BurnInJobItem job) => SubtitleOutputMode == 2 && !IsBatchMode && AlternateSoftTrack.Length != 0 ? AlternateSoftTrack : job.SubtitleFileName;
    internal async Task PickSoftTrack()
    {
        if (IsGenerating || IsBatchMode) return;
        var paths = await _fileHelper.PickOpenFiles(Window!, QueueLabel("Select soft subtitle track", "בחירת רצועת כתוביות רכה"), "SRT/ASS/SSA", ["*.srt","*.ass","*.ssa"], Se.Language.General.AllFiles, ["*"]);
        if (!IsGenerating && paths?.FirstOrDefault() is string path) AlternateSoftTrack = path;
    }

    internal async Task ApplyEncoderProfile(int index)
    {
        if (IsGenerating || index < 0 || index > 3) return;
        var fast = index == 3;
        var encoder = fast ? VideoEncodings.FirstOrDefault(x => x.Codec == "h264_nvenc") : null;
        var fallback = fast && encoder == null;
        encoder ??= VideoEncodings.FirstOrDefault(x => x.Codec == "libx264");
        if (encoder == null || fallback)
        {
            await MessageBox.Show(Window!, Se.Language.General.Warning, encoder == null
                ? QueueLabel("H.264 encoder unavailable", "מקודד H.264 אינו זמין")
                : QueueLabel("NVENC unavailable. Using CPU x264, veryfast, CRF 22.", "NVENC אינו זמין. נבחר x264 במעבד, veryfast, CRF 22."), MessageBoxButtons.OK);
            if (encoder == null || IsGenerating) return;
        }
        SelectedVideoEncoding = encoder;
        VideoEncodingChanged();
        UseTargetFileSize = false;
        SelectedVideoPreset = index < 2 ? "slow" : index == 2 ? "medium" : fallback ? "veryfast" : "p4";
        SelectedVideoCrf = index == 0 ? "18" : index == 1 || (fast && !fallback) ? "20" : "22";
    }

    [RelayCommand]
    private void QueueUp() => MoveQueue(-1);
    [RelayCommand]
    private void QueueDown() => MoveQueue(1);
    private void MoveQueue(int offset)
    {
        if (IsGenerating || SelectedJobItem == null) return;
        var item = SelectedJobItem;
        var from = JobItems.IndexOf(item);
        if (from < 0 || from + offset < 0 || from + offset >= JobItems.Count) return;
        JobItems.Move(from, from + offset);
        SelectedJobItem = item;
    }

    [RelayCommand]
    private async Task SaveQueue()
    {
        if (IsGenerating || JobItems.Count == 0) return;
        var path = await _fileHelper.PickSaveFile(Window!, [("Subtitle Edit queue", ".sequeue")], "encode.sequeue", QueueLabel("Save queue", "שמירת תור"));
        if (string.IsNullOrEmpty(path) || IsGenerating) return;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!path.EndsWith(".sequeue", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Expected .sequeue");
            await File.WriteAllTextAsync(tmp, SerializeQueue(JobItems));
            File.Move(tmp,path,true);
        }
        catch (Exception ex) { await MessageBox.Show(Window!, Se.Language.General.Error, ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [RelayCommand]
    private async Task OpenQueue()
    {
        if (IsGenerating) return;
        var paths = await _fileHelper.PickOpenFiles(Window!, QueueLabel("Open queue", "פתיחת תור"), "Subtitle Edit queue", ["*.sequeue"], Se.Language.General.AllFiles, ["*"]);
        var path = paths?.FirstOrDefault();
        if (path == null || IsGenerating) return;
        try
        {
            if (new FileInfo(path).Length > 4000000) throw new InvalidDataException("Queue file too large");
            var jobs = DeserializeQueue(await File.ReadAllTextAsync(path));
            if (IsGenerating) return;
            if (JobItems.Count > 0 && await MessageBox.Show(Window!, Se.Language.General.Warning, QueueLabel("Replace current queue?", "להחליף את התור הנוכחי?"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != MessageBoxResult.Yes) return;
            if (IsGenerating) return;
            JobItems.Clear();
            foreach (var job in jobs) JobItems.Add(job);
            SelectedJobItem = jobs[0];
        }
        catch (Exception ex) { await MessageBox.Show(Window!, Se.Language.General.Error, ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
