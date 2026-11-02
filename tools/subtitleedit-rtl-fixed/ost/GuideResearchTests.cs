using System;
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
    private static readonly DateTimeOffset ResearchDate = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static string ResearchFixture(string text = "שלום 🦊", string url = "https://studio.example/work", string suggestions = "<style>.chip{color:blue}</style><a class='chip' href='https://www.google.com/search?q=title'>Google</a>") => JsonSerializer.Serialize(new
    {
        status = "completed", model = "gemini-4-argon",
        steps = new object[] {
            new { type = "google_search_call", arguments = new { queries = new[] { "title year official studio" } } },
            new { type = "google_search_result", call_id = "search", result = new[] { new { search_suggestions = suggestions } } },
            new { type = "thought", summary = "NEVER SHOW PRIVATE THOUGHT" },
            new { type = "model_output", content = new[] { new { type = "text", text, annotations = new[] { new { type = "url_citation", title = "Studio <official?>", url, start_index = 0, end_index = Encoding.UTF8.GetByteCount(text) } } } } }
        }
    });
    [Fact]
    public void GuideResearchPreservesOriginalSearchWidgetAndHebrewByteCitationAndEscapesModelHtml()
    {
        var result = GuideResearch.Parse(ResearchFixture("שלום 🦊 <script>bad()</script>"), ResearchDate);
        Assert.Equal("שלום 🦊 <script>bad()</script>", result.Text); Assert.Equal(result.Text, Assert.Single(result.Citations).Passage);
        Assert.Contains("&lt;script&gt;", result.Html); Assert.DoesNotContain("<script>", result.Html); Assert.DoesNotContain("NEVER SHOW", result.Html);
        Assert.Contains("<style>.chip{color:blue}</style><a class='chip' href='https://www.google.com/search?q=title'>Google</a>", result.Html);
        Assert.Contains("https://studio.example/work", result.UserCopy); Assert.Contains("לא מדריך רשמי", result.UserCopy);
        Assert.Equal("🦊", GuideResearch.BytePassage("א🦊ב", 2, 6));
        Assert.Throws<InvalidDataException>(() => GuideResearch.BytePassage("א🦊ב", 3, 6));
        Assert.Throws<InvalidDataException>(() => GuideResearch.BytePassage("x", 0, 2));
    }
    [Fact]
    public void GuideResearchRequiresCompletedActualSearchResultsCitationsAndValidRanges()
    {
        var good = ResearchFixture();
        foreach (var bad in new[] {
            good.Replace("completed", "incomplete"), good.Replace("google_search_call", "other"), good.Replace("google_search_result", "other"),
            good.Replace("url_citation", "other"), good.Replace("start_index\":0", "start_index\":-1"),
            "{\"status\":\"completed\",\"output_text\":\"I searched the web\",\"steps\":[]}",
            ResearchFixture("", "https://studio.example/work"),
            good.Replace("\"call_id\":\"search\"", "\"is_error\":true,\"call_id\":\"search\"") })
            Assert.Throws<InvalidDataException>(() => GuideResearch.Parse(bad, ResearchDate));
        Assert.Throws<InvalidDataException>(() => GuideResearch.Parse(new string('x', GuideResearch.MaxResponseBytes + 1), ResearchDate));
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Settings.json")]
    [InlineData("https://localhost/secret")]
    [InlineData("https://127.0.0.1/secret")]
    [InlineData("http://studio.example/")]
    [InlineData("https://user:password@studio.example/")]
    public void GuideResearchRejectsUnsafeCitationDestinations(string url) => Assert.Throws<InvalidDataException>(() => GuideResearch.Parse(ResearchFixture(url: url), ResearchDate));
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<a href='jav&#x61;script:alert(1)'>bad</a>")]
    [InlineData("<a href='file:///C:/secret'>bad</a>")]
    [InlineData("<div onclick='bad()'>bad</div>")]
    public void GuideResearchNeverRunsUnsafeSearchSuggestionMarkup(string html) => Assert.Throws<InvalidDataException>(() => GuideResearch.Parse(ResearchFixture(suggestions: html), ResearchDate));
    private sealed class ResearchHandler : HttpMessageHandler
    {
        internal int Calls; internal string RequestBody = ""; internal HttpStatusCode Code = HttpStatusCode.OK; internal string Response = ResearchFixture();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", request.RequestUri!.ToString());
            RequestBody = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(Code) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }
    [Fact]
    public async Task GuideResearchRequestUsesSelectedModelGoogleSearchNoStorageAndNoUngroundedRetry()
    {
        var handler = new ResearchHandler(); using var http = new HttpClient(handler);
        var result = await GuideResearch.AskAsync(http, "test-key-0123456789", "Avatar", "2026 film", "provided", "gemini-4-argon", default);
        using var doc = JsonDocument.Parse(handler.RequestBody); var body = doc.RootElement;
        Assert.Equal("gemini-4-argon", body.GetProperty("model").GetString()); Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal("google_search", Assert.Single(body.GetProperty("tools").EnumerateArray()).GetProperty("type").GetString());
        Assert.Single(result.Citations); Assert.Equal(1, handler.Calls); Assert.Contains("public work title", body.GetProperty("input").GetString());
        handler.Code = HttpStatusCode.BadRequest;
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuideResearch.AskAsync(http, "test-key-0123456789", "Avatar", "", "", "gemini-4-argon", default)); Assert.Equal(2, handler.Calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GuideResearch.AskAsync(http, "test-key-0123456789", "Avatar", "", "", "gemini-4-argon", new CancellationToken(true))); Assert.Equal(2, handler.Calls);
        handler.Code = HttpStatusCode.OK; handler.Response = ResearchFixture().Replace("gemini-4-argon", "gemini-other");
        await Assert.ThrowsAsync<InvalidDataException>(() => GuideResearch.AskAsync(http, "test-key-0123456789", "Avatar", "", "", "gemini-4-argon", default)); Assert.Equal(3, handler.Calls);
    }
}
