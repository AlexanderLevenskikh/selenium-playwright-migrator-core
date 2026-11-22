#!/usr/bin/env python3
"""Read-only checks on three immutable *external public* Selenium/NUnit source slices.

This is NOT a conversion or execution of the external repos. Every source is
bound to an exact Git commit and raw Git blob; no source content is exported.
"""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

SCHEMA = "migrator-p2p3-public-source-portfolio/v1"
SAMPLES = {
    "desh": {
        "repository": "desh2001/Selenium-NUnit-Automation",
        "commit": "edacb9feb41a7e21d9b6bf6924f24c11698a4427",
        "files": {
            "AssignmentProject/Tests/TextInputTests.cs":
                "92592a1746b876956ab98506c7dcb8818f479b9a",
            "AssignmentProject/Pages/TextInputPage.cs":
                "2aec530bd9dbbacf051a4c19b7cda3fda1ec0a00",
        },
        "expected": "POM multi-hop, NUnit setup/teardown, args, selectors",
    },
    "selenio": {
        "repository": "jorgedelacruz/selenio",
        "commit": "0ca128dfaf32c00f944b756fea9dbf9b6c8964a2",
        "files": {
            "Selenio/Selenio.NUnit/NUnitTests.cs":
                "579aca5785d680ed9e8f8b739c7d145eef9fda47",
            "Selenio/Selenio.NUnit/PageObjects/SeleniumHomePage.cs":
                "960ecede143e403d1d319784593de36a756207e0",
        },
        "expected": "Inherited NUnit TestClass and PageFactory proxy dispatch",
    },
    "remotion": {
        "repository": "re-motion/Framework",
        "commit": "27716889ad2d26f4fa4b442207460c570bd253db",
        "files": {
            "Remotion/Web/IntegrationTests/SetUpFixture.cs":
                "b27d5a4b0e91166c479737489f8bc8ad581c180b",
        },
        "expected": "Assembly/namespace scoped SetUpFixture and external side effects",
    },
}


def check(ok, message):
    if not ok:
        raise RuntimeError("PUBLIC_SOURCE_IDENTITY_FAIL_CLOSED: " + message)


def git_output(root, *args):
    value = subprocess.run(["git", "-C", str(root), *args],
                           capture_output=True, text=True, timeout=25)
    check(value.returncode == 0, "git metadata command failed")
    return value.stdout.strip()


def git_blob_bytes(root, blob_sha):
    """Read exact pinned Git OBJECT bytes, never the platform-normalized worktree."""
    result = subprocess.run(["git", "-C", str(root), "cat-file",
                             "blob", blob_sha], capture_output=True, timeout=25)
    check(result.returncode == 0 and len(result.stdout) <= 1024 * 1024,
          "canonical pinned Git blob unavailable")
    return result.stdout


def only_checkout_eol_difference(canonical, worktree):
    # Checkout may replace LF with CRLF. No selector/argument/body mutation is
    # allowed to hide behind Git's text filters or hash-object normalization.
    return canonical.replace(b"\r\n", b"\n") == worktree.replace(
        b"\r\n", b"\n")


def inspect(directory):
    root = Path(directory)
    result = []
    for label, sample in sorted(SAMPLES.items()):
        path = root / label
        check(path.is_dir() and not path.is_symlink(), label + " missing")
        check(git_output(path, "rev-parse", "HEAD") == sample["commit"],
              label + " wrong checked-out revision")
        files = []
        for relative, blob in sorted(sample["files"].items()):
            f = path / relative
            check(f.is_file() and not f.is_symlink(), label + " file missing")
            content = f.read_bytes()
            check(len(content) <= 1024 * 1024, label + " oversized")
            check(git_output(path, "hash-object", relative) == blob
                  and not git_output(path, "diff", "--name-only",
                                     "--", relative),
                  label + " Git blob identity or checkout integrity changed: "
                  + relative)
            canonical = git_blob_bytes(path, blob)
            check(only_checkout_eol_difference(canonical, content),
                  label + " checkout diverges beyond LF/CRLF: " + relative)
            files.append({"path": relative, "gitBlobSha1": blob,
                          # Canonical SHA is stable on Linux and Windows.
                          "sha256": hashlib.sha256(canonical).hexdigest(),
                          "byteLength": len(canonical),
                          "checkoutSha256": hashlib.sha256(content).hexdigest(),
                          "checkoutByteLength": len(content),
                          "checkoutEolNormalized": canonical != content})
        result.append({
            "id": label, "repository": sample["repository"],
            "sourceCommit": sample["commit"], "files": files,
            "risk": sample["expected"],
            "sourceIdentityVerified": True,
            "sourceNUnitRuntime": "not_run",
            "targetNUnitRuntime": "not_run",
            "sourceTargetObservables": "not_run",
            "migrationBehavioralEquivalence": "unknown",
            "canAutoApply": False,
            "accepted": False,
        })
    return {"schema": SCHEMA, "projects": result,
            "publicSourceOnly": True, "privateSources": "not_used",
            "realProjectMigrationAccepted": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkout_root", type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    report = inspect(args.checkout_root)
    payload = json.dumps(report, indent=2, sort_keys=True) + "\n"
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(payload, encoding="utf-8")
    else:
        print(payload, end="")


if __name__ == "__main__":
    main()
