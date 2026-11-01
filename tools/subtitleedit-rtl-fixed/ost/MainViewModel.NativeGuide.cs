using System.Text.Json;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    internal sealed record NativeGuideSession(string Document, int Generation, int Hash, string ProfileFingerprint, ProjectProfile? Profile);
    internal NativeGuideSession CaptureNativeGuideSession()
    {
        if (!string.IsNullOrWhiteSpace(_subtitleFileName)) EnsureCustomProject();
        var profile = string.IsNullOrWhiteSpace(_subtitleFileName) ? null : ActiveProfile;
        var fingerprint = profile == null ? "" : JsonSerializer.Serialize(profile);
        return new NativeGuideSession(_subtitleFileName ?? "", _ostDocumentGeneration, GetFastHash(), fingerprint,
            profile == null ? null : JsonSerializer.Deserialize<ProjectProfile>(fingerprint));
    }
    private void RequireTranslationGuide(ProjectProfile expected)
    {
        RequireEpisodeContext(expected);
        if (ActiveProfile == null || JsonSerializer.Serialize(ActiveProfile) != JsonSerializer.Serialize(expected))
            throw new System.InvalidOperationException("הנחיות הפרויקט, המדריך או המילון השתנו. פתח את הפעולה מחדש; תוצאה ישנה לא תוחל.");
    }
    internal bool IsNativeGuideSessionCurrent(NativeGuideSession session) =>
        session.Document == (_subtitleFileName ?? "") && session.Generation == _ostDocumentGeneration && session.Hash == GetFastHash() &&
        session.ProfileFingerprint == (string.IsNullOrWhiteSpace(_subtitleFileName) || ActiveProfile == null ? "" : JsonSerializer.Serialize(ActiveProfile));
}
