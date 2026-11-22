#!/usr/bin/env python3
"""P1-M: immutable Chromium 156 Crashpad SOURCE and original P47 ZIP witness boundary.

Read-only and offline: no Win32 handle access, process launch, network request,
ETW, dump/URL/pipe disclosure or product remediation claim.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import p1l_crashpad_client_boundary as original

SCHEMA = "migrator-p1m-crashpad-tagged-source-boundary/v1"
CHROMIUM_TAG = "156.0.8078.80"
CRASHPAD_REV = "53c4dcfae02976fe5c1b0b9e5347914b1ccfcd9f"
ROOT = "third_party/crashpad/crashpad/"
# SHA-1 of raw Git blobs from the exact public Chromium 156.0.8078.80 tag.
# This is source provenance, NOT an installed Chrome.exe identity.
TAG_BLOBS = {
    "chrome/VERSION": "31f37d1714a6f80b7a8194471e817f6eacae0466",
    "third_party/crashpad/README.chromium": "f7ea6573881414e25af1e0293556efd8a56ebecb",
    ROOT + "util/win/exception_handler_server.cc":
        "8af53a62b088fececd558f1436a84d4c2c4feabd",
    ROOT + "util/win/exception_handler_server.h":
        "6f67ce8adc64f453da4be6105ae5a79ee4c7fc36",
    ROOT + "handler/handler_main.cc":
        "072176d161de2dd9e45388daa61b67add1cbd497",
    ROOT + "handler/crashpad_handler.md":
        "3e722bad5f179fe67b08918030295c3dc0a24155",
}
MAX_SOURCE_FILE = 256 * 1024
MAX_ARCHIVES = 12

# Private handler witness shape for a future isolated/owned instrumented build.
# No raw handles, pipe names, command lines, database paths or upload URLs.
REQUIRED_WITNESS = (
    "handler_version_and_binary_sha256",
    "handler_pid_and_native_creation_filetime",
    "client_register_pid_and_native_creation_filetime",
    "registered_client_process_wait_state",
    "process_end_threadpool_callback_fired",
    "completion_port_post_succeeded",
    "completion_port_message_consumed",
    "client_data_erased",
    "persistent_mode_internal_boolean",
    "upload_in_flight_internal_boolean",
    "monotonic_event_order",
)


def ensure(ok, error):
    if not ok:
        raise original.Unverified(error)


def git_blob_sha1(data):
    return hashlib.sha1(b"blob " + str(len(data)).encode("ascii") +
                        b"\x00" + data).hexdigest()


def verify_tagged_source(path, expected=None):
    """Verify bytes against exact Git objects; expected override is test-only."""
    expected = TAG_BLOBS if expected is None else expected
    root = Path(path)
    ensure(root.is_dir() and not root.is_symlink(), "TAGGED_SOURCE_ROOT_UNSAFE")
    entries = []
    for relative, sha in sorted(expected.items()):
        parts = PurePosixPath(relative).parts
        ensure(parts and not any(p in ("", ".", "..") for p in parts),
               "TAGGED_SOURCE_PATH_UNSAFE")
        candidate = root
        for part in parts:
            candidate = candidate / part
            ensure(not candidate.is_symlink(), "TAGGED_SOURCE_SYMLINK_REJECTED")
        ensure(candidate.is_file(), "TAGGED_SOURCE_FILE_MISSING")
        ensure(candidate.stat().st_size <= MAX_SOURCE_FILE,
               "TAGGED_SOURCE_FILE_TOO_LARGE")
        content = candidate.read_bytes()
        ensure(git_blob_sha1(content) == sha, "TAGGED_SOURCE_BLOB_MISMATCH")
        if relative == "chrome/VERSION":
            ensure(content.decode("ascii") ==
                   "MAJOR=156\nMINOR=0\nBUILD=8078\nPATCH=80\n",
                   "CHROMIUM_VERSION_CONTENT_MISMATCH")
        if relative == "third_party/crashpad/README.chromium":
            ensure(("Revision: " + CRASHPAD_REV).encode("ascii") in content,
                   "CRASHPAD_SOURCE_REVISION_MISMATCH")
        entries.append({"path": relative, "gitBlobSha1": sha,
                        "sha256": hashlib.sha256(content).hexdigest()})
    return {"status": "tagged_source_exact_git_blobs_checked",
            "tag": CHROMIUM_TAG, "crashpadRevision": CRASHPAD_REV,
            "sourceFiles": entries, "deployedChromeBinaryProven": False}


def index_only():
    return {"status": "tagged_source_pins_only_not_locally_checked",
            "tag": CHROMIUM_TAG, "crashpadRevision": CRASHPAD_REV,
            "sourceFiles": [{"path": p, "gitBlobSha1": h}
                            for p, h in sorted(TAG_BLOBS.items())],
            "deployedChromeBinaryProven": False}


def evaluate(original_archives, source_root=None):
    ensure(0 < len(original_archives) <= MAX_ARCHIVES,
           "ORIGINAL_ZIP_COUNT_OUT_OF_BOUNDS")
    proof = verify_tagged_source(source_root) if source_root else index_only()
    observations = []
    for label, sha, filename in original_archives:
        ensure(label.isascii() and label.replace("_", "").replace("-", "").isalnum()
               and len(label) <= 40, "UNSAFE_EVIDENCE_LABEL")
        inspected = original.inspect_archive(filename, sha)
        fail = inspected["originalFailFastFailure"]
        observations.append({
            "label": label, "originalZipSha256": sha,
            "originalStatus": inspected["status"],
            "originalStage": fail["stage"] if fail else None,
            "nativeLiveCrashpadCount": (
                fail["sourceBoundNativeLiveCrashpads"] if fail else 0),
            "originalSurvivors": fail["survivors"] if fail else [],
            "unchangedOriginal12sGate": True,
            "originalReadOnly": True,
            "registeredClientsInternal": "unknown",
            "clientOnProcessEndPosted": "unknown",
            "clientCompletionConsumed": "unknown",
            "persistentHandlerMode": "unknown",
            "crashUploadInFlight": "unknown",
            "productCleanupFixProven": False,
        })
    return {
        "schema": SCHEMA,
        "chromiumSource": proof,
        "originals": observations,
        "historicalNativeLiveFailures": sum(
            x["originalStatus"] == "native_live_failure_observed"
            for x in observations),
        "registeredClientIdentityProven": False,
        "callbackProcessedProven": False,
        "pendingUploadProven": False,
        "rootCauseProven": False,
        "productFixProven": False,
        "fixClaim": False,
        "requiredPrivateHandlerWitness": list(REQUIRED_WITNESS),
        "nextExperiment": "independent_test_owned_instrumented_Crashpad_handler_only",
        "noPrivilegedDiagnostics": True,
        "noNewProcessAccess": True,
        "noOriginalP47Changes": True,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--original", action="append", nargs=3, required=True,
                        metavar=("LABEL", "SHA256", "ORIGINAL_ZIP"))
    parser.add_argument("--chromium-source-root", type=Path,
                        help="Optional already checked-out exact tagged Chromium 156 tree")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    result = evaluate(args.original, args.chromium_source_root)
    payload = json.dumps(result, indent=2, sort_keys=True) + "\n"
    if args.out:
        args.out.write_text(payload, encoding="utf-8")
    else:
        print(payload, end="")


if __name__ == "__main__":
    main()
