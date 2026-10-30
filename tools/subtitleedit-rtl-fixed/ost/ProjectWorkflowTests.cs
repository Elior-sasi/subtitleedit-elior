using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    [Fact]
    public void AutomaticTimingAdvanceDefaultsOffAndRoundTripsWithOtherSettings()
    {
        var settings = new Se();
        Assert.False(settings.Tools.AutoAdvanceOnSetEnd);
        settings.Tools.AutoAdvanceOnSetEnd = true;
        settings.Tools.CustomQc.GlossariesByProfile["client"] = "שם=חלופה";
        var restored = System.Text.Json.JsonSerializer.Deserialize<Se>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Assert.True(restored.Tools.AutoAdvanceOnSetEnd);
        Assert.Equal("שם=חלופה", restored.Tools.CustomQc.GlossariesByProfile["client"]);
        restored.Tools.AutoAdvanceOnSetEnd = false;
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<Se>(System.Text.Json.JsonSerializer.Serialize(restored))!.Tools.AutoAdvanceOnSetEnd);
    }
    [AvaloniaFact]
    public async Task StampEndAdvancesOnlyWhenEnabledAndNeverPastLastRowOrWhenLocked()
    {
        var old = Se.Settings.Tools.AutoAdvanceOnSetEnd;
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        try {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            vm.Subtitles.Clear();
            var first = new SubtitleLineViewModel { Number = 1, Text = "first", StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(5) };
            var second = new SubtitleLineViewModel { Number = 2, Text = "second", StartTime = TimeSpan.FromSeconds(6), EndTime = TimeSpan.FromSeconds(8) };
            vm.Subtitles.Add(first); vm.Subtitles.Add(second);
            void Select(SubtitleLineViewModel row) { vm.SubtitleGrid.SelectedItems?.Clear(); vm.SubtitleGrid.SelectedItem = row; Dispatcher.UIThread.RunJobs(); Nikse.SubtitleEdit.Logic.TableViewExtras.SyncSelectedItemsWithSelection(vm.SubtitleGrid); Assert.Same(row, vm.SelectedSubtitle); Assert.Equal(1, vm.SubtitleGridSelectedCount); }
            Assert.NotNull(vm.GetVideoPlayerControl());
            Select(first); Se.Settings.Tools.AutoAdvanceOnSetEnd = false;
            vm.WaveformSetEndCommand.Execute(null); Assert.Same(first, vm.SelectedSubtitle); Assert.NotEqual(TimeSpan.FromSeconds(5), first.EndTime);
            first.EndTime = TimeSpan.FromSeconds(5); Se.Settings.Tools.AutoAdvanceOnSetEnd = true;
            vm.WaveformSetEndCommand.Execute(null); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); Dispatcher.UIThread.RunJobs(); Assert.Same(second, vm.SelectedSubtitle); Assert.Equal("first", first.Text); Assert.Equal("second", second.Text);
            vm.WaveformSetEndCommand.Execute(null); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); Dispatcher.UIThread.RunJobs(); Assert.Same(second, vm.SelectedSubtitle); Assert.Equal(2, vm.Subtitles.Count);
            Select(first); vm.LockTimeCodes = true; var lockedEnd = first.EndTime;
            vm.WaveformSetEndCommand.Execute(null); Assert.Same(first, vm.SelectedSubtitle); Assert.Equal(lockedEnd, first.EndTime); vm.LockTimeCodes = false;
            vm.SubtitleGrid.SelectionMode = SelectionMode.Multiple;
            vm.SubtitleGrid.SelectedItems!.Add(second); Dispatcher.UIThread.RunJobs();
            vm.WaveformSetEndCommand.Execute(null); Assert.Same(first, vm.SelectedSubtitle);
            Select(first);
            // An explicitly named end-and-next command advances once even with the toggle on.
            vm.WaveformSetEndAndGoToNextCommand.Execute(null); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); Dispatcher.UIThread.RunJobs(); Assert.Same(second, vm.SelectedSubtitle);
        } finally { window.Close(); Se.Settings.Tools.AutoAdvanceOnSetEnd = old; }
    }

    [AvaloniaFact]
    public async Task ExplicitEndAndNextKeepsNextSelectedAtInclusivePlayheadBoundary()
    {
        var old = Se.Settings.Tools.AutoAdvanceOnSetEnd;
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window;
        var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var follow = typeof(MainViewModel).GetMethod("SelectCurrentSubtitleAtPlayhead", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var player = vm.GetVideoPlayerControl()!;
            foreach (var autoAdvance in new[] { false, true })
            {
                Se.Settings.Tools.AutoAdvanceOnSetEnd = autoAdvance;
                vm.Subtitles.Clear();
                var first = new SubtitleLineViewModel { Number = 1, Text = "finished", StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(5) };
                var reference = new SubtitleLineViewModel { Text = "reference", IsReferenceOnly = true };
                var next = new SubtitleLineViewModel { Number = 2, Text = "next", StartTime = TimeSpan.FromSeconds(6), EndTime = TimeSpan.FromSeconds(8) };
                vm.Subtitles.Add(first); vm.Subtitles.Add(reference); vm.Subtitles.Add(next);
                vm.SubtitleGrid.SelectedItems?.Clear(); vm.SubtitleGrid.SelectedItem = first;
                Dispatcher.UIThread.RunJobs(); Nikse.SubtitleEdit.Logic.TableViewExtras.SyncSelectedItemsWithSelection(vm.SubtitleGrid);
                var seconds = player.Position;
                vm.WaveformSetEndAndGoToNextCommand.Execute(null);
                Assert.Same(next, vm.SelectedSubtitle); // selection must be synchronous
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); Dispatcher.UIThread.RunJobs();
                var rows = vm.Subtitles.Where(r => !r.IsReferenceOnly).OrderBy(r => r.StartTime).ToList();
                // The timer used to take the selection straight back at the stamped position.
                follow.Invoke(vm, new object[] { seconds, rows });
                follow.Invoke(vm, new object[] { seconds, rows });
                Dispatcher.UIThread.RunJobs(); Assert.Same(next, vm.SelectedSubtitle);
                Assert.Equal("finished", first.Text); Assert.Equal("next", next.Text);
                // Moving the playhead releases the hold and ordinary follow-selection resumes.
                follow.Invoke(vm, new object[] { 7.0, rows });
                follow.Invoke(vm, new object[] { first.StartTime.TotalSeconds, rows });
                Dispatcher.UIThread.RunJobs(); Assert.Same(first, vm.SelectedSubtitle);
                vm.LockTimeCodes = true; var end = first.EndTime;
                vm.WaveformSetEndAndGoToNextCommand.Execute(null);
                Assert.Same(first, vm.SelectedSubtitle); Assert.Equal(end, first.EndTime); vm.LockTimeCodes = false;
                vm.SubtitleGrid.SelectedItems?.Clear(); vm.SubtitleGrid.SelectedItem = next;
                Dispatcher.UIThread.RunJobs();
                vm.WaveformSetEndAndGoToNextCommand.Execute(null); Dispatcher.UIThread.RunJobs();
                Assert.Same(next, vm.SelectedSubtitle); Assert.Equal(3, vm.Subtitles.Count);
            }
        }
        finally { window.Close(); Se.Settings.Tools.AutoAdvanceOnSetEnd = old; }
    }

    [AvaloniaFact]
    public void PausedWaveformSelectionRepaintsImmediatelyInRtlAndLtrWithBothRenderers()
    {
        var oldRtl = Se.Settings.Appearance.RightToLeft;
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window;
        var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var originalWaveform = vm.AudioVisualizer;
            try
            {
                foreach (var rtl in new[] { false, true })
                foreach (var skia in new[] { false, true })
                {
                    Se.Settings.Appearance.RightToLeft = rtl;
                    vm.Subtitles.Clear();
                    var first = new SubtitleLineViewModel { Number = 1, Text = "כתובית ראשונה", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) };
                    var second = new SubtitleLineViewModel { Number = 2, Text = "כתובית שנייה", StartTime = TimeSpan.FromSeconds(4), EndTime = TimeSpan.FromSeconds(5) };
                    vm.Subtitles.Add(first); vm.Subtitles.Add(second);
                    var av = skia ? new Nikse.SubtitleEdit.Controls.AudioVisualizerControl.SkiaAudioVisualizer() : new Nikse.SubtitleEdit.Controls.AudioVisualizerControl.AudioVisualizer();
                    av.WavePeaks = new Nikse.SubtitleEdit.Logic.Media.WavePeakData2(126, Enumerable.Repeat(new Nikse.SubtitleEdit.Logic.Media.WavePeak2(200, -200), 1260).ToArray());
                    av.WaveformColor = Avalonia.Media.Colors.Green;
                    av.WaveformSelectedColor = Avalonia.Media.Colors.Red;
                    av.ParagraphBackground = Avalonia.Media.Color.FromArgb(40, 70, 70, 70);
                    av.ParagraphSelectedBackground = Avalonia.Media.Color.FromArgb(150, 40, 80, 220);
                    vm.AudioVisualizer = av;
                    var waveformWindow = new Window { Width = 800, Height = 200, Content = av };
                    try
                    {
                        waveformWindow.Show(); waveformWindow.UpdateLayout();
                        vm.SubtitleGrid.SelectedItems?.Clear();
                        av.SetPosition(0, vm.Subtitles.ToList(), 0, -1, new());
                        byte[] Capture()
                        {
                            Dispatcher.UIThread.RunJobs(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                            using var frame = waveformWindow.CaptureRenderedFrame()!;
                            using var stream = new MemoryStream(); frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); return stream.ToArray();
                        }
                        var unselected = Capture();
                        vm.SubtitleGrid.SelectedItem = first;
                        Assert.Contains(first, av.AllSelectedParagraphs); // no player tick or dispatcher wait
                        Assert.Same(first, av.SelectedParagraph);
                        Assert.NotEqual(unselected, Capture());
                        vm.SubtitleGrid.SelectionMode = SelectionMode.Multiple;
                        vm.SubtitleGrid.SelectedItems!.Add(second); Dispatcher.UIThread.RunJobs();
                        Assert.Contains(first, av.AllSelectedParagraphs); Assert.Contains(second, av.AllSelectedParagraphs);
                        vm.SubtitleGrid.SelectedItems.Clear(); Dispatcher.UIThread.RunJobs();
                        Assert.Empty(av.AllSelectedParagraphs); Assert.Null(av.SelectedParagraph);
                    }
                    finally { waveformWindow.Close(); }
                }
            }
            finally { vm.AudioVisualizer = originalWaveform; }
        }
        finally { window.Close(); Se.Settings.Appearance.RightToLeft = oldRtl; }
    }

    [Fact]
    public void RecapMemoryIsProjectScopedAndIndependentOfTimestampsAndNeverAutoApplies()
    {
        var profile = new ProjectProfile();
        ProjectArchive.Approve(profile, "We will find a way home!", "נמצא דרך הביתה!", "Avatar S01E01.srt");
        ProjectArchive.Approve(profile, "We will find a way home!", "נמצא דרך הביתה!", "Avatar S01E01.srt");
        Assert.Single(profile.Memory);
        var exact = Assert.Single(ProjectArchive.Find(profile.Memory, "We will find a way home?"));
        Assert.Equal(1, exact.Similarity); Assert.Equal("Avatar S01E01.srt", exact.Entry.Document);
        Assert.NotEmpty(ProjectArchive.Find(profile.Memory, "We will find our way home"));
        Assert.Empty(ProjectArchive.Find(profile.Memory, "No"));
        Assert.Empty(ProjectArchive.Find(new ProjectProfile().Memory, "We will find a way home!"));
        Assert.Equal("נמצא דרך הביתה!", profile.Memory[0].Hebrew);
    }
    [Fact]
    public void VersionedFolderBackupsRoundTripNotesOstAndGuideWithoutSettingsOrOverwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var subtitle = Path.Combine(root, "episode.srt"); File.WriteAllText(subtitle, "original bytes");
            var files = new System.Collections.Generic.Dictionary<string, string> { ["subtitles.srt"] = "edited snapshot", ["project.sework.json"] = "{\"Notes\":[]}", ["table.ostproject"] = "{\"ProjectTitle\":\"כותרת\"}", ["guide-and-memory.json"] = "{\"Name\":\"סדרה\"}" };
            var first = ProjectArchive.WriteSnapshot(root, subtitle, files); var second = ProjectArchive.WriteSnapshot(root, subtitle, files);
            Assert.NotEqual(first, second); Assert.True(File.Exists(first));
            var restored = ProjectArchive.RestoreSnapshot(first, root);
            Assert.Equal("edited snapshot", File.ReadAllText(Path.Combine(restored, "subtitles.srt")));
            Assert.Equal(files["table.ostproject"], File.ReadAllText(Path.Combine(restored, "subtitles.ostproject")));
            Assert.True(File.Exists(Path.Combine(restored, "subtitles.sework.json")));
            Assert.False(File.Exists(Path.Combine(restored, "Settings.json")));
            Assert.Equal("original bytes", File.ReadAllText(subtitle));
            Assert.Equal(ProjectArchive.Fingerprint(files), ProjectArchive.Fingerprint(files));
            files["subtitles.srt"] = "different"; Assert.NotEqual(ProjectArchive.Fingerprint(files), ProjectArchive.Fingerprint(new System.Collections.Generic.Dictionary<string, string> { ["subtitles.srt"] = "edited snapshot" }));
            files["Settings.json"] = "secret"; Assert.Throws<InvalidDataException>(() => ProjectArchive.WriteSnapshot(root, subtitle, files));
        } finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void BackupRejectsCorruptionAndTraversalBeforeCreatingRestoredDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var files = new System.Collections.Generic.Dictionary<string, string> { ["subtitles.srt"] = "safe" };
            var archive = ProjectArchive.WriteSnapshot(root, Path.Combine(root, "movie.srt"), files);
            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Update)) { zip.GetEntry("subtitles.srt")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("subtitles.srt").Open()); writer.Write("corrupt"); }
            Assert.Throws<InvalidDataException>(() => ProjectArchive.RestoreSnapshot(archive, root));
            Assert.Empty(Directory.GetDirectories(root, "SubtitleEdit-Restored-*"));
            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Update)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("bad"); }
            Assert.Throws<InvalidDataException>(() => ProjectArchive.RestoreSnapshot(archive, root)); Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        } finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task FolderBackupCapturesUnsavedEditorWithoutChangingDocumentOrOriginalFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var originalOffset = Se.Settings.General.CurrentVideoOffsetInMs; Se.Settings.General.CurrentVideoOffsetInMs = 1000;
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        try {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var document = Path.Combine(root, "movie.srt"); File.WriteAllText(document, "original file bytes");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(MainViewModel).GetField("_subtitleFileName", flags)!.SetValue(vm, document);
            typeof(MainViewModel).GetField("_projectBackupSettings", flags)!.SetValue(vm, new ProjectBackupSettings { Folder = root });
            vm.Subtitles.Clear(); vm.Subtitles.Add(new SubtitleLineViewModel { Number = 1, Text = "עריכה שטרם נשמרה!", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) });
            var hash = vm.GetFastHash(); await vm.BackupProjectNow(true);
            var archive = Assert.Single(Directory.GetFiles(Path.Combine(root, "SubtitleEdit-Elior-Backups"), "*.zip", SearchOption.AllDirectories));
            var restored = ProjectArchive.RestoreSnapshot(archive, root);
            Assert.Contains("עריכה שטרם נשמרה!", File.ReadAllText(Path.Combine(restored, "subtitles.srt")));
            Assert.True(File.Exists(Path.Combine(restored, "subtitles.ostproject")));
            Assert.Contains("00:00:02,000 --> 00:00:04,000", File.ReadAllText(Path.Combine(restored, "subtitles.srt")));
            Assert.Equal(hash, vm.GetFastHash()); Assert.Equal("original file bytes", File.ReadAllText(document));
            await vm.BackupProjectNow(false); Assert.Single(Directory.GetFiles(Path.Combine(root, "SubtitleEdit-Elior-Backups"), "*.zip", SearchOption.AllDirectories));
        } finally { window.Close(); Se.Settings.General.CurrentVideoOffsetInMs = originalOffset; Directory.Delete(root, true); }
    }
    [Fact]
    public void BackupRestoreAdaptsNoteAndReferenceAnchorsToCurrentVideoOffset()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var work = new ProjectWork { Notes = new() { new WorkNote { Text = "שורה", Start = 1000, End = 2000, Note = "הערה" } }, TargetAnchor1 = 3000, TargetAnchor2 = 4000 };
            var files = new System.Collections.Generic.Dictionary<string, string> { ["subtitles.srt"] = "timed with saved offset", ["subtitles.ass"] = "ASS styles and comments", ["project.sework.json"] = System.Text.Json.JsonSerializer.Serialize(work) };
            var archive = ProjectArchive.WriteSnapshot(root, Path.Combine(root, "movie.ass"), files, 2000);
            var restored = ProjectArchive.RestoreSnapshot(archive, root, 500);
            var actual = CustomProject.Load<ProjectWork>(Path.Combine(restored, "subtitles.sework.json"));
            Assert.Equal(2500, actual.Notes[0].Start); Assert.Equal(3500, actual.Notes[0].End); Assert.Equal(4500, actual.TargetAnchor1); Assert.Equal(1000, work.Notes[0].Start);
            Assert.Equal("ASS styles and comments", File.ReadAllText(Path.Combine(restored, "subtitles.ass")));
        } finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("Avatar S02E01.srt")]
    [InlineData("AvAtar E01 S02.srt")]
    [InlineData("Avatar.S02.E01.Hebrew.srt")]
    public void ProjectMatchingAcceptsBothEpisodeOrdersAndRejectsAmbiguity(string name)
    {
        var p = new ProjectProfile { Alias = "Avatar" };
        Assert.Single(CustomProject.Match(new[] { p }, name));
        Assert.Equal(2, CustomProject.Match(new[] { p, new ProjectProfile { Alias = "avatar" } }, name).Count);
        Assert.Empty(CustomProject.Match(new[] { p }, "AvatarTheMovie.srt"));
    }
    [Fact]
    public void ReferenceMappingLeavesSourceAndHebrewUnchangedAndOffersNearbyCues()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,000\nHello there\n\n2\n00:00:03,000 --> 00:00:04,000\nGoodbye\n";
        var work = new ProjectWork { ReferenceText = srt, SourceAnchor1 = 1000, TargetAnchor1 = 3000 };
        var cues = CustomProject.Candidates(work, 3000, 4000);
        Assert.Equal(2, cues.Count); Assert.Equal("Hello there", cues[0].Text); Assert.Equal(srt, work.ReferenceText);
        Assert.Equal(5000, work.Map(3000)); work.SourceAnchor2 = 3000; work.TargetAnchor2 = 7000;
        Assert.Equal(5000, work.Map(2000));
        work.TargetAnchor2 = 1000; Assert.Throws<InvalidDataException>(() => work.Map(2000));
    }
    [Fact]
    public void ProjectSidecarPersistsPrivateNotesAndExceptionsWithoutTouchingSubtitle()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var subtitle = Path.Combine(root, "movie.srt"); File.WriteAllText(subtitle, "subtitle bytes");
            var n = new WorkNote { Id = Guid.NewGuid(), Text = "אסיפ", Start = 1000, End = 2000, Note = "לבדוק מול הבמאי", NeedsReview = true, ExceptionText = "אסיפ", ExceptionReason = "בדיחה" };
            var work = new ProjectWork { Notes = new() { n }, ProfileId = "movie" }; var path = Path.ChangeExtension(subtitle, ".sework.json"); CustomProject.Save(path, work);
            var restored = CustomProject.Load<ProjectWork>(path); var newId = Guid.NewGuid();
            Assert.Equal("לבדוק מול הבמאי", CustomProject.FindNote(restored, newId, "אסיפ", 1000, 2000)!.Note);
            Assert.Equal("בדיחה", restored.Notes[0].ExceptionReason); Assert.True(restored.Notes[0].NeedsReview);
            Assert.Null(CustomProject.FindNote(restored, Guid.NewGuid(), "אסיפ", 9000, 10000));
            Assert.Equal("subtitle bytes", File.ReadAllText(subtitle));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void GuideAndApprovedTermsAreIncludedLiterallyAndDoNotRewriteAnything()
    {
        var p = new ProjectProfile { Guide = "בלי ריכוך", Prompt = "עברית טבעית", Terms = new() { new ProjectTerm { Preferred = "אסיף", Variants = new[] { "אסיפ" } } } };
        var prompt = CustomProject.BuildPrompt(p, "review", "אסיפ הגיע"); Assert.Contains("בלי ריכוך", prompt); Assert.Contains("עברית טבעית", prompt);
        Assert.Single(CustomProject.TermWarnings(p, "אסיפ הגיע")); Assert.Empty(CustomProject.TermWarnings(p, "אסיפון הגיע"));
        Assert.Equal("אסיף הגיע", CustomProject.ReplaceTerm("אסיפ הגיע", "אסיפ", "אסיף"));
        Assert.Equal("אסיף", p.Terms[0].Preferred);
    }
    private sealed class ProjectHttp : HttpMessageHandler
    {
        internal string Body = ""; internal string Key = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token); Key = request.Headers.GetValues("x-goog-api-key").Single();
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"completed\",\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"suggestions\\\":[{\\\"text\\\":\\\"שלום\\\",\\\"reason\\\":\\\"מדויק\\\"}]}\"}]}]}") };
        }
    }
    [Fact]
    public async Task GeminiUsesExplicitRequestAndParsesOffersWithoutApplyingThem()
    {
        var handler = new ProjectHttp(); using var client = new HttpClient(handler);
        var result = await CustomProject.AskAsync(client, "test-not-a-real-key", "guide and source", CancellationToken.None);
        Assert.Contains("gemini-4-argon", handler.Body); Assert.Equal("test-not-a-real-key", handler.Key);
        Assert.Equal("שלום", Assert.Single(CustomProject.Offers(result)).Text);
        var id = Guid.NewGuid(); Assert.True(CustomProject.CanApply("old", "old", id, id, "a", "a"));
        Assert.False(CustomProject.CanApply("old", "edited", id, id, "a", "a"));
        Assert.False(CustomProject.CanApply("old", "old", id, id, "a", "b"));
    }
    [AvaloniaFact]
    public void OstMetadataAndFilterLeaveRowsNotesAndDocumentHashIntact()
    {
        var view = MakeWindow(); try { view.SetDetails("סרט", "", ""); Assert.Equal("סרט", view.GetDetails().Title); Assert.Empty(view.GetDetails().Season); } finally { view.TrySaveAndStop(); }
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var row = new SubtitleLineViewModel { Number = 1, Text = "טקסט מקורי", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) };
            vm.Subtitles.Clear(); vm.Subtitles.Add(row); var hash = vm.GetFastHash();
            row.CustomWorkMarker = " ✎ ◇"; row.IsOstFilterExcluded = true;
            Assert.True(row.IsCustomRowHidden); Assert.Contains("✎", row.NumberDisplay); Assert.Equal(hash, vm.GetFastHash()); Assert.Single(vm.Subtitles);
            row.IsOstFilterExcluded = false; Assert.False(row.IsCustomRowHidden); row.IsHidden = true; Assert.True(row.IsCustomRowHidden);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void ApplyingApprovedOfferChecksStalenessAndNeverChangesTimes()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var row = new SubtitleLineViewModel { Number = 1, Text = "ישן", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) }; vm.Subtitles.Clear(); vm.Subtitles.Add(row);
            typeof(MainViewModel).GetField("_subtitleFileName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, "test.srt");
            Assert.Throws<InvalidOperationException>(() => vm.ApplyCustomOffer(row.Id, "wrong", "test.srt", "חדש")); Assert.Equal("ישן", row.Text);
            vm.ApplyCustomOffer(row.Id, "ישן", "test.srt", "חדש"); Assert.Equal("חדש", row.Text); Assert.Equal(TimeSpan.FromSeconds(1), row.StartTime); Assert.Equal(TimeSpan.FromSeconds(3), row.EndTime);
            vm.UndoCommand.Execute(null); Assert.Equal("ישן", vm.Subtitles[0].Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SettingsRestoreBacksUpCurrentSettingsAndRejectsInvalidFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var original = Se.Settings; var originalPath = Se.SettingsFilePathOverride;
        var backups = Path.Combine(Se.DataFolder, "SettingsBackups"); var prior = Directory.Exists(backups) ? Directory.GetFiles(backups).ToHashSet() : new System.Collections.Generic.HashSet<string>();
        try
        {
            Se.SettingsFilePathOverride = Path.Combine(root, "Settings.json"); Se.Settings = new Se(); Se.Settings.General.SubtitleLineMaximumLength = 41; Se.SaveSettings();
            var first = CustomWindowsIntegration.BackupSettings(); Se.Settings.General.SubtitleLineMaximumLength = 37; Se.SaveSettings();
            var current = File.ReadAllText(Se.GetSettingsFilePath()); File.WriteAllText(Path.Combine(root, "bad.json"), "{}");
            Assert.Throws<InvalidDataException>(() => CustomWindowsIntegration.RestoreSettings(Path.Combine(root, "bad.json"))); Assert.Equal(current, File.ReadAllText(Se.GetSettingsFilePath()));
            CustomWindowsIntegration.RestoreSettings(first); Assert.Equal(41, Se.Settings.General.SubtitleLineMaximumLength);
            Assert.True(Directory.GetFiles(backups).Except(prior).Any(f => File.ReadAllText(f) == current));
        }
        finally
        {
            Se.Settings = original; Se.SettingsFilePathOverride = originalPath; Se.UpdateLibSeSettings();
            if (Directory.Exists(backups)) foreach (var file in Directory.GetFiles(backups).Except(prior)) File.Delete(file);
            Directory.Delete(root, true);
        }
    }

}

