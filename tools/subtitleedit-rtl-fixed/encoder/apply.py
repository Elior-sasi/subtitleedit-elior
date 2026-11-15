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
