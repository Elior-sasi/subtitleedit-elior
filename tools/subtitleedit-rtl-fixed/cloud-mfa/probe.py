"""No audio, transcript, model download, listener, credentials or user settings."""
import importlib.metadata
import json
import subprocess


def check(run=subprocess.run, version=importlib.metadata.version):
    installed = version("montreal-forced-aligner")
    if installed != "3.4.3":
        raise RuntimeError("Unexpected MFA version")
    for command in ("align", "segment", "model"):
        result = run(["mfa", command, "--help"], capture_output=True,
                     text=True, timeout=90, check=True)
        if "Usage:" not in result.stdout or "--help" not in result.stdout:
            raise RuntimeError("MFA CLI smoke failed: " + command)
    return {"schema": 1, "engine": "MFA", "version": installed,
            "cli_ok": True, "alignment_tested": False,
            "desktop_connected": False}


if __name__ == "__main__":
    print(json.dumps(check(), sort_keys=True))

