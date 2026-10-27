using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal static class CustomWindowsIntegration
{
    internal const string AppId = "Elior.SubtitleEdit.Hebrew";
    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows()) SetCurrentProcessExplicitAppUserModelID(AppId);
    }
    internal static void RecentFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;
        IntPtr item = IntPtr.Zero; IntPtr appId = IntPtr.Zero;
        try
        {
            var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item) != 0) return;
            // SHARDAPPIDINFO requires the IShellItem interface pointer, not IUnknown.
            // Marshaling an object as IUnknown changes the vtable and crashes shell32.
            appId = Marshal.StringToCoTaskMemUni(AppId);
            var data = new RecentInfo { Item = item, AppId = appId };
            SHAddToRecentDocs(4, ref data);
        }
        finally { if (item != IntPtr.Zero) Marshal.Release(item); if (appId != IntPtr.Zero) Marshal.FreeCoTaskMem(appId); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RecentInfo
    {
        public IntPtr Item;
        public IntPtr AppId;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IntPtr item);
    [DllImport("shell32.dll")] private static extern void SHAddToRecentDocs(uint flag, ref RecentInfo data);
    private static string KeyPath => Path.Combine(Se.DataFolder, "CustomProjects", "gemini.dpapi");
    internal static void SaveKey(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("שמירה מאובטחת זו זמינה ב־Windows.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^[A-Za-z0-9_-]{12,256}$")) throw new InvalidDataException("מפתח Gemini אינו בפורמט תקין.");
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, Protect(Encoding.UTF8.GetBytes(value.Trim()), false));
    }
    internal static string ReadKey() => File.Exists(KeyPath) && OperatingSystem.IsWindows() ? Encoding.UTF8.GetString(Protect(File.ReadAllBytes(KeyPath), true)) : "";
    // Same per-user DPAPI protection for the cloud alignment token. Never in Settings.json or packages.
    private static string CloudMfaTokenPath => Path.Combine(Se.DataFolder, "CloudMfa", "token.dpapi");
    internal static void SaveCloudMfaToken(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("שמירה מאובטחת זו זמינה ב־Windows.");
        if (!CloudMfaClient.IsValidToken(value)) throw new InvalidDataException("אסימון הגישה אינו בפורמט תקין.");
        Directory.CreateDirectory(Path.GetDirectoryName(CloudMfaTokenPath)!);
        File.WriteAllBytes(CloudMfaTokenPath, Protect(Encoding.UTF8.GetBytes(value.Trim()), false));
    }
    internal static string ReadCloudMfaToken() => File.Exists(CloudMfaTokenPath) && OperatingSystem.IsWindows() ? Encoding.UTF8.GetString(Protect(File.ReadAllBytes(CloudMfaTokenPath), true)) : "";
    internal static void DeleteCloudMfaToken() { if (File.Exists(CloudMfaTokenPath)) File.Delete(CloudMfaTokenPath); }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
    private static byte[] Protect(byte[] bytes, bool decode)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = decode ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("לא ניתן לגשת למפתח המאובטח בחשבון Windows הזה.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(bytes); }
    }
    internal static string BackupSettings()
    {
        var source = Se.GetSettingsFilePath(); if (!File.Exists(source)) Se.SaveSettings();
        var folder = Path.Combine(Se.DataFolder, "SettingsBackups"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Settings-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".json");
        File.Copy(source, path, false); return path;
    }
    internal static void RestoreSettings(string path)
    {
        var json = File.ReadAllText(path); using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("General", out _) || !doc.RootElement.TryGetProperty("Appearance", out _)) throw new InvalidDataException("זה אינו גיבוי הגדרות תקין.");
        var parsed = JsonSerializer.Deserialize(json, SeJsonContext.Default.Se) ?? throw new InvalidDataException("קובץ הגדרות לא תקין.");
        if (parsed.General == null || parsed.Appearance == null) throw new InvalidDataException("הגדרות חסרות.");
        BackupSettings();
        var target = Se.GetSettingsFilePath(); var temp = target + ".restore.tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false)); File.Move(temp, target, true); Se.LoadSettings();
    }
}

