using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.Features.Video.BurnIn;
using Nikse.SubtitleEdit.Logic.Media;

namespace UITests.Features.Video;

public class EncoderMuxTests
{
    private static async Task<string> Run(string dir, string exe, string args)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo(exe, args) {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        }};
        p.Start();
        var log = p.StandardError.ReadToEndAsync();
        var data = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, await log);
        return await data;
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    public async Task SoftVideoIsUnchangedAndBothBurnsDifferentText(string ext)
    {
        var dir = Directory.CreateTempSubdirectory("encoder-test-").FullName;
        try {
            var input = Path.Combine(dir,"מקור 1.mp4");
            var sub = Path.Combine(dir,"דיאלוג.srt");
            var burn = Path.Combine(dir,"sign.ass");
            await File.WriteAllTextAsync(sub,"1\n00:00:00,100 --> 00:00:00,900\nשלום עולם!\n");
            await File.WriteAllTextAsync(Path.Combine(dir,"sign.srt"),"1\n00:00:00,100 --> 00:00:00,900\nכניסה\n");
            await Run(dir,"ffmpeg","-v error -i sign.srt sign.ass");
            await Run(dir,"ffmpeg",$"-v error -f lavfi -i color=s=1920x1080:r=24 -f lavfi -i sine=f=400 -f lavfi -i sine=f=600 -map 0:v -map 1:a -map 2:a -t 1 -c:v libx264 -threads 2 -c:a aac {SubtitleMux.PathArg(input)}");
            async Task<string[]> Frames(string file) => (await Run(dir,"ffmpeg",$"-v error -i {SubtitleMux.PathArg(file)} -map 0:v:0 -f framemd5 -")).Split('\n').Where(l=>l.StartsWith("0,")).Select(l=>l.Split(',').Last().Trim()).ToArray();
            var reference = await Frames(input);
            Assert.Equal(24, reference.Length);
            foreach (var both in new[]{false,true}) {
                var output = Path.Combine(dir,(both?"both":"soft")+ext);
                var args = both ? FfmpegGenerator.GenerateHardcodedVideoFile(input,burn,output,1920,1080,"libx264","veryfast","yuv420p","20","copy",false,"48000","","128k","","",softSubtitle:sub) : SubtitleMux.SoftOnly(input,sub,output);
                await Run(dir,"ffmpeg",args);
                using var probe = JsonDocument.Parse(await Run(dir,"ffprobe",$"-v error -show_streams -of json {SubtitleMux.PathArg(output)}"));
                var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                Assert.Equal(2,streams.Count(x=>x.GetProperty("codec_type").GetString()=="audio"));
                Assert.Single(streams.Where(x=>x.GetProperty("codec_type").GetString()=="subtitle"));
                var extracted = await Run(dir,"ffmpeg",$"-v error -i {SubtitleMux.PathArg(output)} -map 0:s:0 -f srt -");
                Assert.Contains("שלום עולם!",extracted);
                Assert.DoesNotContain("כניסה",extracted);
                var frames = await Frames(output);
                if (both) Assert.NotEqual(reference[12],frames[12]);
                else Assert.Equal(reference,frames);
            }
        } finally { Directory.Delete(dir,true); }
    }
}
