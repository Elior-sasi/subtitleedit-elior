from pathlib import Path
from shutil import copyfile

def apply(window,vm):
    def change(p,a,b):
        s=p.read_text(encoding='utf-8-sig')
        if s.count(a)!=1: raise ValueError(f'{p}: quick clip anchor changed: {a}')
        p.write_text(s.replace(a,b),encoding='utf-8')
    copyfile(Path(__file__).with_name('QuickClip.cs'), vm.with_name('QuickClip.cs'))
    change(vm,'        VideoFileName = videoFileName;', '        ResetClipMarks();\n        VideoFileName = videoFileName;')
    change(vm,'    private async Task Generate()','    private Task Generate() => GenerateCore(false);\n\n    private async Task GenerateCore(bool quickClip)')
    change(vm,'var outputVideoFileName = await _fileHelper.PickSaveFile(Window!, fileTypes, suggestedFileName, Se.Language.General.SaveVideoAsVideoTitle);',
        'var outputVideoFileName = quickClip ? NewClipPath(VideoFileName, ext) : await _fileHelper.PickSaveFile(Window!, fileTypes, suggestedFileName, Se.Language.General.SaveVideoAsVideoTitle);')
    change(vm,'JobItems = GetCurrentVideoAsJobItems(outputVideoFileName);','JobItems = GetCurrentVideoAsJobItems(outputVideoFileName);\n            JobItems[0].QuickClipOutput = quickClip;')
    change(vm,'if (emptySubtitleCount > 0)','if (emptySubtitleCount > 0 && !quickClip)')
    change(vm,'        // Before the prompt,','''        if (jobItem.QuickClipOutput && !preview && passNumber != 1 && ffmpegParameters.StartsWith("-y ", StringComparison.Ordinal))
            ffmpegParameters = "-n" + ffmpegParameters.Substring(2);
        // Before the prompt,''')
    change(vm.with_name('BurnInJobItem.cs'),'    internal string? TimelineBurnFile','    internal bool QuickClipOutput { get; set; }\n    internal string? TimelineBurnFile')
    change(window,'        // A plain border:', '''        var controls = new StackPanel { Orientation=Orientation.Horizontal, Spacing=6 };
        Button ActionButton(string en, string he, System.Action action)
        {
            var caption=BurnInViewModel.QueueLabel(en,he);
            var b=new Button { Content=caption };
            AutomationProperties.SetName(b,caption);
            b.Click+=(_,_)=>action();
            controls.Children.Add(b);
            return b;
        }
        ActionButton("Mark In (I)", "סמן התחלה (I)", ()=>vm.MarkPreview(true));
        ActionButton("Mark Out (O)", "סמן סוף (O)", ()=>vm.MarkPreview(false));
        var export=new Button {Content=BurnInViewModel.QueueLabel("Cut and export","חתוך וייצא"),IsEnabled=vm.ClipReady};
        ToolTip.SetTip(export,BurnInViewModel.QueueLabel("Save a new clip next to the original video","שמור קטע חדש ליד הסרטון המקורי"));
        export.Click+=async (_,_)=>await vm.ExportQuickClip();
        controls.Children.Add(export);
        var hint=new TextBlock {VerticalAlignment=VerticalAlignment.Center};
        controls.Children.Add(hint);
        vm.PropertyChanged+=(_,e)=>{
            if(e.PropertyName is nameof(vm.ClipReady) or nameof(vm.ClipHint) or nameof(vm.IsBatchMode) or nameof(vm.IsGenerating)){
                export.IsEnabled=vm.ClipReady;hint.Text=vm.ClipHint;controls.IsEnabled=!vm.IsGenerating;
            }
        };
        var host=new DockPanel();
        DockPanel.SetDock(controls,Dock.Bottom);
        host.Children.Add(controls);host.Children.Add(vm.VideoPlayerControl);
        // A plain border:''')
    change(window,'            Child = vm.VideoPlayerControl,','            Child = host,')
    change(window,'        KeyDown += (_, e) => vm.OnKeyDown(e);','''        KeyDown += (_, e) => {
            if (!e.Handled && !vm.IsBatchMode && !vm.IsGenerating && e.KeyModifiers == Avalonia.Input.KeyModifiers.None &&
                e.Key is Avalonia.Input.Key.I or Avalonia.Input.Key.O &&
                !(e.Source is Avalonia.Visual v && Avalonia.VisualTree.VisualExtensions.GetSelfAndVisualAncestors(v)
                    .Any(a=>a is TextBox or ComboBox or NumericUpDown))) {
                vm.MarkPreview(e.Key == Avalonia.Input.Key.I); e.Handled=true;
            } else vm.OnKeyDown(e);
        };''')
