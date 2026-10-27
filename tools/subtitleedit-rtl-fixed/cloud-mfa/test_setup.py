import pathlib
import os
import tempfile
import subprocess
import unittest
from unittest.mock import Mock
import probe

HERE = pathlib.Path(__file__).resolve().parent


class SetupTests(unittest.TestCase):
    def test_plan_does_not_invoke_gcloud_or_mutate_files(self):
        result = subprocess.run(["bash", str(HERE / "setup.sh"), "example-project"],
                                env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("NOT", result.stdout.upper())
        self.assertIn("timeout: 10 minutes", result.stdout)

    def test_rejects_injection_and_unknown_mode(self):
        for project, mode in [("x; echo secret", "--plan"), ("example-project", "--execute")]:
            result = subprocess.run(["bash", str(HERE / "setup.sh"), project, mode], capture_output=True)
            self.assertEqual(2, result.returncode)

    def test_cancel_before_cloud_commands(self):
        result = subprocess.run(["bash", str(HERE / "setup.sh"), "example-project", "--deploy-probe"],
                                input="no\n", capture_output=True, text=True)
        self.assertEqual(3, result.returncode)
        self.assertIn("Cancelled", result.stdout)

    def test_help_probe_is_explicitly_not_alignment(self):
        run = Mock(return_value=Mock(stdout="Usage: mfa command [OPTIONS]\n--help"))
        result = probe.check(run, lambda _: "3.4.3")
        self.assertTrue(result["cli_ok"])
        self.assertFalse(result["alignment_tested"])
        self.assertFalse(result["desktop_connected"])
        self.assertEqual(3, run.call_count)
        self.assertTrue(all(c.kwargs["timeout"] == 90 for c in run.call_args_list))

    def test_failed_or_changed_engine_not_ready(self):
        with self.assertRaises(RuntimeError):
            probe.check(Mock(), lambda _: "4.0.0")
        with self.assertRaises(RuntimeError):
            probe.check(Mock(return_value=Mock(stdout="broken")), lambda _: "3.4.3")
        with self.assertRaises(subprocess.CalledProcessError):
            probe.check(Mock(side_effect=subprocess.CalledProcessError(1, "mfa")), lambda _: "3.4.3")

    def test_deployment_plan_uses_digest_and_never_runs_job_or_grants_roles(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            fake = root / "gcloud"
            fake.write_text('#!/bin/bash\nprintf "%s\\n" "$*" >> "$CALL_LOG"\n'
                            'if [[ "$1 $2" == "billing projects" ]]; then echo True; fi\n'
                            'if [[ "$1 $2 $3 $4" == "artifacts docker images describe" ]]; then '
                            'printf "sha256:%064d\\n" 0; fi\n')
            fake.chmod(0o700)
            env = dict(os.environ, PATH=tmp + ":" + os.environ["PATH"], CALL_LOG=str(root / "calls"))
            result = subprocess.run(["bash", str(HERE / "setup.sh"), "example-project", "--deploy-probe"],
                                    input="example-project\n", capture_output=True, text=True, env=env)
            self.assertEqual(0, result.returncode, result.stderr)
            calls = (root / "calls").read_text()
            self.assertIn("@sha256:" + "0" * 64, calls)
            self.assertIn("--max-retries=0 --task-timeout=10m", calls)
            self.assertNotIn("jobs execute", calls)
            self.assertNotIn("add-iam-policy-binding", calls)
            self.assertNotIn("allUsers", calls)
            self.assertNotIn("keys create", calls)


if __name__ == "__main__":
    unittest.main()

