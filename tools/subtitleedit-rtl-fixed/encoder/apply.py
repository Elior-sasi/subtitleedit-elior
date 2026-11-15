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
