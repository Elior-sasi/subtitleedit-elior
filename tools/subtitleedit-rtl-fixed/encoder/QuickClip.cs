using System;
using System.IO;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Video.BurnIn;

public partial class BurnInViewModel
{
    private long? _clipInMs, _clipOutMs;
    private bool _quickStarting;
    internal string ClipHint { get; private set; } = "";
    internal bool ClipReady => _clipInMs.HasValue && _clipOutMs > _clipInMs && !IsBatchMode && !IsGenerating && !_quickStarting;
    private void ClipChanged(string hint)
    {
        ClipHint = hint;
        OnPropertyChanged(nameof(ClipHint));
        OnPropertyChanged(nameof(ClipReady));
    }
    private void ResetClipMarks()
    {
        _clipInMs = _clipOutMs = null;
        ClipChanged("");
    }
    internal void StepPreviewFrame(bool forward)
    {
        var player = VideoPlayerControl?.VideoPlayer;
        if (player == null || IsGenerating || IsBatchMode || _quickStarting ||
            string.IsNullOrEmpty(player.FileName) || player.Duration <= 0) return;
        // Use decoded-frame navigation, never an assumed 25 fps time increment.
        if (player is Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic.LibMpvDynamicPlayer mpv)
        {
            player.Pause();
            if (forward) mpv.StepOneFrameForward(); else mpv.StepOneFrameBack();
        }
        else if (player is Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg.FfmpegPlayer ffmpeg)
        {
            player.Pause();
            if (forward) ffmpeg.StepOneFrameForward(); else ffmpeg.StepOneFrameBack();
        }
        else ClipChanged(QueueLabel("This preview player does not support frame stepping.", "נגן התצוגה הזה אינו תומך במעבר לפי פריימים."));
    }
    internal void MarkPreview(bool start)
    {
        var player = VideoPlayerControl?.VideoPlayer;
        if (player == null || IsGenerating || IsBatchMode || _quickStarting) return;
        MarkClip(start, player.Position, player.Duration);
    }
    internal void MarkClip(bool start,double pos,double duration)
    {
        if (!double.IsFinite(pos) || !double.IsFinite(duration) || duration<=0 || pos<0 || pos>duration+0.001)
        {
            ClipChanged(QueueLabel("Wait for the video preview.", "יש להמתין לטעינת הסרטון."));return;
        }
        long ms=(long)Math.Round(Math.Min(pos,duration)*1000);
        if(start){_clipInMs=ms;if(_clipOutMs<=ms)_clipOutMs=null;}
        else if (_clipInMs is null || ms<=_clipInMs){
            _clipOutMs=null;ClipChanged(QueueLabel("Mark an In point, then an Out point after it.","סמן התחלה ואז סמן סוף בנקודה מאוחרת יותר."));return;
        } else _clipOutMs=ms;
        static string T(long? t)=>t.HasValue?TimeSpan.FromMilliseconds(t.Value).ToString(@"hh\:mm\:ss\.fff"):"…";
        ClipChanged($"{T(_clipInMs)} → {T(_clipOutMs)}");
    }
    internal static string NewClipPath(string source,string ext)
    {
        string dir=Path.GetDirectoryName(Path.GetFullPath(source))!;
        string stem=Path.GetFileNameWithoutExtension(source)+"-clip";
        for(int n=1;;n++){
            string p=Path.Combine(dir,stem+(n==1?"":"-"+n)+ext);
            if(!File.Exists(p)&&!Directory.Exists(p))return p;
        }
    }
    internal async Task ExportQuickClip()
    {
        if (!ClipReady || !File.Exists(VideoFileName)) return;
        _quickStarting=true;
        try {
            IsCutActive=true;
            CutFrom=TimeSpan.FromMilliseconds(_clipInMs!.Value);
            CutTo=TimeSpan.FromMilliseconds(_clipOutMs!.Value);
            await GenerateCore(true);
        } finally {_quickStarting=false;OnPropertyChanged(nameof(ClipReady));}
    }
}
