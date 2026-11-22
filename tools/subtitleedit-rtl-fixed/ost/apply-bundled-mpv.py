from pathlib import Path
import shutil

p = Path('src/ui/Logic/VideoPlayers/LibMpvDynamic/LibMpvDynamicPlayer.cs')
s = p.read_text(encoding='utf-8-sig')
old = '''                MpvPath,
                Directory.GetCurrentDirectory(),
                string.Empty,'''
new = '''                MpvPath,
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory(),
                string.Empty,'''
if s.count(old) != 1:
    raise ValueError('Windows mpv search path anchor changed')
s = s.replace(old, new)
old = '                    var libHandle = NativeMethods.CrossLoadLibrary(fullPath);'
new = '''                    var libHandle = NativeMethods.CrossLoadLibrary(fullPath);
                    PlayerDiagnostics.Log("library", $"mpv candidate={(string.Equals(libPath, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) ? "bundled" : "other")} loaded={libHandle != IntPtr.Zero}");'''
if s.count(old) != 1:
    raise ValueError('mpv library load anchor changed')
p.write_text(s.replace(old, new), encoding='utf-8')
shutil.copyfile(Path(__file__).with_name('BundledMpvTests.cs'), 'tests/UI/Logic/BundledMpvTests.cs')
print('Bundled mpv lookup and native regression applied')
