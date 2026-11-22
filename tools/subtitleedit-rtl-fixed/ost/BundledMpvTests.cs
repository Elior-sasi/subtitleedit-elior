using System;
using System.IO;
using Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic;

namespace UITests.Logic;

// Run alone after CI installs the official DLL beside UITests.dll.
public class BundledMpvTests
{
    [Fact]
    public void BundledMpvLoadsWhenDataAndWorkingDirectoriesHaveNoLibrary()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")));
        string oldCwd = Directory.GetCurrentDirectory();
        string oldMpvPath = LibMpvDynamicPlayer.MpvPath;
        var root = Directory.CreateTempSubdirectory("mpv-launch-").FullName;
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var launch = Directory.CreateDirectory(Path.Combine(root, "launch")).FullName;
            LibMpvDynamicPlayer.MpvPath = data;
            Directory.SetCurrentDirectory(launch);
            // These were ALL Windows candidates before the fix: none finds the bundled DLL.
            Assert.False(File.Exists(Path.Combine(data, "libmpv-2.dll")));
            Assert.False(File.Exists(Path.Combine(launch, "libmpv-2.dll")));
            Assert.False(File.Exists("libmpv-2.dll"));
            using var player = new LibMpvDynamicPlayer();
            Assert.True(player.CanLoad());
            Assert.Equal(launch, Directory.GetCurrentDirectory());
            Assert.Equal(data, LibMpvDynamicPlayer.MpvPath);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldCwd);
            LibMpvDynamicPlayer.MpvPath = oldMpvPath;
            Directory.Delete(root, true);
        }
    }
}
