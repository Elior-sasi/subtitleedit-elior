using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.Features.Help.CheckForUpdates;
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    [Fact]
    public void PrivateReleaseUpdateRequiresExactSuccessfulRunCommitAttemptAndDigest()
    {
        var sha = new string('a', 40);
        var json = JsonSerializer.Serialize(new { draft = false, prerelease = true, tag_name = "custom-build-123-2", target_commitish = sha,
            assets = new[] { new { id = 456L, name = "SE-5.4.0-beta6-RTL-Hebrew-OST-AutoUpdate.zip", digest = "sha256:" + new string('b', 64) } } });
        var package = CustomUpdateChannel.ParseReleasePackage(json, 123, sha, 2);
        Assert.NotNull(package); Assert.True(package.ReleaseAsset); Assert.Equal(456, package.ArtifactId); Assert.Equal(sha, package.Commit);
        Assert.Null(CustomUpdateChannel.ParseReleasePackage(json, 123, sha, 1));
        Assert.Null(CustomUpdateChannel.ParseReleasePackage(json, 124, sha, 2));
        Assert.Null(CustomUpdateChannel.ParseReleasePackage(json, 123, new string('c', 40), 2));
        Assert.Null(CustomUpdateChannel.ParseReleasePackage(json.Replace("\"draft\":false", "\"draft\":true"), 123, sha, 2));
        Assert.Null(CustomUpdateChannel.ParseReleasePackage(json.Replace("\"prerelease\":true", "\"prerelease\":false"), 123, sha, 2));
        Assert.Null(CustomUpdateChannel.ParseReleasePackage(json.Replace("sha256:", "md5:"), 123, sha, 2));
        Assert.Null(CustomUpdateChannel.ParseReleasePackage(json.Replace("RTL-Hebrew-OST", "Official"), 123, sha, 2));
    }

    [Fact]
    public async Task PrivateReleaseAndOriginalArtifactLayoutsBothExtractOnlyAfterDigestVerification()
    {
        var root = Path.Combine(Path.GetTempPath(), "se-release-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "custom-updater"));
            File.WriteAllText(Path.Combine(source, "SubtitleEdit.exe"), "synthetic fixture, not a program");
            File.WriteAllText(Path.Combine(source, "custom-updater", "gh.exe"), "synthetic fixture");
            File.WriteAllText(Path.Combine(source, "custom-build.json"), JsonSerializer.Serialize(new { run_id = 123, channel = "Elior-sasi/subtitleedit-elior/main", commit = new string('a', 40) }));
            var inner = Path.Combine(root, "custom.zip"); ZipFile.CreateFromDirectory(source, inner);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inner)));
            await CustomUpdateChannel.VerifyArchiveAsync(inner, hash, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() => CustomUpdateChannel.VerifyArchiveAsync(inner, new string('0', 64), CancellationToken.None));
            var payload = Path.Combine(root, "direct"); CustomUpdateChannel.ExtractPackage(inner, payload, true);
            CustomUpdateChannel.ValidatePayload(payload, 123);
            var wrap = Path.Combine(root, "wrap"); Directory.CreateDirectory(wrap); File.Copy(inner, Path.Combine(wrap, "custom.zip"));
            var outer = Path.Combine(root, "artifact.zip"); ZipFile.CreateFromDirectory(wrap, outer);
            var second = Path.Combine(root, "artifact", "payload"); Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            CustomUpdateChannel.ExtractPackage(outer, second, false); CustomUpdateChannel.ValidatePayload(second, 123);
            Assert.Equal(File.ReadAllText(Path.Combine(payload, "custom-build.json")), File.ReadAllText(Path.Combine(second, "custom-build.json")));
            Assert.Throws<InvalidDataException>(() => CustomUpdateChannel.ValidatePayload(payload, 124));
        }
        finally { Directory.Delete(root, true); }
    }
}

