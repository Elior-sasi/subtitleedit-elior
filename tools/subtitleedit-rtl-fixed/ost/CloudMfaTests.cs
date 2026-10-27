using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Xunit;

namespace UITests.Features.Main;

public partial class OstTableTests
{
    private const string MfaToken = "unit_test_token_0123456789abcdefABCDEF";
    private static readonly string MfaJob = new('J', 32);

    private sealed class FakeMfaServer : HttpMessageHandler
    {
        internal readonly List<string> Calls = new();
        internal int Creates; internal int DropFirstStatus = 0; internal readonly Dictionary<Guid, string> Jobs = new();
        internal string ResultJson = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("Bearer " + MfaToken, request.Headers.Authorization?.ToString());
            var path = request.RequestUri!.AbsolutePath; Calls.Add(request.Method + " " + path);
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
            string Status(string s, string stage = "queued") => $"{{\"job_id\":\"{MfaJob}\",\"status\":\"{s}\",\"stage\":\"{stage}\",\"stage_index\":2,\"stage_count\":8,\"cues_sha256\":\"{new string('c', 64)}\",\"audio_sha256\":\"{new string('a', 64)}\",\"uploaded_chunks\":[],\"retention_hours\":6}}";
            HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (path == "/v1/jobs" && request.Method == HttpMethod.Post)
            {
                Creates++;
                var id = Guid.Parse(JsonDocument.Parse(body).RootElement.GetProperty("client_request_id").GetString()!);
                if (Creates == 1) throw new HttpRequestException("connection reset"); // first attempt drops after reaching the server
                Jobs.TryAdd(id, MfaJob);
                return Json(Status("awaiting_upload"), Jobs.Count == 1 && Creates == 2 ? HttpStatusCode.Created : HttpStatusCode.OK);
            }
            if (path.Contains("/audio/chunks/")) { Assert.Matches("^[0-9a-f]{64}$", request.Headers.GetValues("X-Chunk-SHA256").Single()); return Json(Status("awaiting_upload")); }
            if (path.EndsWith("/audio/complete")) return Json(Status("queued"));
            if (path.EndsWith("/result")) return Json(ResultJson);
            if (path.EndsWith("/cancel")) return Json(Status("cancelled"));
            if (request.Method == HttpMethod.Get && path == "/v1/jobs/" + MfaJob)
            {
                if (DropFirstStatus-- > 0) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return Json(Status("succeeded", "done"));
            }
            return Json("{\"error\":{\"code\":\"not_found\",\"message\":\"x\"}}", HttpStatusCode.NotFound);
        }
    }

    private static string MfaResult(string status, string cue2Status = "aligned", string[]? review = null, double end2 = 3.0) =>
        "{\"schema\":\"subs-il.mfa-result/1\",\"job_id\":\"" + MfaJob + "\",\"status\":\"" + status + "\"," +
        "\"engine\":{\"name\":\"Montreal Forced Aligner\",\"version\":\"3.4.3\",\"acoustic_model\":\"english_mfa\"}," +
        "\"input\":{\"audio_sha256\":\"" + new string('a', 64) + "\",\"cues_sha256\":\"" + new string('c', 64) + "\",\"audio_duration_s\":10.0}," +
        "\"method\":{\"passes\":1},\"summary\":{\"cues_total\":2}," +
        "\"cues\":[{\"id\":1,\"status\":\"aligned\",\"start\":0.5,\"end\":1.5,\"review_flags\":[],\"words\":[{\"text\":\"<i>Hello,\",\"start\":0.5,\"end\":1.0,\"flags\":[]},{\"text\":\"world!</i>\",\"start\":1.0,\"end\":1.5,\"flags\":[]}]}," +
        "{\"id\":2,\"status\":\"" + cue2Status + "\",\"start\":2.0,\"end\":" + end2.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"review_flags\":[" + string.Join(",", (review ?? Array.Empty<string>()).Select(r => "{\"code\":\"" + r + "\"}")) + "],\"words\":[{\"text\":\"-Next\",\"start\":2.0,\"end\":2.5,\"flags\":[]},{\"text\":\"line.\",\"start\":2.5,\"end\":3.0,\"flags\":[]}]}]}";

    [Fact]
    public async Task CloudMfaRetriesOnSameJobAndNeverDuplicates()
    {
        var server = new FakeMfaServer { DropFirstStatus = 2 };
        using var http = new HttpClient(server);
        var client = new CloudMfaClient(http, "https://mfa.example.test", MfaToken) { Delay = (_, _) => Task.CompletedTask };
        var cues = CloudMfaImport.CuesFromPrepared("<i>Hello,\nworld!</i>\n\n-Next line.");
        Assert.Equal(2, cues.Count); Assert.Equal("<i>Hello,\nworld!</i>", cues[0].Text);
        var rid = Guid.NewGuid();
        var job = await client.CreateAsync(rid, "english_mfa", cues, new string('a', 64), 1000, default);
        Assert.Equal(2, server.Creates); Assert.Single(server.Jobs); // retried create reused the same request id
        var wav = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            File.WriteAllBytes(wav, new byte[2500]);
            var progress = new List<double>();
            await client.UploadAsync(job, wav, 1000, new SyncProgress(progress.Add), default);
            Assert.Equal(3, server.Calls.Count(c => c.Contains("/audio/chunks/")));
            Assert.Equal(1.0, progress.Last());
        }
        finally { File.Delete(wav); }
        var seen = new List<string>();
        var final = await client.WaitAsync(job.JobId, s => seen.Add(s.Status), default, 1);
        Assert.Equal("succeeded", final.Status);
        Assert.Equal(1, server.Calls.Count(c => c == "POST /v1/jobs") - 1); // two POSTs: dropped + retried
    }

    private sealed class SyncProgress : IProgress<double> { private readonly Action<double> _a; internal SyncProgress(Action<double> a) => _a = a; public void Report(double v) => _a(v); }

    [Fact]
    public void CloudMfaImportRequiresCompleteFreshResult()
    {
        var cues = CloudMfaImport.CuesFromPrepared("<i>Hello,\nworld!</i>\n\n-Next line.");
        CloudMfaResult Parse(string json) => CloudMfaClient.ParseResult(JsonDocument.Parse(json).RootElement);
        var ok = CloudMfaImport.Validate(Parse(MfaResult("complete")), cues, new string('a', 64), new string('c', 64), false);
        Assert.Equal(cues.Select(c => c.Text), ok.Select(c => c.Text)); // tags, punctuation and dash kept exactly
        Assert.Equal(new[] { 0.5, 2.0 }, ok.Select(c => c.Start));
        Assert.Throws<CloudMfaException>(() => CloudMfaImport.Validate(Parse(MfaResult("partial", "partial")), cues, new string('a', 64), new string('c', 64), true));
        Assert.Throws<CloudMfaException>(() => CloudMfaImport.Validate(Parse(MfaResult("needs_review", review: new[] { "crosses_long_pause" })), cues, new string('a', 64), new string('c', 64), false));
        Assert.Equal(2, CloudMfaImport.Validate(Parse(MfaResult("needs_review", review: new[] { "crosses_long_pause" })), cues, new string('a', 64), new string('c', 64), true).Count);
        Assert.Throws<CloudMfaException>(() => CloudMfaImport.Validate(Parse(MfaResult("complete")), cues, new string('b', 64), new string('c', 64), false)); // other audio
        var edited = cues.Select((c, i) => i == 1 ? c with { Text = "-Next line changed." } : c).ToList();
        Assert.Throws<CloudMfaException>(() => CloudMfaImport.Validate(Parse(MfaResult("complete")), edited, new string('a', 64), new string('c', 64), false)); // stale text
        Assert.Throws<CloudMfaException>(() => CloudMfaImport.Validate(Parse(MfaResult("complete", end2: 11.0)), cues, new string('a', 64), new string('c', 64), false)); // beyond audio
        Assert.Throws<CloudMfaException>(() => CloudMfaImport.Validate(Parse(MfaResult("complete", end2: 1.0)), cues, new string('a', 64), new string('c', 64), false)); // end before start
        Assert.Contains("⚠", CloudMfaImport.Describe(Parse(MfaResult("needs_review", review: new[] { "crosses_long_pause" })).Cues[1], cues[1].Text));
    }

    [Fact]
    public void CloudMfaRejectsUnsafeConnectionSettings()
    {
        Assert.Throws<CloudMfaException>(() => CloudMfaClient.ValidateUrl("http://mfa.example.test"));
        Assert.Throws<CloudMfaException>(() => CloudMfaClient.ValidateUrl("https://user:pw@mfa.example.test"));
        Assert.Equal("http://127.0.0.1:8080/", CloudMfaClient.ValidateUrl("http://127.0.0.1:8080").ToString());
        Assert.False(CloudMfaClient.IsValidToken("short"));
        Assert.False(CloudMfaClient.IsValidToken(MfaToken + " ; rm"));
        var a = CloudMfaImport.Fingerprint("https://x", "en", CloudMfaImport.CuesFromPrepared("a\n\nb"), "m.wav", 10, DateTime.UnixEpoch);
        Assert.NotEqual(a, CloudMfaImport.Fingerprint("https://x", "en", CloudMfaImport.CuesFromPrepared("a\n\nb!"), "m.wav", 10, DateTime.UnixEpoch));
        Assert.NotEqual(a, CloudMfaImport.Fingerprint("https://x", "en", CloudMfaImport.CuesFromPrepared("a\n\nb"), "m.wav", 11, DateTime.UnixEpoch));
    }

    // Protocol end-to-end against the real Python service (fake engine double) started by CI.
    [Fact]
    public async Task CloudMfaLiveServiceProtocol()
    {
        var url = Environment.GetEnvironmentVariable("SUBS_IL_MFA_TEST_URL");
        if (string.IsNullOrEmpty(url)) return;
        var token = Environment.GetEnvironmentVariable("SUBS_IL_MFA_TEST_TOKEN")!;
        using var http = new HttpClient();
        var client = new CloudMfaClient(http, url, token);
        var info = await client.InfoAsync(default);
        Assert.Contains(info.Languages, l => l.Id == "hebrew" && !l.Available);
        var lang = info.Languages.First(l => l.Available).Id;
        var cues = CloudMfaImport.CuesFromPrepared("<i>Hello, world!</i>\n\n-Second line.");
        var wav = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            using (var w = new BinaryWriter(File.Create(wav)))
            {
                var samples = 16000 * 3; w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16); w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2); for (var i = 0; i < samples; i++) w.Write((short)(3000 * Math.Sin(i * 2 * Math.PI * 220 / 16000)));
            }
            var sha = CloudMfaClient.Sha256File(wav); var rid = Guid.NewGuid();
            var job = await client.CreateAsync(rid, lang, cues, sha, new FileInfo(wav).Length, default);
            Assert.Equal(job.JobId, (await client.CreateAsync(rid, lang, cues, sha, new FileInfo(wav).Length, default)).JobId);
            job = await client.UploadAsync(job, wav, 40000, null, default);
            var final = await client.WaitAsync(job.JobId, _ => { }, default, 200);
            Assert.Equal("succeeded", final.Status);
            var result = await client.ResultAsync(job.JobId, default);
            Assert.Equal(cues.Select(c => c.Text), CloudMfaImport.Validate(result, cues, sha, job.CuesSha256, true).Select(c => c.Text));
            await client.DeleteAsync(job.JobId, default);
            await Assert.ThrowsAsync<CloudMfaException>(() => client.StatusAsync(job.JobId, default));
        }
        finally { File.Delete(wav); }
    }
}

