using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

// Client for the subs-il cloud MFA service (tools/subtitleedit-rtl-fixed/cloud-mfa/service/API.md).
// No UI or settings dependencies, so the protocol is unit-tested on its own.
internal sealed record CloudMfaCue(int Id, string Text);
internal sealed record CloudMfaLanguage(string Id, string Label, bool Available, string Reason, string Verified);
internal sealed record CloudMfaServiceInfo(string ServiceName, int RetentionHours, int MaxAudioSeconds, int MaxChunkBytes, List<CloudMfaLanguage> Languages);
internal sealed record CloudMfaStatus(string JobId, string Status, string? Stage, int? StageIndex, int StageCount, string CuesSha256, string AudioSha256, List<int> UploadedChunks, string? ErrorCode, string? ErrorMessage, int RetentionHours)
{
    internal bool IsFinal => Status is "succeeded" or "failed" or "cancelled";
}
internal sealed record CloudMfaWord(string Text, double? Start, double? End, string[] Flags);
internal sealed record CloudMfaCueResult(int Id, string Status, double? Start, double? End, List<CloudMfaWord> Words, string[] ReviewFlags);
internal sealed record CloudMfaResult(string JobId, string Status, string AudioSha256, string CuesSha256, double AudioDuration, string Engine, string Method, List<CloudMfaCueResult> Cues, string SummaryText);

internal sealed class CloudMfaException : Exception
{
    internal CloudMfaException(string message, string code = "", bool transient = false) : base(message) { Code = code; Transient = transient; }
    internal string Code { get; }
    internal bool Transient { get; }
}

internal sealed class CloudMfaClient
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _token;
    internal Func<int, CancellationToken, Task> Delay { get; set; } = (ms, ct) => Task.Delay(ms, ct);

    internal CloudMfaClient(HttpClient http, string baseUrl, string token)
    {
        _base = ValidateUrl(baseUrl);
        if (!IsValidToken(token)) throw new CloudMfaException("אסימון הגישה לשירות אינו בפורמט תקין.");
        _http = http; _token = token.Trim();
    }

    internal static bool IsValidToken(string token) => Regex.IsMatch(token?.Trim() ?? "", "^[A-Za-z0-9_-]{32,200}$");

    // HTTPS only; plain http is allowed solely for a service on this computer (tests).
    internal static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate((url ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new CloudMfaException("כתובת השירות אינה תקינה.");
        var local = uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
        if (uri.Scheme != Uri.UriSchemeHttps && !local) throw new CloudMfaException("כתובת השירות חייבת להתחיל ב־https://.");
        return uri;
    }

    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> make, CancellationToken token, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = make();
                using var response = await _http.SendAsync(request, token);
                var body = await response.Content.ReadAsStringAsync(token);
                if (response.IsSuccessStatusCode) return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
                var transient = response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)response.StatusCode == 429 && body.Contains("\"busy\"");
                if (transient && attempt < attempts) { await Delay(Backoff(attempt), token); continue; }
                throw Error(response.StatusCode, body);
            }
            catch (HttpRequestException) when (attempt < attempts) { await Delay(Backoff(attempt), token); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested && attempt < attempts) { await Delay(Backoff(attempt), token); }
            catch (HttpRequestException e) { throw new CloudMfaException("אין חיבור לשירות היישור: " + e.Message, "network", true); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new CloudMfaException("השירות לא הגיב בזמן.", "timeout", true); }
        }
    }

    private static int Backoff(int attempt) => Math.Min(15000, 1000 * (1 << Math.Min(attempt - 1, 4)));

    private static CloudMfaException Error(HttpStatusCode status, string body)
    {
        string code = "", message = "";
        try { using var doc = JsonDocument.Parse(body); var e = doc.RootElement.GetProperty("error"); code = e.GetProperty("code").GetString() ?? ""; message = e.GetProperty("message").GetString() ?? ""; } catch { }
        var hebrew = status switch
        {
            HttpStatusCode.Unauthorized => "השירות דחה את אסימון הגישה. בדוק את האסימון בהגדרות היישור בענן.",
            HttpStatusCode.NotFound => "המשימה לא נמצאה בשירות (נמחקה או פג תוקפה).",
            (HttpStatusCode)429 => "השירות עמוס או שיש לך משימות פעילות רבות מדי. נסה שוב מאוחר יותר.",
            HttpStatusCode.RequestEntityTooLarge => "הקובץ או הטקסט חורגים ממגבלות השירות.",
            _ => "השירות החזיר שגיאה " + (int)status + ".",
        };
        return new CloudMfaException(hebrew + (message.Length > 0 ? " (" + message + ")" : ""), code);
    }

    internal async Task<CloudMfaServiceInfo> InfoAsync(CancellationToken token)
    {
        using var doc = await SendAsync(() => Request(HttpMethod.Get, "v1/languages"), token);
        var r = doc.RootElement;
        var langs = r.GetProperty("languages").EnumerateArray().Select(l => new CloudMfaLanguage(
            Str(l, "id"), Str(l, "label"), l.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True, Str(l, "reason"), Str(l, "verified"))).ToList();
        return new CloudMfaServiceInfo(Str(r, "service"), r.GetProperty("retention_hours").GetInt32(), r.GetProperty("max_audio_seconds").GetInt32(), r.GetProperty("max_chunk_bytes").GetInt32(), langs);
    }

    // Idempotent: the same requestId always returns the same job, so a retry after a dropped
    // connection, or reopening the dialog after a crash, never creates a duplicate job.
    internal async Task<CloudMfaStatus> CreateAsync(Guid requestId, string language, IReadOnlyList<CloudMfaCue> cues, string audioSha256, long audioBytes, CancellationToken token)
    {
        var body = JsonSerializer.Serialize(new { client_request_id = requestId.ToString(), language, cues = cues.Select(c => new { id = c.Id, text = c.Text }), audio_sha256 = audioSha256, audio_bytes = audioBytes });
        using var doc = await SendAsync(() => Request(HttpMethod.Post, "v1/jobs", new StringContent(body, Encoding.UTF8, "application/json")), token);
        return Status(doc.RootElement);
    }

    internal async Task<CloudMfaStatus> UploadAsync(CloudMfaStatus job, string wavPath, int chunkBytes, IProgress<double>? progress, CancellationToken token)
    {
        var length = new FileInfo(wavPath).Length;
        var count = (int)((length + chunkBytes - 1) / chunkBytes);
        var done = new HashSet<int>(job.UploadedChunks);
        var buffer = new byte[chunkBytes];
        await using (var stream = File.OpenRead(wavPath))
        {
            for (var i = 0; i < count; i++)
            {
                var read = 0; stream.Position = (long)i * chunkBytes;
                while (read < chunkBytes) { var n = await stream.ReadAsync(buffer.AsMemory(read, chunkBytes - read), token); if (n == 0) break; read += n; }
                if (!done.Contains(i))
                {
                    var part = buffer.AsSpan(0, read).ToArray();
                    var sha = Convert.ToHexString(SHA256.HashData(part)).ToLowerInvariant();
                    using var doc = await SendAsync(() =>
                    {
                        var content = new ByteArrayContent(part); content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        var request = Request(HttpMethod.Put, $"v1/jobs/{job.JobId}/audio/chunks/{i}", content); request.Headers.Add("X-Chunk-SHA256", sha); return request;
                    }, token);
                }
                progress?.Report((double)Math.Min(length, (long)(i + 1) * chunkBytes) / length);
            }
        }
        var complete = JsonSerializer.Serialize(new { chunk_count = count });
        using var final = await SendAsync(() => Request(HttpMethod.Post, $"v1/jobs/{job.JobId}/audio/complete", new StringContent(complete, Encoding.UTF8, "application/json")), token);
        return Status(final.RootElement);
    }

    internal async Task<CloudMfaStatus> StatusAsync(string jobId, CancellationToken token)
    {
        using var doc = await SendAsync(() => Request(HttpMethod.Get, "v1/jobs/" + CheckId(jobId)), token);
        return Status(doc.RootElement);
    }

    // Polls until the job ends. Network drops are retried with backoff on the same job.
    internal async Task<CloudMfaStatus> WaitAsync(string jobId, Action<CloudMfaStatus> onStatus, CancellationToken token, int pollMs = 2000)
    {
        while (true)
        {
            CloudMfaStatus status;
            try { status = await StatusAsync(jobId, token); }
            catch (CloudMfaException e) when (e.Transient) { onStatus(new CloudMfaStatus(jobId, "reconnecting", null, null, 0, "", "", new(), e.Code, e.Message, 0)); await Delay(5000, token); continue; }
            onStatus(status);
            if (status.IsFinal) return status;
            await Delay(pollMs, token);
        }
    }

    internal async Task<CloudMfaStatus> CancelAsync(string jobId, CancellationToken token)
    {
        using var doc = await SendAsync(() => Request(HttpMethod.Post, $"v1/jobs/{CheckId(jobId)}/cancel"), token, 2);
        return Status(doc.RootElement);
    }

    internal async Task DeleteAsync(string jobId, CancellationToken token)
    {
        using var _ = await SendAsync(() => Request(HttpMethod.Delete, "v1/jobs/" + CheckId(jobId)), token, 2);
    }

    internal async Task<CloudMfaResult> ResultAsync(string jobId, CancellationToken token)
    {
        using var doc = await SendAsync(() => Request(HttpMethod.Get, $"v1/jobs/{CheckId(jobId)}/result"), token);
        return ParseResult(doc.RootElement);
    }

    private static string CheckId(string id) => Regex.IsMatch(id ?? "", "^[A-Za-z0-9_-]{32}$") ? id! : throw new CloudMfaException("מזהה משימה לא תקין.");
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static CloudMfaStatus Status(JsonElement r)
    {
        var error = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
        return new CloudMfaStatus(CheckId(Str(r, "job_id")), Str(r, "status"), Str(r, "stage") is { Length: > 0 } s ? s : null,
            r.TryGetProperty("stage_index", out var si) && si.ValueKind == JsonValueKind.Number ? si.GetInt32() : null,
            r.TryGetProperty("stage_count", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 0,
            Str(r, "cues_sha256"), Str(r, "audio_sha256"),
            r.TryGetProperty("uploaded_chunks", out var u) ? u.EnumerateArray().Select(x => x.GetInt32()).ToList() : new(),
            error.ValueKind == JsonValueKind.Object ? Str(error, "code") : null, error.ValueKind == JsonValueKind.Object ? Str(error, "message") : null,
            r.TryGetProperty("retention_hours", out var rh) && rh.ValueKind == JsonValueKind.Number ? rh.GetInt32() : 0);
    }

    internal static CloudMfaResult ParseResult(JsonElement r)
    {
        if (Str(r, "schema") != "subs-il.mfa-result/1") throw new CloudMfaException("פורמט תוצאה לא מוכר. עדכן את התוכנה או את השירות.");
        var input = r.GetProperty("input"); var engine = r.GetProperty("engine");
        var cues = r.GetProperty("cues").EnumerateArray().Select(c => new CloudMfaCueResult(
            c.GetProperty("id").GetInt32(), Str(c, "status"), Num(c, "start"), Num(c, "end"),
            c.GetProperty("words").EnumerateArray().Select(w => new CloudMfaWord(Str(w, "text"), Num(w, "start"), Num(w, "end"),
                w.GetProperty("flags").EnumerateArray().Select(f => f.GetString() ?? "").ToArray())).ToList(),
            c.TryGetProperty("review_flags", out var rf) ? rf.EnumerateArray().Select(f => Str(f, "code")).ToArray() : Array.Empty<string>())).ToList();
        var method = r.TryGetProperty("method", out var m) ? m : default;
        var passes = method.ValueKind == JsonValueKind.Object && method.TryGetProperty("passes", out var p) ? p.GetInt32() : 1;
        var s = r.GetProperty("summary");
        int I(string n) => s.TryGetProperty(n, out var v) ? v.GetInt32() : 0;
        var summary = $"כתוביות: {I("cues_total")} · יושרו במלואן: {I("cues_aligned")} · חלקית: {I("cues_partial")} · לא יושרו: {I("cues_unaligned")} · לבדיקה: {I("cues_review")}\n" +
                      $"מילים שיושרו: {I("words_aligned")} מתוך {I("words_total")} · חסרות: {I("words_missing")} · הגייה לא ידועה: {I("words_oov")} · הגייה שנוצרה אוטומטית (G2P): {I("words_g2p")}";
        return new CloudMfaResult(CheckId(Str(r, "job_id")), Str(r, "status"), Str(input, "audio_sha256"), Str(input, "cues_sha256"),
            Num(input, "audio_duration_s") ?? 0, $"{Str(engine, "name")} {Str(engine, "version")} · {Str(engine, "acoustic_model")}",
            passes == 2 ? "שני מעברים: יישור מלא, ואז יישור חוזר בתוך קטעי דיבור (MFA segment_vad)" : "מעבר יישור אחד", cues, summary);
    }
}

internal static class CloudMfaImport
{
    internal sealed record Cue(int Id, string Text, double Start, double End);

    // Cues are built from the same preview the user approved: blank-line separated blocks.
    internal static List<CloudMfaCue> CuesFromPrepared(string prepared) =>
        prepared.Replace("\r\n", "\n").Split("\n\n").Select(t => t.Trim('\n')).Where(t => t.Trim().Length > 0).Select((t, i) => new CloudMfaCue(i + 1, t)).ToList();

    internal static string Fingerprint(string url, string language, IEnumerable<CloudMfaCue> cues, string mediaPath, long mediaBytes, DateTime mediaWriteUtc)
    {
        var text = url + "\u001f" + language + "\u001f" + mediaPath + "\u001f" + mediaBytes + "\u001f" + mediaWriteUtc.Ticks + "\u001f" + string.Join("\u001e", cues.Select(c => c.Id + "\u001d" + c.Text));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    // Accepts a result only for exactly what was sent, with engine timings that are valid
    // for this audio. Nothing is moved, stretched or invented to make it fit.
    internal static List<Cue> Validate(CloudMfaResult result, IReadOnlyList<CloudMfaCue> sent, string audioSha256, string cuesSha256, bool reviewedFlagged)
    {
        if (result.AudioSha256 != audioSha256 || result.CuesSha256 != cuesSha256) throw new CloudMfaException("התוצאה שייכת לשמע או לטקסט אחר. לא ניתן לייבא אותה.");
        if (result.Cues.Count != sent.Count) throw new CloudMfaException("מספר הכתוביות בתוצאה אינו תואם לטקסט שנשלח.");
        if (result.Status == "partial" || result.Cues.Any(c => c.Status != "aligned")) throw new CloudMfaException("היישור לא הושלם לכל הכתוביות. ייבוא תוצאה חלקית אינו מותר; בדוק את ההבדלים בתהליך ההשוואה.");
        if (result.Status == "needs_review" && !reviewedFlagged) throw new CloudMfaException("יש כתוביות שסומנו לבדיקה. האזן להן ואשר לפני ייבוא.");
        if (result.Status is not ("complete" or "needs_review")) throw new CloudMfaException("סטטוס תוצאה לא מוכר.");
        var cues = new List<Cue>(); var lastEnd = 0.0;
        for (var i = 0; i < sent.Count; i++)
        {
            var c = result.Cues[i];
            if (c.Id != sent[i].Id) throw new CloudMfaException("סדר הכתוביות בתוצאה אינו תואם.");
            var expected = Regex.Matches(sent[i].Text, @"\S+").Select(m => m.Value).ToArray();
            if (!expected.SequenceEqual(c.Words.Select(w => w.Text))) throw new CloudMfaException("נוסח הכתובית בתוצאה שונה מהטקסט שנשלח. לא יובא דבר.");
            if (c.Start is not double start || c.End is not double end || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > result.AudioDuration + 0.05 || start < lastEnd - 0.0005)
                throw new CloudMfaException($"תזמון לא תקין בכתובית {i + 1}. לא יובא דבר.");
            lastEnd = end;
            cues.Add(new Cue(c.Id, sent[i].Text, start, end));
        }
        return cues;
    }

    internal static string Describe(CloudMfaCueResult c, string text)
    {
        static string T(double? s) => s is double v ? TimeSpan.FromSeconds(v).ToString(@"hh\:mm\:ss\,fff") : "--:--:--,---";
        var mark = c.Status switch { "aligned" when c.ReviewFlags.Length == 0 => "✓ יושר", "aligned" => "⚠ לבדיקה", "partial" => "⚠ חלקי", _ => "✗ לא יושר" };
        var notes = new List<string>();
        var missing = c.Words.Where(w => w.Flags.Any(f => f is "missing" or "partially_missing")).Select(w => w.Text).ToList();
        if (missing.Count > 0) notes.Add("לא יושרו: " + string.Join(" ", missing));
        var oov = c.Words.Where(w => w.Flags.Contains("oov_unknown_pronunciation")).Select(w => w.Text).ToList();
        if (oov.Count > 0) notes.Add("הגייה לא ידועה: " + string.Join(" ", oov));
        var g2p = c.Words.Where(w => w.Flags.Contains("g2p_pronunciation")).Select(w => w.Text).ToList();
        if (g2p.Count > 0) notes.Add("הגייה אוטומטית: " + string.Join(" ", g2p));
        if (c.ReviewFlags.Contains("crosses_long_pause")) notes.Add("הכתובית חוצה הפסקה ארוכה - ייתכן שחסר או עודף טקסט");
        if (c.ReviewFlags.Contains("long_word_duration")) notes.Add("מילה ארוכה במיוחד בזמן - בדוק באוזן");
        return $"{mark} | {T(c.Start)} → {T(c.End)} | {text.Replace('\n', ' ')}" + (notes.Count > 0 ? "\n      " + string.Join(" · ", notes) : "");
    }
}

