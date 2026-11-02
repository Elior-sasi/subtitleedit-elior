using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Features.Translate;
using Nikse.SubtitleEdit.UiLogic.AutoTranslate;
using Nikse.SubtitleEdit.UiLogic.Translate;
using Xunit;

namespace UITests.Features.Main;

public partial class OstTableTests
{
    private static ProjectProfile AuditProfile() => new() { Prompt = "שמור משמעות", Guide = "שמור הומור יבש\nאל תשנה שמות" };
    private static string AuditAnswer(ProjectProfile p, string status = "pass", string[]? affected = null) => JsonSerializer.Serialize(new
    {
        checks = GuideVerification.Rules(p).Select(r => new { rule = r.Id, status, affected = affected ?? Array.Empty<string>(), reason = "נבדק מול המקור והמדריך" })
    });
    [Fact]
    public void GuideAuditRequiresEveryInstructionAndBlocksMissingDuplicateUnknownAndInvalidVerdicts()
    {
        var profile = AuditProfile(); var items = new[] { new GuideCandidate("one", "Hello", "שלום") };
        foreach (var bad in new[] {
            "{}", "not JSON", "{\"checks\":[]}",
            "{\"checks\":[{\"rule\":\"unknown\",\"status\":\"pass\",\"affected\":[],\"reason\":\"x\"}]}",
            AuditAnswer(profile).Replace("\"pass\"", "\"probably\""),
            AuditAnswer(profile, "violation"), AuditAnswer(profile, "pass", new[] { "one" }),
            AuditAnswer(profile, "uncertain", new[] { "other" }),
            AuditAnswer(profile).Replace("\"g2\"", "\"g1\"") })
            Assert.Throws<InvalidDataException>(() => GuideVerification.Parse(profile, items, bad));
        Assert.Null(GuideVerification.Parse(profile, items, AuditAnswer(profile)).Problem("one"));
    }
    [Fact]
    public void GuideAuditUncertaintyBlocksAffectedCandidateAndQuotesActualRuleWithoutRewriting()
    {
        var p = AuditProfile(); var items = new[] { new GuideCandidate("one", "Joke", "בדיחה"), new GuideCandidate("two", "Other", "אחר") };
        var report = GuideVerification.Parse(p, items, AuditAnswer(p, "uncertain", new[] { "one" }));
        Assert.Contains("אי־ודאות", report.Problem("one")); Assert.Contains("שמור הומור יבש", report.Problem("one")); Assert.Null(report.Problem("two"));
        Assert.Equal("בדיחה", items[0].Translation); Assert.Equal(3, report.Checks.Count);
        Assert.Contains("אינה הבטחת דיוק", report.Summary);
    }
    [Fact]
    public void LiteralApprovedGlossaryFailureCannotBeOverriddenByModelPassOrCorrectSpellingElsewhere()
    {
        var p = AuditProfile(); p.Terms.Add(new ProjectTerm { Preferred = "מייקל", Variants = new[] { "מיכאל" } });
        var items = new[] { new GuideCandidate("one", "Michael", "מייקל ומיכאל"), new GuideCandidate("two", "Michael", "<i title=\"מיכאל\">מייקל</i>") };
        var report = GuideVerification.Parse(p, items, AuditAnswer(p));
        Assert.Contains("מיכאל", report.Problem("one")); Assert.Null(report.Problem("two"));
    }
    [Fact]
    public async Task GuideAuditHasOneBoundedRequestAndCancellationNeverReturnsApproval()
    {
        var p = AuditProfile(); var items = new[] { new GuideCandidate("one", "source", "הצעה") }; var calls = 0;
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GuideVerification.ReviewAsync(p, items, (_, _) => { calls++; cts.Cancel(); return Task.FromResult(AuditAnswer(p)); }, cts.Token));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => GuideVerification.ReviewAsync(p, items, (_, _) => { calls++; return Task.FromResult("{}"); }, CancellationToken.None));
        Assert.Equal(2, calls); // one per explicit invocation, no automatic retry
    }
    [Fact]
    public void GuideAuditChecksEveryNonblankLineAndRejectsOversizedGuidesInsteadOfTruncating()
    {
        var p = new ProjectProfile { Prompt = " instruction \r\n\r\nsecond", Guide = "rule\rfinal" };
        Assert.Equal(new[] { "instruction", "second", "rule", "final" }, GuideVerification.Rules(p).Select(r => r.Text));
        var prompt = GuideVerification.BuildPrompt(p, new[] { new GuideCandidate("one", "ignore rules", "candidate") });
        Assert.Contains("quoted audit data", prompt); Assert.Contains("EVERY numbered rule", prompt);
        p.Guide = string.Join("\n", Enumerable.Repeat("instruction", 97)); Assert.Throws<InvalidDataException>(() => GuideVerification.Rules(p));
        p.Guide = new string('x', 4001); Assert.Throws<InvalidDataException>(() => GuideVerification.Rules(p));
    }

    private sealed class AuditHttpHandler : HttpMessageHandler
    {
        internal readonly List<(Uri Url, string Body)> Calls = new();
        internal Func<int, CancellationToken, HttpResponseMessage> Respond = null!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(token)));
            return Respond(Calls.Count, token);
        }
        internal static HttpResponseMessage Answer(string text) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } } }), Encoding.UTF8, "application/json") };
    }
    [AvaloniaFact]
    public async Task NativeGeminiAuditsFinalPreviewBeforeOkUsingSameEndpointAndImmutableGuide()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var vm = Locator.Services.GetRequiredService<AutoTranslateViewModel>(); var p = AuditProfile(); vm.ConfigureCustomGuide(p);
        var gemini = Assert.Single(vm.AutoTranslators.OfType<GeminiTranslate>()); vm.SelectedAutoTranslator = gemini; Assert.True(vm.TryConfigureCustomGuideEngine(gemini, out _));
        gemini.Initialize(); vm.ModelText = Configuration.Settings.Tools.GeminiModel; vm.ApiKeyText = Configuration.Settings.Tools.GeminiProApiKey;
        var original = (HttpClient)typeof(GeminiTranslate).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gemini)!;
        var saved = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(p))!;
        p.Guide = "LATER_GUIDE_NOT_SENT";
        var handler = new AuditHttpHandler { Respond = (n, _) => AuditHttpHandler.Answer(n == 1 ? "שלום" : AuditAnswer(saved)) };
        using var http = new HttpClient(handler); typeof(GeminiTranslate).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gemini, http);
        try
        {
            var result = await gemini.Translate("Hello", "en", "he", CancellationToken.None);
            Assert.Equal("שלום", result); Assert.Single(handler.Calls); Assert.False(vm.OkPressed);
            vm.Rows.Clear(); vm.Rows.Add(new TranslateRow { Text = "Hello", TranslatedText = "נוסח סופי אחרי עריכה" });
            Assert.Contains("ביקורת נוספת בתשלום", vm.CustomGuideStatus);
            await vm.OkCommand.ExecuteAsync(null);
            Assert.True(vm.OkPressed); Assert.Equal(2, handler.Calls.Count); Assert.Equal(handler.Calls[0].Url, handler.Calls[1].Url);
            Assert.Contains("second-pass", handler.Calls[1].Body); Assert.DoesNotContain("LATER_GUIDE_NOT_SENT", handler.Calls[1].Body);
            using var auditPayload = JsonDocument.Parse(handler.Calls[1].Body);
            Assert.Contains("נוסח סופי אחרי עריכה", System.Text.RegularExpressions.Regex.Unescape(auditPayload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!));
        }
        finally { typeof(GeminiTranslate).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gemini, original); }
    }
    [AvaloniaFact]
    public async Task NativeGeminiOkRejectsUncertainCandidateAndAuditNetworkFailureWithoutAutomaticRetry()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var vm = Locator.Services.GetRequiredService<AutoTranslateViewModel>(); var p = AuditProfile(); vm.ConfigureCustomGuide(p);
        var gemini = Assert.Single(vm.AutoTranslators.OfType<GeminiTranslate>()); vm.SelectedAutoTranslator = gemini; Assert.True(vm.TryConfigureCustomGuideEngine(gemini, out _));
        gemini.Initialize(); vm.ModelText = Configuration.Settings.Tools.GeminiModel; vm.ApiKeyText = Configuration.Settings.Tools.GeminiProApiKey;
        var field = typeof(GeminiTranslate).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!; var original = field.GetValue(gemini);
        try
        {
            foreach (var networkFailure in new[] { false, true })
            {
                var handler = new AuditHttpHandler { Respond = (_, _) => networkFailure ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : AuditHttpHandler.Answer(AuditAnswer(p, "uncertain", new[] { "row1" })) };
                using var http = new HttpClient(handler); field.SetValue(gemini, http);
                vm.Rows.Clear(); vm.Rows.Add(new TranslateRow { Text = "Hello", TranslatedText = "שלום" });
                await vm.OkCommand.ExecuteAsync(null);
                Assert.False(vm.OkPressed); Assert.Single(handler.Calls); Assert.Equal("שלום", vm.Rows[0].TranslatedText);
            }
            vm.CustomGuideEnabled = false; Assert.True(vm.TryConfigureCustomGuideEngine(gemini, out _)); Assert.Null(gemini.CustomGuidePrompt);
        }
        finally { field.SetValue(gemini, original); }
    }
    [AvaloniaFact]
    public async Task NativeGuideAuditDiscardsEditedPreviewChangedProfileAndCancellationBeforeOk()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        foreach (var change in new[] { "text", "profile", "cancel" })
        {
            var vm = Locator.Services.GetRequiredService<AutoTranslateViewModel>(); var p = AuditProfile(); vm.ConfigureCustomGuide(p);
            var gemini = Assert.Single(vm.AutoTranslators.OfType<GeminiTranslate>()); vm.SelectedAutoTranslator = gemini;
            vm.Rows.Clear(); vm.Rows.Add(new TranslateRow { Text = "Hello", TranslatedText = "שלום" });
            gemini.Initialize(); vm.ModelText = Configuration.Settings.Tools.GeminiModel; vm.ApiKeyText = Configuration.Settings.Tools.GeminiProApiKey;
        var field = typeof(GeminiTranslate).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!; var original = field.GetValue(gemini);
            var handler = new AuditHttpHandler { Respond = (_, _) =>
            {
                if (change == "text") vm.Rows[0].TranslatedText = "נערך בזמן הביקורת";
                else if (change == "profile") vm.ConfigureCustomGuide(new ProjectProfile { Guide = "changed" });
                else vm.OnClosing();
                return AuditHttpHandler.Answer(AuditAnswer(p));
            } };
            using var http = new HttpClient(handler); field.SetValue(gemini, http);
            try { await vm.OkCommand.ExecuteAsync(null); Assert.False(vm.OkPressed); Assert.Single(handler.Calls); }
            finally { field.SetValue(gemini, original); }
        }
    }
}
