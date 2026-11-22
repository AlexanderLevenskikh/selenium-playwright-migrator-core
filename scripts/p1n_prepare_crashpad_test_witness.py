#!/usr/bin/env python3
"""Prepare a separate test-owned Chromium 156 Crashpad witness overlay.

NEVER modifies the pinned source checkout, historical ZIPs or original P47.
NEVER builds, runs, kills or inspects processes. Not a product fix.
"""
import argparse
import hashlib
import json
from pathlib import Path

import p1m_crashpad_tagged_source_boundary as pinned
from p1l_crashpad_client_boundary import Unverified

SCHEMA = "migrator-p1n-test-owned-overlay/v1"
SERVER = pinned.ROOT + "util/win/exception_handler_server.cc"
UPLOADER = pinned.ROOT + "handler/crash_report_upload_thread.cc"
HEADER = pinned.ROOT + "util/win/p1n_test_owned_witness.h"
UPLOADER_GIT_SHA1 = "bec8714263779d57bfff5ba83cfe26776acf3732"

# Header compiles only in an explicitly instrumented test-owned Win32 build.
# No raw HANDLE value, pipe name, command line, dump, database path or URL.
HEADER_TEXT = r"""
#ifndef CRASHPAD_UTIL_WIN_P1N_TEST_OWNED_WITNESS_H_
#define CRASHPAD_UTIL_WIN_P1N_TEST_OWNED_WITNESS_H_
#if defined(_WIN32) && defined(MIGRATOR_P1_TEST_OWNED_WITNESS)
#include <windows.h>
#include <cstdio>
#include <cstdint>
namespace crashpad { namespace p1n_test_witness {
inline unsigned long long Created(HANDLE h) {
  if (!h || h == INVALID_HANDLE_VALUE) return 0;
  FILETIME a = {}, b = {}, c = {}, d = {};
  if (!::GetProcessTimes(h, &a, &b, &c, &d)) return 0;
  return (static_cast<unsigned long long>(a.dwHighDateTime) << 32) |
         a.dwLowDateTime;
}
inline void Emit(const char* phase, void* raw_client,
                 long clients, int persistent, int upload) {
  wchar_t dest[1024] = {};
  DWORD len = ::GetEnvironmentVariableW(
      L"MIGRATOR_P1_TEST_WITNESS_FILE", dest, 1024);
  if (!len || len >= 1024) return;
  static SRWLOCK lock = SRWLOCK_INIT;
  static LONG seq = 0;
  ::AcquireSRWLockExclusive(&lock);
  LONG n = ++seq;
  if (n > 512) {
    ::ReleaseSRWLockExclusive(&lock);
    return;
  }
  HANDLE client = static_cast<HANDLE>(raw_client);
  DWORD pid = client && client != INVALID_HANDLE_VALUE ?
              ::GetProcessId(client) : 0;
  char line[512] = {};
  int used = std::snprintf(line, sizeof(line),
      "{\"schema\":\"migrator-p1n-test-owned-handler-event/v1\","
      "\"phase\":\"%s\",\"seq\":%ld,\"tickMs\":%llu,"
      "\"handlerPid\":%lu,\"handlerFiletime\":\"%llu\","
      "\"clientPid\":%lu,\"clientFiletime\":\"%llu\","
      "\"registeredCount\":%ld,\"persistentMode\":%d,"
      "\"uploadInFlight\":%d}\n",
      phase, n, static_cast<unsigned long long>(::GetTickCount64()),
      ::GetCurrentProcessId(), Created(::GetCurrentProcess()),
      pid, Created(client), clients, persistent, upload);
  if (used > 0 && used < static_cast<int>(sizeof(line))) {
    HANDLE f = ::CreateFileW(dest, FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
      DWORD written = 0;
      ::WriteFile(f, line, static_cast<DWORD>(used), &written, nullptr);
      ::CloseHandle(f);
    }
  }
  ::ReleaseSRWLockExclusive(&lock);
}
} } // namespace crashpad::p1n_test_witness
#else
namespace crashpad { namespace p1n_test_witness {
inline void Emit(const char*, void*, long, int, int) {}
} }
#endif
#endif
""".lstrip()

SERVER_EDITS = (
    ('#include "util/win/registration_protocol_win.h"',
     '#include "util/win/registration_protocol_win.h"\n'
     '#include "util/win/p1n_test_owned_witness.h"'),
    ('    clients_.insert(client);',
     '    clients_.insert(client);\n'
     '    p1n_test_witness::Emit("initial_register", client->process(),\n'
     '        static_cast<long>(clients_.size()), -1, -1);'),
    ('    service_context.clients()->insert(client);',
     '    service_context.clients()->insert(client);\n'
     '    p1n_test_witness::Emit("pipe_register", client->process(),\n'
     '        static_cast<long>(service_context.clients()->size()), -1, -1);'),
    ('  delegate->ExceptionHandlerServerStarted();',
     '  delegate->ExceptionHandlerServerStarted();\n'
     '  p1n_test_witness::Emit("mode", nullptr,\n'
     '      static_cast<long>(clients_.size()), persistent_ ? 1 : 0, -1);'),
    ('    clients_.erase(client);\n    delete client;',
     '    p1n_test_witness::Emit("completion_consumed", client->process(),\n'
     '        static_cast<long>(clients_.size()), persistent_ ? 1 : 0, -1);\n'
     '    clients_.erase(client);\n'
     '    p1n_test_witness::Emit("client_erased", client->process(),\n'
     '        static_cast<long>(clients_.size()), persistent_ ? 1 : 0, -1);\n'
     '    delete client;'),
    ('  PostQueuedCompletionStatus(client->port(), 0, ULONG_PTR(client), nullptr);',
     '  p1n_test_witness::Emit("process_end_callback", client->process(),\n'
     '      -1, -1, -1);\n'
     '  BOOL posted = PostQueuedCompletionStatus(\n'
     '      client->port(), 0, ULONG_PTR(client), nullptr);\n'
     '  p1n_test_witness::Emit(posted ? "completion_posted"\n'
     '      : "completion_post_failed", client->process(), -1, -1, -1);')
)
UPLOAD_EDITS = (
    ('#include "handler/crash_report_upload_thread.h"',
     '#include "handler/crash_report_upload_thread.h"\n'
     '#include "util/win/p1n_test_owned_witness.h"'),
    ('  std::string response_body;\n  UploadResult upload_result =\n'
     '      UploadReport(upload_report.get(), &response_body);',
     '  p1n_test_witness::Emit("upload_begin", nullptr, -1, -1, 1);\n'
     '  std::string response_body;\n  UploadResult upload_result =\n'
     '      UploadReport(upload_report.get(), &response_body);\n'
     '  p1n_test_witness::Emit("upload_end", nullptr, -1, -1, 0);')
)


def patch_exact(source, edits):
    for anchor, replacement in edits:
        if source.count(anchor) != 1:
            raise Unverified("TEST_OWNED_PATCH_ANCHOR_NOT_EXACT")
        source = source.replace(anchor, replacement, 1)
    return source


def verify_uploader(root):
    path = root / UPLOADER
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 262144:
        raise Unverified("TEST_OWNED_TAGGED_UPLOADER_MISSING_OR_UNSAFE")
    data = path.read_bytes()
    if pinned.git_blob_sha1(data) != UPLOADER_GIT_SHA1:
        raise Unverified("TEST_OWNED_UPLOADER_BLOB_MISMATCH")
    return data.decode("utf-8")


def prepare(source_root, overlay_root):
    src = Path(source_root).resolve(strict=True)
    dst = Path(overlay_root).resolve()
    if src == dst or src in dst.parents or dst in src.parents:
        raise Unverified("TEST_OWNED_OVERLAY_MUST_BE_ISOLATED")
    if dst.exists():
        raise Unverified("TEST_OWNED_OVERLAY_OUTPUT_EXISTS")
    source_check = pinned.verify_tagged_source(src)
    uploader = verify_uploader(src)
    server = (src / SERVER).read_text(encoding="utf-8")
    files = {
        SERVER: patch_exact(server, SERVER_EDITS),
        UPLOADER: patch_exact(uploader, UPLOAD_EDITS),
        HEADER: HEADER_TEXT,
    }
    dst.mkdir(parents=True, exist_ok=False)
    out = []
    for relative, content in sorted(files.items()):
        target = dst / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        data = content.encode("utf-8")
        target.write_bytes(data)
        out.append({"path": relative, "sha256": hashlib.sha256(data).hexdigest(),
                    "bytes": len(data)})
    receipt = {
        "schema": SCHEMA, "tag": pinned.CHROMIUM_TAG,
        "crashpadRevision": pinned.CRASHPAD_REV,
        "sourceIdentity": source_check["status"],
        "uploaderGitBlobSha1": UPLOADER_GIT_SHA1,
        "testOwnedOverlayFiles": out,
        "compiled": False, "executed": False,
        "originalP47Changed": False, "stockChromeChanged": False,
        "registeredClientIdentityInOriginalProven": False,
        "rootCauseProven": False, "productFixProven": False,
        "fixClaim": False,
    }
    (dst / "overlay-receipt.json").write_text(
        json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tagged-chromium", type=Path, required=True)
    parser.add_argument("--new-overlay", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare(args.tagged_chromium, args.new_overlay),
                     indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
