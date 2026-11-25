"""Pair recoverable subtitle backups with the live OST snapshot."""
from pathlib import Path

path = Path('src/ui/Logic/AutoBackupService.cs')
text = path.read_text(encoding='utf-8-sig')

def replace(before, after):
    global text
    if text.count(before) != 1:
        raise RuntimeError(f'OST recovery backup anchor changed: {before[:100]!r}')
    text = text.replace(before, after)

replace('    private int _backupInFlight;', '''    private int _backupInFlight;
    private string? _lastBackedUpOst;''')
replace('''            if (_mainViewModel is not { } vm || !vm.HasChanges())
            {
                return;
            }''', '''            if (_mainViewModel is not { } vm || Volatile.Read(ref _backupInFlight) != 0)
            {
                return;
            }

            var subtitleChanged = vm.HasChanges();
            var ost = vm.CaptureOstRecoverySnapshot();
            if (!subtitleChanged && string.Equals(ost, _lastBackedUpOst, StringComparison.Ordinal))
            {
                return;
            }''')
replace('                    SaveAutoBackup(subtitle, saveFormat);', '''                    if (SaveAutoBackup(subtitle, saveFormat, ost))
                    {
                        _lastBackedUpOst = ost;
                    }''')
start = text.index('    private static void SaveAutoBackup(')
end = text.index('    public List<string> GetAutoBackupFiles()', start)
save = text[start:end]
text = text[:start] + save.replace('private static void SaveAutoBackup(Subtitle subtitle, SubtitleFormat saveFormat)', 'private static bool SaveAutoBackup(Subtitle subtitle, SubtitleFormat saveFormat, string? ost)').replace('return;', 'return false;') + text[end:]
replace('''        try
        {
            File.WriteAllText(fileName, text);
        }
        catch
        {
            // ignore - a backup that cannot be written must not disturb editing
        }
    }''', '''        // A unique recovery pair never replaces a different backup from another window.
        if (ost != null || File.Exists(fileName) || File.Exists(fileName + ".ost-recovery"))
            fileName = Path.Combine(folder, Path.GetFileNameWithoutExtension(fileName) + "_" +
                Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
        return WriteSubtitleRecoveryPair(fileName, text, ost);
    }

    internal static bool WriteSubtitleRecoveryPair(string fileName, string subtitle, string? ost)
    {
        var directory = Path.GetDirectoryName(fileName)!;
        var staging = Path.Combine(directory, ".recovery-" + Guid.NewGuid().ToString("N"));
        var ostPath = fileName + ".ost-recovery";
        var wroteOst = false;
        try
        {
            File.WriteAllText(staging, subtitle);
            if (ost != null)
            {
                File.WriteAllText(staging + ".ost", ost);
                File.Move(staging + ".ost", ostPath);
                wroteOst = true;
            }

            // The subtitle becomes visible to Restore Auto Backup only once OST is ready.
            File.Move(staging, fileName);
            return true;
        }
        catch (Exception ex)
        {
            if (wroteOst) DeleteRecoveryFile(ostPath);
            Se.LogError(ex, "Could not write subtitle recovery backup");
            return false;
        }
        finally
        {
            DeleteRecoveryFile(staging);
            DeleteRecoveryFile(staging + ".ost");
        }
    }

    private static void DeleteRecoveryFile(string path)
    {
        try { File.Delete(path); }
        catch { /* A failed backup must not disturb editing. */ }
    }''')
replace('''                if (RegexFileNamePattern().IsMatch(path))
                {
                    result.Add(fileName);
                }''', '''                if (RegexFileNamePattern().IsMatch(path) &&
                    !path.EndsWith(".ost-recovery", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".ostproject", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(fileName);
                }''')
path.write_text(text, encoding='utf-8-sig')
