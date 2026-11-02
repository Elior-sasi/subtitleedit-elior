"""Attach per-dialog project context to native Gemini translation; never persist it in settings."""
from pathlib import Path
import shutil

source = Path(__file__).resolve().parent
for name, destination in [
    ('AutoTranslateViewModel.CustomGuide.cs', 'src/ui/Features/Translate'),
    ('MainViewModel.NativeGuide.cs', 'src/ui/Features/Main'),
    ('CustomNativeTranslation.cs', 'src/ui/Features/Main/MainHelpers'),
    ('NativeGuideTests.cs', 'tests/UI/Features/Main'),
    ('GuideVerification.cs', 'src/ui/Features/Main/MainHelpers'),
    ('GuideVerificationTests.cs', 'tests/UI/Features/Main'),
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

patch('src/libuilogic/AutoTranslate/GeminiTranslate.cs',
      '        public List<TranslationPair> GetSupportedSourceLanguages()',
      r'''        // One audit POST, using the same configured model/key. No automatic retries or rewriting.
        public async Task<string> RequestCustomGuideReview(string prompt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_httpClient == null) Initialize();
            if (prompt.Length > 250000) throw new System.IO.InvalidDataException("Guide review request is too large.");
            var payload = System.Text.Json.JsonSerializer.Serialize(new { contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } }, generationConfig = new { maxOutputTokens = 32768 } });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_baseUrl, content, token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(token);
            using var json = System.Text.Json.JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                throw new System.IO.InvalidDataException(MakeNoTextMessage(body));
            var candidate = candidates[0];
            if (!candidate.TryGetProperty("finishReason", out var finish) || finish.GetString() != "STOP")
                throw new System.IO.InvalidDataException("Guide review was not completed. The candidate was not approved.");
            var texts = new List<string>();
            foreach (var part in candidate.GetProperty("content").GetProperty("parts").EnumerateArray())
                if (!(part.TryGetProperty("thought", out var thought) && thought.ValueKind == System.Text.Json.JsonValueKind.True) && part.TryGetProperty("text", out var text)) texts.Add(text.GetString() ?? "");
            token.ThrowIfCancellationRequested();
            return string.Concat(texts).Trim();
        }

        public List<TranslationPair> GetSupportedSourceLanguages()''')

patch('src/ui/Features/Translate/AutoTranslateViewModel.cs',
      '    private async Task<bool> StartTranslation(IAutoTranslator translator)\n    {',
      '    private async Task<bool> StartTranslation(IAutoTranslator translator)\n    {\n        if (!await PrepareCustomGuideTranslation(translator)) return false;')
patch('src/ui/Features/Translate/AutoTranslateViewModel.cs',
      '    private void Ok()\n    {\n        OkPressed = true;',
      '    private async Task Ok()\n    {\n        if (!await ApproveCustomGuideResults()) return;\n        OkPressed = true;')
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
