# P1-M — tagged Chromium Crashpad source provenance and minimal private witness contract

This draft continues P1-L #73. NO FIX CLAIM. Existing original Windows P47 artifacts, native-LIVE failures, real Node os.kill(SIGTERM), original 12-second gate, first-FAIL stop, exact PID/startIdentity/FILETIME and full 28-job Linux/Windows CI are unchanged.

## Proven source facts

Chromium tag 156.0.8078.80 pins Crashpad 53c4dcfae02976fe5c1b0b9e5347914b1ccfcd9f. The script pins exact Git blob SHA-1 for six public source files (chrome/VERSION, README.chromium, exception_handler_server.cc/.h, handler_main.cc, crashpad_handler.md) and optional offline verification against an existing checked-out tag. There is no network download or new OS permission. SHA-1 is used only as Git blob identity; SHA-256 of locally checked source bytes is also reported.

A source match does NOT prove the historical shipped Chrome executable or linked Crashpad binary equals that source. No original ZIP contains a direct observation of private clients_, threadpool callback post/consume, persistent_ boolean or in-flight upload. This tool reuses P1-L's exact SHA256/CRC/manifests/NUnit TRX/PID+FILETIME/12s verifier for original ZIPs, and deliberately returns UNKNOWN for the internal handler states even for real native-LIVE failures. It never labels no-reproduction a fix.

## Minimal next proof experiment

A test-owned, isolated, separately built Crashpad handler would need bounded timestamps and per-test-owned PID/native creation FILETIME showing registration, process handle wait, OnProcessEnd callback fired, successful completion-post, main-loop consumption, ClientData erasure, internal persistent mode and upload-in-flight boolean. Build provenance (version and exact handler binary SHA256) must be recorded. Do not collect raw handles, pipe names, full command lines, dump paths, report URLs or other users' processes. No ETW/foreign handle enumeration; original P47 remains pristine. This would establish mechanism on the instrumented test build only, not prove remediation in the product without original native-LIVE before/after gate evidence.

Usage: python scripts/p1m_crashpad_tagged_source_boundary.py --original LABEL EXPECTED_ZIP_SHA ORIGINAL.zip [--chromium-source-root already-checked-out-156-tree] --out sanitized.json

No merge, release or Windows product cleanup fix is authorized by this read-only diagnostic.
