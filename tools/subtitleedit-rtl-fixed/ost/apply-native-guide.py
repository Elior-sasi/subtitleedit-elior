"""Attach per-dialog project context to native Gemini translation; never persist it in settings."""
from pathlib import Path
import shutil

source = Path(__file__).resolve().parent
for name, destination in [
    ('AutoTranslateViewModel.CustomGuide.cs', 'src/ui/Features/Translate'),
    ('MainViewModel.NativeGuide.cs', 'src/ui/Features/Main'),
    ('CustomNativeTranslation.cs', 'src/ui/Features/Main/MainHelpers'),
    ('NativeGuideTests.cs', 'tests/UI/Features/Main'),
]:
    shutil.copyfile(source / name, Path(destination) / name)

def patch(path, before, after):
    path = Path(path)
    text = path.read_text(encoding='utf-8-sig')
    if text.count(before) != 1:
        raise RuntimeError(f'Native guide anchor changed in {path}: {before[:100]!r}')
    path.write_text(text.replace(before, after), encoding='utf-8-sig')

patch('src/libuilogic/AutoTranslate/GeminiTranslate.cs',
      '        public int MaxCharacters => 1500;',
      '        public int MaxCharacters => 1500;\n        public System.Func<string, string, string>? CustomGuidePrompt { get; set; }')
patch('src/libuilogic/AutoTranslate/GeminiTranslate.cs',
      '            var input = "{ \\"contents\\": [ { \\"role\\": \\"user\\", \\"parts\\": [{ \\"text\\": \\"" + Json.EncodeJsonText(prompt) + "\\\\n\\\\n" + Json.EncodeJsonText(text.Trim()) + "\\" }]}]}";'.replace('\\\\"', '\\"'),
      '            var requestText = CustomGuidePrompt == null ? prompt + "\\n\\n" + text.Trim() : CustomGuidePrompt(prompt, text.Trim());\n'
      '            var input = "{ \\"contents\\": [ { \\"role\\": \\"user\\", \\"parts\\": [{ \\"text\\": \\"" + Json.EncodeJsonText(requestText) + "\\" }]}]}";'.replace('\\\\"', '\\"'))
patch('src/ui/Features/Translate/AutoTranslateViewModel.cs',
      '    private async Task<bool> StartTranslation(IAutoTranslator translator)\n    {',
      '    private async Task<bool> StartTranslation(IAutoTranslator translator)\n    {\n        if (!await PrepareCustomGuideTranslation(translator)) return false;')
patch('src/ui/Features/Translate/AutoTranslateWindow.cs',
      '        var engineSettings = BuildEngineSettingsStrip(vm);',
      '        var engineSettings = new StackPanel { Children = { BuildEngineSettingsStrip(vm), vm.MakeCustomGuideStrip() } };')

path = Path('src/ui/Features/Main/MainViewModel.cs')
text = path.read_text(encoding='utf-8-sig')
for signature in ('    private async Task ShowAutoTranslate()', '    private async Task AutoTranslateSelectedLines(bool autoStart)'):
    start = text.index(signature)
    end = text.find('    [RelayCommand]', start + len(signature))
    if end == -1:
        raise RuntimeError('Cannot locate next command after native translation')
    method = text[start:end]
    anchor = '        var result = await ShowDialogAsync<AutoTranslateWindow, AutoTranslateViewModel>'
    if method.count(anchor) != 1:
        raise RuntimeError('Native translate dialog anchor changed')
    method = method.replace(anchor, '        var guideSession = CaptureNativeGuideSession();\n' + anchor)
    if signature.endswith('ShowAutoTranslate()'):
        needle = 'vm => { vm.Initialize(GetUpdateSubtitle()); }'
        replacement = 'vm => { vm.Initialize(GetUpdateSubtitle()); vm.ConfigureCustomGuide(guideSession.Profile); }'
    else:
        needle = '            vm.Initialize(sub);'
        replacement = needle + '\n            vm.ConfigureCustomGuide(guideSession.Profile);'
        if method.count('            if (autoStart)') != 1:
            raise RuntimeError('Native translate auto-start anchor changed')
        method = method.replace('            if (autoStart)', '            if (autoStart && !vm.CustomGuideAvailable)')
    if method.count(needle) != 1:
        raise RuntimeError('Native translate initialization anchor changed')
    method = method.replace(needle, replacement)
    needle = '        if (!result.OkPressed)'
    if method.count(needle) != 1:
        raise RuntimeError('Native translate apply anchor changed')
    method = method.replace(needle, '''        if (result.OkPressed && result.HasUsableTranslation && !IsNativeGuideSessionCurrent(guideSession))
        {
            await ShowCustomActionNotice("תרגום אוטומטי", "המסמך או פרטי הפרויקט השתנו בזמן התרגום. התוצאה לא הוחלה; פתח את התרגום מחדש.");
            return;
        }

''' + needle)
    text = text[:start] + method + text[end:]
path.write_text(text, encoding='utf-8-sig')
