# Dependency map and safe PR archive (2026-11-22)

## Core + MCP product integration, still draft

Core main `0745e6a9046dff8edb684931a84e7074a2382788` → [Core #68](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/68) (`a5393624497f9fad9bedc17a84605caa9e2df857`) → [#74](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/74) (`6f2155479822f6266ed1f25f2be97a65a17b3ee8`) → [#75](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/75) (`cf417ade4d0d836dd1866bf1d0e3a1a86720057b`) → [#78](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/78) (`d8600b158d6e871d8bf6048b8910b19a1f9481d6`).

MCP main `9069a6f019426362e10fe0ff992011c00a59e0c0` → [MCP #56](https://github.com/levencode-labs/selenium-playwright-migrator-mcp/pull/56) (`a9d7a121bb8480131a93a8c7237a3e9fcf7a6a57`) → [#57](https://github.com/levencode-labs/selenium-playwright-migrator-mcp/pull/57) (`87ff6b4c97183e9697a1f6f0b8ad4af4fbbabb14`) → [#58](https://github.com/levencode-labs/selenium-playwright-migrator-mcp/pull/58) (`ebe79f9ad7948956860dfb85457c6418d57ee01d`) → [#60](https://github.com/levencode-labs/selenium-playwright-migrator-mcp/pull/60) (`f94654b8ea494b8c269d07abe67faaa85edc1638`). Its two package-checkout pins point to Core #78 exact SHA.

Separate product inspector PR: [Core #58](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/58) and [MCP #54](https://github.com/levencode-labs/selenium-playwright-migrator-mcp/pull/54), **not** merged or archived as P1 experiments. Their historical Core Windows P47 had a genuine FAIL and cannot be laundered through green MCP CI.

## P1 historical source and experimentation

[Core issue #59](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/59) remains OPEN. P1 read-only research chain [#73](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/73) `0844494b828c204ee1eaab6aa83cd208ee0aa340` → [#76](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/76) `f099b60fdb9c6b82f902bf067df215c0c49342e0` → [#77](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/77) `b065d9d4b3d5bc664ec51b0008d40f4412a35410` (28/28). #77 prepares an unbuilt test-owned Crashpad source overlay and **cannot** establish a product fix.

**Closed without merge, branches retained:** Core historical P1 #62, #63, #64, #65, #67, #69, #71, #72. Canonical evidence/archive index and exact SHA: [issue #59 comment #7081574834](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/59#issuecomment-7081574834). Earlier Core #60/#61 were previously closed without merge. Original ZIP, TRX, native-LIVE FAIL, checksum and Actions refs are preserved; no branch deletion or force-push.

This RC integration branch is a **clean product-tree snapshot**, not a merge of experimental P1 Job Object branches. Do not close active product/source-preservation PR until the clean integration's own full CI and explicit owner decision.
