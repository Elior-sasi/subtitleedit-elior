from pathlib import Path
from shutil import copyfile

def apply(window,vm):
    def replace(p,old,new):
        s=p.read_text(encoding='utf-8-sig')
        if s.count(old)!=1: raise ValueError(f'{p}: timeline anchor changed: {old}')
        p.write_text(s.replace(old,new),encoding='utf-8')
    copyfile(Path(__file__).with_name('EncoderTimeline.cs'),vm.with_name('EncoderTimeline.cs'))
    replace(window,'        rows.Children.Add(track);','''        rows.Children.Add(track);
        var timeline = new Button { Content = BurnInViewModel.QueueLabel("Waveform, burn selection and video trim…", "ציר זמן, בחירת כתוביות לצריבה וחיתוך וידאו…") };
        timeline.Bind(IsEnabledProperty, new Binding(nameof(vm.IsGenerating)) { Converter = InverseBooleanConverter.Instance });
        timeline.Click += async (_, _) => await vm.EditEncoderTimeline();
        rows.Children.Add(timeline);''')
    replace(vm,'        jobItem.AddSubtitleFileName(subtitleFileName);','''        jobItem.AddSubtitleFileName(subtitleFileName);
        if (!IsImageSubtitle && _burnRows != null && SubtitleOutputMode != 1)
        {
            var burn = BurnDraft(subtitle);
            jobItem.TimelineBurnFile = _subtitleFormat is { Name: AdvancedSubStationAlpha.NameOfFormat }
                ? _tempSubtitleFiles.Write(burn, new AdvancedSubStationAlpha()) : _tempSubtitleFiles.Write(burn, new SubRip());
        }''')
    replace(vm,'MakeAssa(jobItem, jobItem.SubtitleFileName);','MakeAssa(jobItem, jobItem.TimelineBurnFile ?? jobItem.SubtitleFileName);')
    replace(vm,'var subtitle = new Subtitle(_subtitle, false);','var subtitle = BurnDraft(_subtitle);')
    replace(vm,'|| IsCutActive || UseTargetFileSize','|| UseTargetFileSize')
    replace(vm,'Disable cut, target size, 3D and logo.', 'Disable target size, 3D and logo.')
    replace(vm,'לכבות חיתוך, גודל יעד','לכבות גודל יעד')
    replace(vm,'!preview && SubtitleOutputMode == 1\n','!preview && SubtitleOutputMode == 1 && !IsCutActive\n')
    replace(vm,'            jobItem.AssaSubtitleFileName,\n','            SubtitleOutputMode == 1 ? string.Empty : jobItem.AssaSubtitleFileName,\n')
    replace(vm,'!preview && SubtitleOutputMode == 2 ? PrepareSoftTrack(SoftTrackFor(jobItem)) : null','!preview && SubtitleOutputMode != 0 ? TimelineSoftTrack(SoftTrackFor(jobItem), jobItem.InputVideoFileName) : null')
    replace(vm,'        var emptySubtitleCount =','''        if (SubtitleOutputMode != 0 && IsCutActive && JobItems.Any(j =>
            Subtitle.Parse(SoftTrackFor(j)) is not Subtitle sub || GetSubtitleBasedOnCut(sub).Paragraphs.Count == 0))
        {
            await MessageBox.Show(Window!, Se.Language.General.Warning, QueueLabel("No soft subtitles in this cut.", "אין כתוביות ברצועה הרכה בטווח החיתוך."), MessageBoxButtons.OK);
            return;
        }
        var emptySubtitleCount =''')
    replace(vm,'        _subtitle = new Subtitle(subtitle, false);','        _burnRows = null;\n        _subtitle = new Subtitle(subtitle, false);')
    replace(vm.with_name('BurnInJobItem.cs'),'    public string AssaSubtitleFileName','    internal string? TimelineBurnFile { get; set; }\n    public string AssaSubtitleFileName')
    replace(window,'רצועה רכה בלבד — העתקת וידאו וכל רצועות השמע','רצועה רכה בלבד — ללא חיתוך: העתקה; בחיתוך: קידוד וידאו')
    replace(window,'Soft only — copy video and all audio','Soft only — copy without trimming; encode video for accurate cuts')

    # beta12 accounts for nonzero media origins when rebasing a trimmed track.
    if 'GetSubtitleBasedOnCut(Subtitle inputSubtitle, string videoFileName)' in vm.read_text(encoding='utf-8-sig'):
        replace(vm, 'GetSubtitleBasedOnCut(sub).Paragraphs', 'GetSubtitleBasedOnCut(sub, j.InputVideoFileName).Paragraphs')
        replace(vm.with_name('EncoderTimeline.cs'), 'GetSubtitleBasedOnCut(src);', 'GetSubtitleBasedOnCut(src, videoFileName ?? VideoFileName);')
