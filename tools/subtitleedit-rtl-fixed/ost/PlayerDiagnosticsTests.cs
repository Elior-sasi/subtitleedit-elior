using Nikse.SubtitleEdit.Logic.VideoPlayers;

namespace UITests.Logic;

/// <summary>
/// The player diagnostics log must never carry personal paths or subtitle text, must stay
/// bounded, and must never throw into the player.
/// </summary>
[Collection("PlayerDiagnostics")]
public class PlayerDiagnosticsTests
{
    private const string Episode = @"C:\Users\Someone\Downloads\Show.S01E04.Secret.Title.mkv";

    [Fact]
    public void MediaTag_KeepsExtensionAndHashOnly()
    {
        var tag = PlayerDiagnostics.MediaTag(Episode);
        Assert.StartsWith(".mkv#", tag);
        Assert.Equal(13, tag.Length);
        Assert.DoesNotContain("Someone", tag);
        Assert.DoesNotContain("Secret", tag);
        Assert.Equal(tag, PlayerDiagnostics.MediaTag(Episode));
        Assert.NotEqual(tag, PlayerDiagnostics.MediaTag(Episode.Replace("S01E04", "S01E03")));
        Assert.Equal("none", PlayerDiagnostics.MediaTag(null));
    }

    [Fact]
    public void Redact_MasksPathsInMpvMessages()
    {
        var text = PlayerDiagnostics.Redact("Failed to open " + Episode + ".\r\nnext line");
        Assert.DoesNotContain("Someone", text);
        Assert.DoesNotContain("Secret", text);
        Assert.DoesNotContain("\n", text);
        Assert.Contains("Failed to open <.mkv#", text);
    }

    [Fact]
    public void Log_WritesLinesAndRotatesAtLimit()
    {
        var folder = Path.Combine(Path.GetTempPath(), "se-player-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previous = PlayerDiagnostics.Folder;
        try
        {
            PlayerDiagnostics.Folder = folder;
            var log = Path.Combine(folder, PlayerDiagnostics.FileName);
            File.WriteAllText(log, new string('x', (int)PlayerDiagnostics.MaxBytes + 10));
            PlayerDiagnostics.Log("load", "#1 begin media=" + PlayerDiagnostics.MediaTag(Episode));
            Assert.True(File.Exists(log + ".1"));
            var line = File.ReadAllText(log);
            Assert.Contains("load", line);
            Assert.Contains("#1 begin media=.mkv#", line);
            Assert.True(new FileInfo(log).Length < 1000);
        }
        finally
        {
            PlayerDiagnostics.Folder = previous;
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Log_NeverThrows_WhenFolderIsUnusable()
    {
        var previous = PlayerDiagnostics.Folder;
        try
        {
            PlayerDiagnostics.Folder = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"), "deeper");
            PlayerDiagnostics.Log("load", "ignored");
            PlayerDiagnostics.Log("load", "ignored again");
        }
        finally
        {
            PlayerDiagnostics.Folder = previous;
        }
    }

    [Theory]
    [InlineData(0, "eof")]
    [InlineData(2, "stop")]
    [InlineData(4, "error")]
    [InlineData(9, "reason9")]
    public void EndFileReason_Names(int reason, string expected) =>
        Assert.Equal(expected, PlayerDiagnostics.EndFileReason(reason));
}

