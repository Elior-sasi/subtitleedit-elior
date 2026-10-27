using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Xunit;

namespace UITests.Features.Main;

public partial class OstTableTests
{
    private static ProjectWork BatchWork() => new() { AlignmentConfirmed = true, SourceAnchor1 = 0, TargetAnchor1 = 1000, ReferenceText = "" }; // English is 1 s earlier

    [Fact]
    public void BatchPairingUsesConfirmedTimeAndAsksWhenAmbiguous()
    {
        var english = new List<SourceCue>
        {
            new(0, 1500, "<i>Hello there.</i>"),   // -> 1.0..2.5
            new(3000, 4000, "First part"),           // -> 4..5
            new(4000, 5000, "second part"),          // -> 5..6
            new(8000, 10000, "Shared sentence"),     // -> 9..11
        };
        var rows = new List<BatchRow>
        {
            new(Guid.NewGuid(), 1000, 2500, "", "", ""),          // one cue -> proposed
            new(Guid.NewGuid(), 4000, 6000, "טיוטה", "", ""),      // two cues -> choose
            new(Guid.NewGuid(), 9000, 10000, "", "", ""),         // shared cue -> choose
            new(Guid.NewGuid(), 10000, 11000, "", "", ""),
            new(Guid.NewGuid(), 20000, 21000, "", "", ""),        // nothing -> choose or skip
        };
        var pairs = CustomTranslateBatch.Pair(BatchWork(), rows, english);
        Assert.Equal("<i>Hello there.</i>", pairs[0].Proposed!.Text);
        Assert.All(pairs.Skip(1), p => Assert.Null(p.Proposed));
        Assert.Contains(pairs[1].Candidates, c => c.Text == "second part");
        // Reordering rows never changes which source a row gets: pairing is by time only.
        var reversed = CustomTranslateBatch.Pair(BatchWork(), rows.AsEnumerable().Reverse().ToList(), english);
        Assert.Equal("<i>Hello there.</i>", reversed.Single(p => p.Row.Id == rows[0].Id).Proposed!.Text);
        Assert.Throws<InvalidOperationException>(() => CustomTranslateBatch.Pair(new ProjectWork(), rows, english));
    }

    [Fact]
    public void BatchPromptUsesGuideGlossaryMemoryAndParsingRejectsUnsafeAnswers()
    {
        var profile = new ProjectProfile { Guide = "GUIDE-RULE", Prompt = "PROJECT-PROMPT", Terms = { new ProjectTerm { Preferred = "אסיף", Source = "Asif" } } };
        var memory = new TranslationMemoryEntry { English = "Hello there.", Hebrew = "שלום לך." };
        var rowA = new BatchRow(Guid.NewGuid(), 0, 1, "", "before", "after");
        var rowB = new BatchRow(Guid.NewGuid(), 2, 3, "<i>טיוטה</i>", "", "");
        var items = new List<BatchItem> { new("r1", rowA, "<i>Hello there.</i>", new() { memory }), new("r2", rowB, "Go.", new()) };
        var prompt = CustomTranslateBatch.BuildPrompt(profile, items);
        foreach (var expected in new[] { "GUIDE-RULE", "PROJECT-PROMPT", "Asif", "שלום לך.", "<i>טיוטה</i>", "\"key\":\"r2\"" }) Assert.Contains(expected, prompt);
        Assert.DoesNotContain(rowA.Id.ToString(), prompt);
        var ok = CustomTranslateBatch.Parse("{\"translations\":[{\"key\":\"r2\",\"text\":\"<i>לך.</i>\",\"note\":\"\"},{\"key\":\"r1\",\"text\":\"<i>שלום לך.</i>\",\"note\":\"זיכרון\"}]}", items);
        Assert.Equal(new[] { "r1", "r2" }, ok.Select(p => p.Key));
        Assert.All(ok, p => Assert.Null(p.Problem));
        var tags = CustomTranslateBatch.Parse("{\"translations\":[{\"key\":\"r1\",\"text\":\"שלום\"},{\"key\":\"r2\",\"text\":\"<b>לך</b>\"}]}", items);
        Assert.All(tags, p => Assert.NotNull(p.Problem));
        var missing = CustomTranslateBatch.Parse("{\"translations\":[{\"key\":\"r1\",\"text\":\"<i>שלום</i>\"}]}", items);
        Assert.NotNull(missing.Single(p => p.Key == "r2").Problem);
        Assert.Throws<InvalidDataException>(() => CustomTranslateBatch.Parse("{\"translations\":[{\"key\":\"r9\",\"text\":\"x\"}]}", items));
        Assert.Throws<InvalidDataException>(() => CustomTranslateBatch.Parse("{\"translations\":[{\"key\":\"r1\",\"text\":\"<i>a</i>\"},{\"key\":\"r1\",\"text\":\"<i>b</i>\"}]}", items));
        Assert.NotNull(CustomTranslateBatch.Parse("{\"translations\":[{\"key\":\"r1\",\"text\":\"<i>a</i>\\n\\n00:00:01,000 --> 00:00:02,000\"},{\"key\":\"r2\",\"text\":\"<i>b</i>\"}]}", items)[0].Problem);
        Assert.Throws<InvalidOperationException>(() => CustomTranslateBatch.BuildPrompt(profile, Enumerable.Range(1, 41).Select(i => new BatchItem("r" + i, rowA, "x", new())).ToList()));
    }

    [Fact]
    public void BatchCostEstimateOnlyForVerifiedPriceAndModelIdsAreValidated()
    {
        Assert.Contains("$", CustomTranslateBatch.Estimate("gemini-4-argon", new string('x', 3000), 32768, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal((4.0, 20.0), CustomTranslateBatch.Price("gemini-4-argon", new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Null(CustomTranslateBatch.Price("gemini-other", DateTime.UtcNow));
        Assert.DoesNotContain("$", CustomTranslateBatch.Estimate("gemini-other", "abc", 100, DateTime.UtcNow));
        Assert.False(CustomTranslateBatch.IsValidModelId("../models"));
        Assert.True(CustomTranslateBatch.IsValidModelId("gemini-4-argon"));
    }

    private sealed class GeminiModelsHttp : HttpMessageHandler
    {
        internal bool ArgonAvailable; internal readonly List<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Urls.Add(request.RequestUri!.ToString());
            Assert.Equal("test-key-0123456789", request.Headers.GetValues("x-goog-api-key").Single());
            Assert.DoesNotContain("key=", request.RequestUri!.Query); // key never in the URL
            if (request.RequestUri.AbsolutePath.EndsWith("/models/gemini-4-argon"))
                return Task.FromResult(ArgonAvailable ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"models/gemini-4-argon\",\"displayName\":\"Gemini 4 Argon\",\"inputTokenLimit\":1048576,\"outputTokenLimit\":65536}") } : new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"name\":\"models/gemini-3-flash\",\"displayName\":\"Gemini 3 Flash\"},{\"name\":\"models/embedding-1\"}]}") });
        }
    }

    [Fact]
    public async Task GeminiModelCheckReportsUnavailableWithoutSubstituting()
    {
        var handler = new GeminiModelsHttp(); using var http = new HttpClient(handler);
        Assert.Null(await CustomTranslateBatch.GetModelAsync(http, "test-key-0123456789", "gemini-4-argon", default));
        Assert.Equal(new[] { "gemini-3-flash" }, (await CustomTranslateBatch.ListModelsAsync(http, "test-key-0123456789", default)).Select(m => m.Id));
        handler.ArgonAvailable = true;
        Assert.Equal(1048576, (await CustomTranslateBatch.GetModelAsync(http, "test-key-0123456789", "gemini-4-argon", default))!.InputTokenLimit);
    }

    private sealed class FailingGemini : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task GeminiFailureIsSingleRequestAndCancellationStops()
    {
        var handler = new FailingGemini(); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CustomProject.AskAsync(http, "test-key-0123456789", "x", default, "gemini-4-argon"));
        Assert.Equal(1, handler.Calls); // no automatic resend that could bill twice
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CustomProject.AskAsync(http, "test-key-0123456789", "x", cts.Token, "gemini-4-argon"));
        await Assert.ThrowsAsync<InvalidDataException>(() => CustomProject.AskAsync(http, "test-key-0123456789", "x", default, "bad model/../x"));
    }

    [AvaloniaFact]
    public void BatchApplyNeedsFreshRowsKeepsIdsTimesTagsAndUndoesInOneStep()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var a = new SubtitleLineViewModel { Number = 1, Text = "", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) };
            var b = new SubtitleLineViewModel { Number = 2, Text = "<i>טיוטה</i>", StartTime = TimeSpan.FromSeconds(4), EndTime = TimeSpan.FromSeconds(6) };
            var c = new SubtitleLineViewModel { Number = 3, Text = "לא נגעו בי", StartTime = TimeSpan.FromSeconds(7), EndTime = TimeSpan.FromSeconds(8) };
            vm.Subtitles.Clear(); vm.Subtitles.Add(a); vm.Subtitles.Add(b); vm.Subtitles.Add(c);
            typeof(MainViewModel).GetField("_subtitleFileName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, "test.srt");
            var sent = new List<MainViewModel.BatchSnapshot> { new(a.Id, ""), new(b.Id, "<i>טיוטה</i>") };
            // stale: b changed after sending -> nothing applied at all
            b.Text = "<i>טיוטה ערוכה</i>";
            Assert.Throws<InvalidOperationException>(() => vm.ApplyBatchTranslations("test.srt", sent, new[] { (a.Id, "שלום"), (b.Id, "<i>לך</i>") }));
            Assert.Equal("", a.Text);
            b.Text = "<i>טיוטה</i>";
            Assert.Throws<InvalidDataException>(() => vm.ApplyBatchTranslations("test.srt", sent, new[] { (b.Id, "לך") })); // tag removed
            Assert.Throws<InvalidOperationException>(() => vm.ApplyBatchTranslations("other.srt", sent, new[] { (a.Id, "שלום") }));
            var ids = vm.Subtitles.Select(r => (r.Id, r.StartTime, r.EndTime)).ToList();
            vm.ApplyBatchTranslations("test.srt", sent, new[] { (a.Id, "שלום"), (b.Id, "<i>לך</i>") });
            Assert.Equal(new[] { "שלום", "<i>לך</i>", "לא נגעו בי" }, vm.Subtitles.Select(r => r.Text));
            Assert.Equal(ids, vm.Subtitles.Select(r => (r.Id, r.StartTime, r.EndTime)).ToList());
            vm.UndoCommand.Execute(null);
            Assert.Equal(new[] { "", "<i>טיוטה</i>", "לא נגעו בי" }, vm.Subtitles.Select(r => r.Text));
        }
        finally { window.Close(); }
    }
}

