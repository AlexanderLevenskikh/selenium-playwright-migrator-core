"""P1-O fail-closed launcher/static safety: no Windows build is executed in CI."""
import json
import ast
from pathlib import Path
import subprocess
import unittest

import p1o_builder_and_witness_contract as gate

ROOT = Path(__file__).resolve().parent.parent
LOCK = ROOT / "docs/p1o-chromium156-toolchain.lock.json"


class P1OLauncherSafety(unittest.TestCase):
    def setUp(self):
        self.lock = json.loads(LOCK.read_text("utf-8"))

    def read(self, filename):
        return (ROOT / filename).read_text(encoding="utf-8")

    def test_original_p47_git_blob_and_gate_are_untouched(self):
        name = self.lock["p47"]["path"]
        original = (ROOT / name).read_bytes()
        # GitHub Windows checkouts can normalize CRLF: exact identity is the
        # canonical Git blob, while the checked-out text may differ ONLY EOL.
        revision = subprocess.run(
            ["git", "-C", str(ROOT), "rev-parse", "HEAD:" + name],
            capture_output=True, text=True, timeout=20, check=True).stdout.strip()
        self.assertEqual(revision,
                         self.lock["p47"]["originalGitBlobSha1"])
        canonical = subprocess.run(
            ["git", "-C", str(ROOT), "cat-file", "blob", revision],
            capture_output=True, timeout=20, check=True).stdout
        self.assertEqual(original.replace(b"\r\n", b"\n"),
                         canonical.replace(b"\r\n", b"\n"))
        text = original.decode("utf-8")
        self.assertIn("os.kill(driver_pid,signal.SIGTERM)", text)
        self.assertIn("limit=time.monotonic()+12", text)
        self.assertIn("p37.check(not survivors", text)
        self.assertIn("for stage,expected,disconnect in STAGES:", text)
        self.assertEqual(text.count("def run_probe("), 1)
        for stage in self.lock["p47"]["originalStageOrder"]:
            self.assertIn('("' + stage + '",', text)

    def test_source_origin_exact_deps_and_tag_pins(self):
        gate.validate_lock(self.lock)
        self.assertEqual(
            self.lock["source"]["gitBlobs"]["DEPS"],
            "b902d611fe6a71d28add17ec663faa48e10f877d")
        self.assertEqual(
            self.lock["source"]["gitBlobs"][
                "third_party/crashpad/crashpad/util/win/exception_handler_server.cc"],
            "8af53a62b088fececd558f1436a84d4c2c4feabd")
        self.assertFalse(self.lock["state"]["chromiumInstrumentedCompiled"])
        self.assertFalse(self.lock["state"]["nativeLiveWitnessObserved"])

    def test_preflight_is_readonly_and_requires_approved_windows(self):
        pre = self.read("scripts/p1o_windows_builder_preflight.ps1")
        self.assertIn("[Text.UTF8Encoding]::new($false)", pre)
        self.assertIn("chromium.clang-cl.exe", pre)
        self.assertIn("chromium.gn.exe", pre)
        self.assertIn("sdk.windows.h", pre)
        for token in (
            "OwnerApprovedIsolatedBuilder", "scratchFreeGiB",
            "Get-CimInstance Win32_ComputerSystem", "depotToolsCommit",
            "sourceTreeClean", "Get-FileHash", "RequireReady",
            "p1o_builder_and_witness_contract.py",
            "PREREQUISITE_FAILURE",
        ):
            self.assertIn(token, pre)
        for banned in ("Set-ItemProperty", "Set-ExecutionPolicy",
                       "Set-PSRepository", "Remove-Item",
                       "git push", "workflow_dispatch", "Start-Process"):
            self.assertNotIn(banned, pre)

    def test_build_only_modifies_fresh_isolated_copy(self):
        build = self.read("scripts/p1o_build_test_owned_chromium156.ps1")
        self.assertIn("[Text.UTF8Encoding]::new($false)", build)
        self.assertIn("buildToolBinariesSha256", build)
        for token in (
            "ApproveOneInstrumentedBuild",
            "FRESH", "robocopy", "/E /COPY:DAT /DCOPY:DAT",
            "p1n_prepare_crashpad_test_witness.py",
            "MIGRATOR_P1_TEST_OWNED_WITNESS 1",
            "out\\P1O-156-instrumented",
            "DEPOT_TOOLS_UPDATE", "DEPOT_TOOLS_WIN_TOOLCHAIN",
            "'0'", "gn.bat", "autoninja.bat",
            "'chrome'", "chromeExeSha256",
            "originalP47GitBlobSha1", "productFixProven = $false",
        ):
            self.assertIn(token, build)
        executable = "\n".join(line for line in build.splitlines()
                               if not line.lstrip().startswith("#"))
        for banned in ("/MIR", "Remove-Item", "git push", "workflow_dispatch",
                       "New-Object System.Management.Automation.PSCredential",
                       "Get-Credential", "CreateJobObject(", "Stop-Process"):
            self.assertNotIn(banned, executable)
        self.assertEqual(build.count("autoninja.bat"), 2)
        # One reference validates its preexistence; the second invokes it.

    def test_real_one_shot_cannot_claim_product_remediation(self):
        launcher = self.read("scripts/p1o_win32_original_p47_one_shot.py")
        for token in (
            "--approve-original-one-shot", "P47 = ",
            "PROCESS_QUERY_LIMITED_INFORMATION", "GetProcessTimes",
            "WaitForSingleObject", "WAIT_TIMEOUT",
            "originalP47RunCount", "originalGateSeconds",
            "originalP47SourceGitBlobSha1", "handler-events.jsonl",
            "MIGRATOR_P1_TEST_WITNESS_FILE", "nativeFiletime",
            "zip_evidence", "GetTickCount64.restype",
            '"rootCauseProven": False', '"productFixProven": False',
        ):
            self.assertIn(token, launcher)
        self.assertEqual(launcher.count("subprocess.Popen("), 1)
        for banned in ("os.kill(", "TerminateProcess(", "CreateJobObject(",
                       "proc.kill(", ".terminate()", "rerun_workflow",
                       "dispatch_workflow", "source.read_text("):
            self.assertNotIn(banned, launcher)

    def test_portable_verifier_does_not_elevate_synthetic_status(self):
        portable = self.read("scripts/p1o_builder_and_witness_contract.py")
        for required in (
            '"productFixProven": False', '"rootCauseProven": False',
            '"releaseAllowed": False', '"fixClaim": False',
            '"not_observed"', '"prerequisite_failure"',
            '"partial_handler_trace_unknown"',
            '"handler_identity_not_correlated"',
        ):
            self.assertIn(required, portable)
        self.assertNotIn("OpenProcess", portable)
        syntax = ast.parse(portable)
        native_kills = [node for node in ast.walk(syntax)
                        if isinstance(node, ast.Call)
                        and isinstance(node.func, ast.Attribute)
                        and isinstance(node.func.value, ast.Name)
                        and node.func.value.id == "os"
                        and node.func.attr in ("kill", "killpg")]
        self.assertEqual(native_kills, [], "portable verifier may never kill a process")

    def test_windows_default_builder_not_run_on_public_ci(self):
        yaml = self.read(".github/workflows/core-ci.yml")
        self.assertEqual(yaml.count("  p1-preview47-real-onetime:"), 1)
        self.assertIn("matrix:\n        os: [ubuntu-latest, windows-latest]", yaml)
        self.assertNotIn("self-hosted", yaml)
        self.assertNotIn("p1o_build_test_owned_chromium156.ps1", yaml)
        self.assertNotIn("p1o_win32_original_p47_one_shot.py", yaml)


if __name__ == "__main__":
    unittest.main()
