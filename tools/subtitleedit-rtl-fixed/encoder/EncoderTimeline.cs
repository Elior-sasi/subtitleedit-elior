using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic.Media;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Video.BurnIn;

public partial class BurnInViewModel
{
    private HashSet<int>? _burnRows;

    private Subtitle BurnDraft(Subtitle src)
    {
        var result = new Subtitle(src, false);
        if (_burnRows != null)
            for (int i=result.Paragraphs.Count-1;i>=0;i--)
                if (!_burnRows.Contains(i)) result.Paragraphs.RemoveAt(i);
        return result;
    }

    internal string TimelineSoftTrack(string file)
    {
        if (!IsCutActive) return PrepareSoftTrack(file);
        var src = Subtitle.Parse(file) ?? throw new InvalidDataException("Cannot read subtitle track.");
        var clipped = GetSubtitleBasedOnCut(src);
        var ext = Path.GetExtension(file);
        return PrepareSoftTrack(_tempSubtitleFiles.Write(clipped,
            ext.Equals(".srt", StringComparison.OrdinalIgnoreCase) ? (SubtitleFormat)new SubRip() : new AdvancedSubStationAlpha()));
    }

    internal async Task EditEncoderTimeline()
    {
        if (Window == null || IsGenerating) return;
        double duration = _mediaInfo?.Duration?.TotalMilliseconds ?? 0;
        if (IsBatchMode || IsImageSubtitle || duration <= 0 || !File.Exists(VideoFileName))
        {
            await MessageBox.Show(Window, Se.Language.General.Warning, QueueLabel("Open one video with text subtitles to edit the timeline.", "לעריכת ציר הזמן יש לפתוח סרטון בודד עם כתוביות טקסט."), MessageBoxButtons.OK);
            return;
        }
        var draft = new Subtitle(_subtitle, false);
        var rows = draft.Paragraphs;
        var selected = _burnRows == null ? Enumerable.Range(0, rows.Count).ToHashSet() : new HashSet<int>(_burnRows);
        string L(string en,string he) => QueueLabel(en,he);
        var win = new Window { Title=L("Encoder timeline","ציר הזמן של המקודד"), Width=960, Height=680, WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing=8, Margin=new Thickness(12) };
        win.Content = new ScrollViewer { Content=panel };
        var list = new ComboBox { HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Stretch, ItemsSource=rows.Select((p,i)=>$"{i+1}. {p.Text.Replace(Environment.NewLine," ")}").ToArray() };
        panel.Children.Add(list);
        double fps=(double)(_mediaInfo?.FramesRate ?? 0), page=0, cursor=0;
        const double span=10000;
        var chart = new TimelineChart();
        panel.Children.Add(chart);
        var time = new TextBlock();
        panel.Children.Add(time);
        StackPanel Bar() { var b=new StackPanel { Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=6 };panel.Children.Add(b);return b; }
        void Button(Panel group,string caption,Action go) { var b=new Button {Content=caption}; b.Click+=(_,_)=>go();group.Children.Add(b); }
        var position=Bar(); var cue=Bar(); var scope=Bar(); var cut=Bar();
        var check=new CheckBox {Content=L("Trim output","חתוך את הווידאו בפלט"),IsChecked=IsCutActive};cut.Children.Add(check);
        double from=IsCutActive?CutFrom.TotalMilliseconds:0, to=IsCutActive?CutTo.TotalMilliseconds:duration;
        var status=new TextBlock { TextWrapping=TextWrapping.Wrap };panel.Children.Add(status);
        string Stamp(double ms)=>TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss\.fff");
        using var lifetime=new CancellationTokenSource();
        CancellationTokenSource? load=null;
        void Draw()
        {
            int i=list.SelectedIndex;
            time.Text=$"{Stamp(page)} – {Stamp(Math.Min(duration,page+span))}    |    {L("Cursor","סמן")}: {Stamp(cursor)}";
            status.Text=$"{L("Trim","חיתוך")}: {Stamp(from)} – {Stamp(to)}    |    {L("Burn","לצריבה")}: {selected.Count}/{rows.Count}";
            if(i>=0) status.Text+=$"    |    {Stamp(rows[i].StartTime.TotalMilliseconds)} – {Stamp(rows[i].EndTime.TotalMilliseconds)}   {(selected.Contains(i)?"✓":"−")}";
            chart.Update(page,span,cursor,rows.Where(p=>p.EndTime.TotalMilliseconds>page && p.StartTime.TotalMilliseconds<page+span).Select(p=>(p.StartTime.TotalMilliseconds,p.EndTime.TotalMilliseconds)).ToArray());
        }
        async Task Wave(double start)
        {
            load?.Cancel();
            var c=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); load=c;
            page=Math.Clamp(start,0,Math.Max(0,duration-span));chart.Peaks=[];Draw();
            try { var peaks=await TimelineChart.ReadPeaks(FfmpegHelper.GetFfmpegLocation(),VideoFileName,page,c.Token);
                if (!c.IsCancellationRequested) { chart.Peaks=peaks;Draw(); } }
            catch(OperationCanceledException) { }
            catch(Exception ex) { if(!c.IsCancellationRequested) time.Text+="   "+L("Waveform unavailable","גל הקול אינו זמין")+": "+ex.Message; }
            finally { if(load==c)load=null;c.Dispose(); }
        }
        void Seek(double ms)
        {
            cursor=Math.Clamp(ms,0,duration);
            if(cursor<page || cursor>page+span) _=Wave(cursor-span/2); else Draw();
            VideoPlayerControl?.SetPosition(cursor/1000);
        }
        chart.PointerPressed+=(_,e)=>Seek(page+Math.Clamp(e.GetPosition(chart).X/Math.Max(1,chart.Bounds.Width),0,1)*span);
        Button(position,"−10s",()=>Seek(cursor-span));
        if(fps>0) { Button(position,"−1f",()=>Seek((Math.Round(cursor*fps/1000)-1)*1000/fps)); Button(position,"+1f",()=>Seek((Math.Round(cursor*fps/1000)+1)*1000/fps)); }
        Button(position,"+10s",()=>Seek(cursor+span));
        void Endpoint(bool start)
        {
            int i=list.SelectedIndex;if(i<0)return;
            var p=rows[i]; var ms=Math.Round(cursor);
            if(start && ms<p.EndTime.TotalMilliseconds) p.StartTime.TotalMilliseconds=ms;
            else if(!start && ms>p.StartTime.TotalMilliseconds)p.EndTime.TotalMilliseconds=ms;
            else {status.Text=L("End must follow start.","סוף הכתובית חייב להיות אחרי ההתחלה.");return;}
            Draw();
        }
        Button(cue,L("Cue start here","התחלת כתובית כאן"),()=>Endpoint(true));
        Button(cue,L("Cue end here","סוף כתובית כאן"),()=>Endpoint(false));
        Button(cue,L("Toggle burn for cue","סמן או בטל צריבת שורה"),()=>{int i=list.SelectedIndex;if(i>=0&&!selected.Remove(i))selected.Add(i);Draw();});
        void Select(Func<Paragraph,bool> match) { selected.Clear();for(int i=0;i<rows.Count;i++)if(match(rows[i]))selected.Add(i);Draw(); }
        Button(scope,L("All","הכול"),()=>Select(_=>true));
        Button(scope,L("Forced only","כפויות בלבד"),()=>Select(p=>p.Forced));
        var styles=new ComboBox {ItemsSource=rows.Select(p=>p.Extra).Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),MinWidth=100};scope.Children.Add(styles);
        Button(scope,L("Add style","הוסף סגנון לצריבה"),()=>{if(styles.SelectedItem is string s)for(int i=0;i<rows.Count;i++)if(string.Equals(rows[i].Extra,s,StringComparison.OrdinalIgnoreCase))selected.Add(i);Draw();});
        Button(scope,L("Clear","נקה בחירה"),()=>Select(_=>false));
        Button(cut,L("Cut from cursor","חתוך מהסמן"),()=>{from=cursor;check.IsChecked=true;Draw();});
        Button(cut,L("Cut to cursor","חתוך עד הסמן"),()=>{to=cursor;check.IsChecked=true;Draw();});
        var footer=Bar();
        Button(footer,L("Apply to export","החל על הייצוא"),()=>{
            if(check.IsChecked==true && Math.Round(to)<=Math.Round(from)) {status.Text=L("Invalid cut range.","סוף החיתוך חייב להיות אחרי ההתחלה.");return;}
            win.Close(true);
        });
        Button(footer,L("Cancel","ביטול"),()=>win.Close(false));
        list.SelectionChanged+=(_,_)=>{if(list.SelectedIndex>=0) Seek(rows[list.SelectedIndex].StartTime.TotalMilliseconds);};
        if(rows.Count>0)list.SelectedIndex=0;
        win.Opened+=(_,_)=>{_=Wave(Math.Max(0,cursor-span/2));};
        win.Closed+=(_,_)=>lifetime.Cancel();
        if(await win.ShowDialog<bool>(Window)) {
            _subtitle=draft; _burnRows=selected.Count==rows.Count?null:selected;
            IsCutActive=check.IsChecked==true;
            if(IsCutActive){CutFrom=TimeSpan.FromMilliseconds(Math.Round(from));CutTo=TimeSpan.FromMilliseconds(Math.Round(to));}
            _previewDirty=true;
        }
    }
}

internal sealed class TimelineChart : Control
{
    internal double[] Peaks=[];
    private double start,span=10000,cursor;
    private (double A,double B)[] cues=[];
    public TimelineChart() {Height=140; ClipToBounds=true;}
    internal void Update(double a,double b,double t,(double,double)[] c){start=a;span=b;cursor=t;cues=c;InvalidateVisual();}
    public override void Render(DrawingContext dc)
    {
        base.Render(dc); var w=Bounds.Width;dc.FillRectangle(Brushes.Black,new Rect(0,0,w,Bounds.Height));
        double X(double ms)=>(ms-start)*w/span;
        var wave=new Pen(Brushes.LightGreen,1.5);
        for(int i=0;i<Peaks.Length;i++){double x=w*i/800, h=55*Peaks[i];dc.DrawLine(wave,new Point(x,58-h),new Point(x,58+h));}
        foreach(var (a,b) in cues){double x=Math.Max(0,X(a)),r=Math.Min(w,X(b));if(r>x)dc.FillRectangle(Brushes.DeepSkyBlue,new Rect(x,120,Math.Max(1,r-x-1),10));}
        dc.DrawLine(new Pen(Brushes.Yellow,2),new Point(X(cursor),0),new Point(X(cursor),138));
    }
    internal static async Task<double[]> ReadPeaks(string ff,string path,double start,CancellationToken token)
    {
        var si=new ProcessStartInfo(ff){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var s in new[]{"-nostdin","-v","error","-ss",(start/1000).ToString("0.######",CultureInfo.InvariantCulture),"-i",path,"-t","10","-map","0:a:0","-vn","-ac","1","-ar","8000","-f","s16le","pipe:1"})si.ArgumentList.Add(s);
        using var p=Process.Start(si)??throw new IOException("FFmpeg");
        using var cancel=token.Register(()=>{try{if(!p.HasExited)p.Kill(true);}catch(InvalidOperationException){}});
        var err=p.StandardError.ReadToEndAsync();using var data=new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(data,token);await p.WaitForExitAsync(token);
        if(p.ExitCode!=0)throw new IOException((await err).Trim());
        var raw=data.ToArray();var peaks=new double[Math.Min(800,(raw.Length+199)/200)];
        for(int i=0;i+1<raw.Length && i/200<peaks.Length;i+=2)
            peaks[i/200]=Math.Max(peaks[i/200],Math.Abs((double)(short)(raw[i]|raw[i+1]<<8))/32768);
        return peaks;
    }
}
