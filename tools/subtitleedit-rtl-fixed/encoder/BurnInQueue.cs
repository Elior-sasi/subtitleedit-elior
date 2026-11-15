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
    private sealed record QueueFile(int Version, QueueEntry[] Items);
    private sealed record QueueEntry(string Video, string Subtitle, int Width, int Height);
    internal static string QueueLabel(string en, string he) =>
        Se.Settings.General.Language.Contains("Hebrew", StringComparison.OrdinalIgnoreCase) || Se.Settings.General.Language.StartsWith("he", StringComparison.OrdinalIgnoreCase) ? he : en;

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
            var doc = new QueueFile(1, JobItems.Select(x => new QueueEntry(x.InputVideoFileName,x.SubtitleFileName,x.Width,x.Height)).ToArray());
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(doc));
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
            var doc = JsonSerializer.Deserialize<QueueFile>(await File.ReadAllTextAsync(path));
            if (doc?.Version != 1 || doc.Items is not { Length: > 0 and <= 10000 }) throw new InvalidDataException("Invalid queue");
            var jobs = doc.Items.Select(x => {
                if (!File.Exists(x.Video)) throw new FileNotFoundException(x.Video);
                if (!string.IsNullOrEmpty(x.Subtitle) && !File.Exists(x.Subtitle)) throw new FileNotFoundException(x.Subtitle);
                var job = new BurnInJobItem(x.Video,x.Width,x.Height);
                job.AddSubtitleFileName(x.Subtitle);
                return job;
            }).ToArray();
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
