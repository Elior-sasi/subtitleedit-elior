from pathlib import Path
import shutil

def apply(window, vm, root):
    def edit(path, old, new):
        src = path.read_text(encoding='utf-8-sig')
        if src.count(old) != 1: raise ValueError(f'{path}: mux anchor changed: {old}')
        path.write_text(src.replace(old, new), encoding='utf-8')
    shutil.copyfile(Path(__file__).with_name('SubtitleMux.cs'), window.with_name('SubtitleMux.cs'))
    edit(window,'        rows.Children.Add(Row(label, profile));','''        rows.Children.Add(Row(label, profile));
        var muxLabel = BurnInViewModel.QueueLabel("Subtitle output", "מצב כתוביות בפלט");
        var mode = new ComboBox { ItemsSource = new[] {
            BurnInViewModel.QueueLabel("Burn into video", "צריבה לתמונה"),
            BurnInViewModel.QueueLabel("Soft only — copy video and all audio", "רצועה רכה בלבד — העתקת וידאו וכל רצועות השמע"),
            BurnInViewModel.QueueLabel("Burn and add switchable track", "צריבה והוספת רצועה רכה") }, SelectedIndex = 0 };
        mode.Bind(IsEnabledProperty, new Binding(nameof(vm.IsGenerating)) { Converter = InverseBooleanConverter.Instance });
        AutomationProperties.SetName(mode, muxLabel);
        ToolTip.SetTip(mode, BurnInViewModel.QueueLabel("MP4 uses timed text; ASS styling may be lost. Some players enable the MP4 track automatically.", "MP4 שומר טקסט מתוזמן ועלול לאבד עיצוב ASS. נגנים מסוימים מפעילים את רצועת ה־MP4 אוטומטית."));
        mode.SelectionChanged += (_, _) => vm.SubtitleOutputMode = mode.SelectedIndex;
        rows.Children.Add(Row(muxLabel, mode));
        var alternate = new Button { Content = BurnInViewModel.QueueLabel("Choose a different soft track…", "בחר קובץ אחר לרצועה הרכה…"), IsVisible = false };
        var track = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        mode.SelectionChanged += (_, _) => {
            alternate.IsVisible = mode.SelectedIndex == 2 && !vm.IsBatchMode;
            track.IsVisible = alternate.IsVisible;
        };
        alternate.Bind(IsEnabledProperty, new Binding(nameof(vm.IsGenerating)) { Converter = InverseBooleanConverter.Instance });
        alternate.Click += async (_, _) => { await vm.PickSoftTrack(); track.Text = vm.AlternateSoftTrack; };
        rows.Children.Add(alternate);
        rows.Children.Add(track);''')
    edit(vm,'        return _tempSubtitleFiles.Write(subtitle, new AdvancedSubStationAlpha());', r"""        // libass otherwise uses an LTR paragraph base for ordinary ASS styles.
        // Only this parsed, render-only copy goes to the burn filter.
        foreach (var cue in subtitle.Paragraphs)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(cue.Text, @"[א-תיִ-ﭏ]") &&
                !cue.Text.Contains(@"\fe", StringComparison.OrdinalIgnoreCase))
                cue.Text = @"{\fe-1}" + cue.Text;
        }
        return _tempSubtitleFiles.Write(subtitle, new AdvancedSubStationAlpha());""")
    edit(vm,'    private string _imageSubtitleFileName', '    internal int SubtitleOutputMode { get; set; }\n    private string _imageSubtitleFileName')
    edit(vm,'        var emptySubtitleCount =','''        if (SubtitleOutputMode != 0)
        {
            foreach (var job in JobItems)
            {
                var container = Path.GetExtension(IsBatchMode ? MakeOutputFileName(job.InputVideoFileName) : job.OutputVideoFileName).ToLowerInvariant();
                var sub = Path.GetExtension(SoftTrackFor(job)).ToLowerInvariant();
                if (container is not (".mp4" or ".mkv") || sub is not (".srt" or ".ass" or ".ssa") || IsCutActive || UseTargetFileSize ||
                    !string.IsNullOrEmpty(BurnInLogo.LogoFileName) || SelectedMode3D?.Mode is not (null or Export3DMode.None) ||
                    (!File.Exists(SoftTrackFor(job)) || !IsBatchMode && File.Exists(job.OutputVideoFileName)) || HasNoSubtitleLines(job.SubtitleFileName))
                {
                    await MessageBox.Show(Window!, Se.Language.General.Warning, QueueLabel(
                        "For a soft track, choose a new MP4 or MKV file and nonempty SRT/ASS/SSA subtitles. Disable cut, target size, 3D and logo.",
                        "לרצועה רכה יש לבחור קובץ MP4 או MKV חדש וכתוביות SRT/ASS/SSA שאינן ריקות. יש לכבות חיתוך, גודל יעד, תלת־ממד ולוגו."), MessageBoxButtons.OK);
                    return;
                }
            }
        }
        var emptySubtitleCount =''')
    edit(vm,'jobItem.AssaSubtitleFileName = MakeAssa(jobItem, jobItem.SubtitleFileName);','jobItem.AssaSubtitleFileName = SubtitleOutputMode == 1 ? jobItem.SubtitleFileName : MakeAssa(jobItem, jobItem.SubtitleFileName);')
    edit(vm,'var ffmpegParameters = FfmpegGenerator.GenerateHardcodedVideoFile(','''var ffmpegParameters = !preview && SubtitleOutputMode == 1
            ? SubtitleMux.SoftOnly(jobItem.InputVideoFileName, jobItem.SubtitleFileName, jobItem.OutputVideoFileName)
            : FfmpegGenerator.GenerateHardcodedVideoFile(''')
    edit(vm,'Stereo3DImage.MaxDepth));\n\n        // Before the prompt','Stereo3DImage.MaxDepth),\n            !preview && SubtitleOutputMode == 2 ? SoftTrackFor(jobItem) : null);\n\n        // Before the prompt')
    gen = root/'src/ui/Logic/Media/FfmpegGenerator.cs'
    edit(gen,'Export3DMode mode3D = Export3DMode.None, int depth3D = 0)\n    {\n        if (width', 'Export3DMode mode3D = Export3DMode.None, int depth3D = 0, string? softSubtitle = null)\n    {\n        if (width')
    edit(gen, '        var passSettings = string.Empty;', """        var softOptions = softSubtitle == null ? string.Empty : Features.Video.BurnIn.SubtitleMux.SubtitleOptions(outputVideoFileName.Trim('"'));
        var passSettings = string.Empty;""")
    edit(gen,'        return\n            $"-y{cutStart}', '''        var switchable = softSubtitle == null ? string.Empty :
            $" -i {Features.Video.BurnIn.SubtitleMux.PathArg(softSubtitle)} -map {(inputIsAudioOnly ? "1:v:0" : "0:V:0")} -map 0:a? -map {inputCount}:s:0 -map_metadata 0 -map_chapters 0 {softOptions}";
        return
            $"{(softSubtitle == null ? "-y" : "-n")}{cutStart}''')
    edit(gen,'{imageSubtitleInput}{logoInput}{cutEnd}', '{imageSubtitleInput}{logoInput}{switchable}{cutEnd}')
