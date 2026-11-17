import json, pathlib, sys
patch = json.loads(pathlib.Path(__file__).with_name("audio-copy.patch.json").read_text(encoding="utf-8"))
p = pathlib.Path(sys.argv[1]) / patch["path"]
s = p.read_text(encoding="utf-8-sig")
if s.count(patch["old"]) != 1:
    raise SystemExit("Encoder audio patch anchor changed; refusing to modify source")
p.write_text(s.replace(patch["old"], patch["new"]), encoding="utf-8")

import shutil
vm = p.with_name("BurnInViewModel.cs")
src = vm.read_text(encoding="utf-8-sig")
anchor = "var available = FfmpegHelper.GetAvailableEncoders();"
if src.count(anchor) != 1:
    raise SystemExit("Encoder availability hook changed")
vm.write_text(src.replace(anchor, "var available = HardwareEncoderChecks.Check(FfmpegHelper.GetFfmpegLocation(), FfmpegHelper.GetAvailableEncoders());"), encoding="utf-8")
shutil.copyfile(pathlib.Path(__file__).with_name("HardwareEncoderChecks.cs"), p.with_name("HardwareEncoderChecks.cs"))
shutil.copyfile(pathlib.Path(__file__).with_name("BurnInQueue.cs"), p.with_name("BurnInQueue.cs"))
ui = p.read_text(encoding="utf-8")
anchor = '            Action("fa-solid fa-plus", Se.Language.General.AddDotDotDot, vm.AddCommand),'
if ui.count(anchor) != 1:
    raise SystemExit("Queue toolbar changed")
extra = [('arrow-up','Move up','הזז למעלה','QueueUp'),('arrow-down','Move down','הזז למטה','QueueDown'),('floppy-disk','Save queue','שמור תור','SaveQueue'),('folder-open','Open queue','פתח תור','OpenQueue')]
rows = [f'            Action("fa-solid fa-{icon}", BurnInViewModel.QueueLabel("{en}", "{he}"), vm.{command}Command),' for icon,en,he,command in extra]
p.write_text(ui.replace(anchor, '\n'.join([anchor]+rows)), encoding="utf-8")
ui = p.read_text(encoding="utf-8")
anchor = "        rows.Children.Add(Row(Se.Language.General.Resolution, resolution));"
if ui.count(anchor) != 1: raise SystemExit("Profile toolbar changed")
profiles = """        var profile = new ComboBox { ItemsSource = new[] {
            BurnInViewModel.QueueLabel("Maximum quality (slow, CRF 18)", "איכות מרבית (slow, CRF 18)"),
            BurnInViewModel.QueueLabel("High quality (slow, CRF 20)", "איכות גבוהה (slow, CRF 20)"),
            BurnInViewModel.QueueLabel("Balanced (medium, CRF 22)", "מאוזן (medium, CRF 22)"),
            BurnInViewModel.QueueLabel("Fast (NVENC if available)", "מהיר (NVENC אם זמין)") } };
        var label = BurnInViewModel.QueueLabel("Apply encoding profile", "החל פרופיל קידוד");
        AutomationProperties.SetName(profile, label);
        ToolTip.SetTip(profile, label);
        profile.Bind(IsEnabledProperty, new Binding(nameof(vm.IsGenerating)) { Converter = InverseBooleanConverter.Instance });
        profile.SelectionChanged += async (_, _) => {
            var index = profile.SelectedIndex;
            if (index < 0) return;
            profile.SelectedIndex = -1;
            await vm.ApplyEncoderProfile(index);
        };
        rows.Children.Add(Row(label, profile));
"""
p.write_text(ui.replace(anchor, profiles+anchor), encoding="utf-8")
from mux import apply
apply(p, vm, pathlib.Path(sys.argv[1]))
testdir = pathlib.Path(sys.argv[1]) / 'tests/UI/Features/Video'
if testdir.is_dir():
    shutil.copyfile(pathlib.Path(__file__).with_name('EncoderMuxTests.cs'), testdir / 'EncoderMuxTests.cs')
from queue_progress import apply as apply_progress
apply_progress(p, vm)
from timeline import apply as apply_timeline
apply_timeline(p, vm)
from quick_clip import apply as apply_quick_clip
apply_quick_clip(p, vm)
