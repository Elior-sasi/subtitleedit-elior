"""CI only: runs the real HTTP service with the fake engine double so the Windows client
test exercises the real protocol. Usage: python ci_fake_server.py PORT TOKEN"""
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from mfa_service import api  # noqa: E402
from mfa_service.config import Config  # noqa: E402

port, token = int(sys.argv[1]), sys.argv[2]
tmp = Path(tempfile.mkdtemp())
(tmp / "tokens.json").write_text(json.dumps({hashlib.sha256(token.encode()).hexdigest(): "ci"}))
(tmp / "words.dict").write_text("hello\th a\n")
cfg = Config(storage_root=str(tmp / "data"), work_root=str(tmp / "work"), tokens_file=str(tmp / "tokens.json"))
cfg.models = [{"id": "fake", "label": "fake engine (CI)", "available": True, "engine_version": "ci", "acoustic": str(tmp / "words.dict"),
               "dictionary": str(tmp / "words.dict"), "acoustic_label": "fake", "dictionary_label": "fake", "dictionary_words": None,
               "mfa_bin": [sys.executable, str(HERE / "fake_mfa.py")]},
              {"id": "hebrew", "label": "עברית", "available": False, "reason": "No official MFA Hebrew model."}]
os.environ["PORT"], os.environ["MFA_HOST"] = str(port), "127.0.0.1"
app, server = api.build(cfg, engine_version="ci")
app.service.start()
print("ready", flush=True)
server.serve_forever()

