#!/usr/bin/env python3
"""preview.48: independent, immutable upstream Playwright .NET 1.63.0 source oracle.

Only observes pinned upstream *source*; never executes library implementation,
invented exceptions, or customer C#. In CI the upstream checkout is
read-only and pinned to an exact commit and Git blob identities.
"""
import json
from pathlib import Path
import subprocess
import sys

REV="6d5c263758e8b50cfc18d3abb384f251b7deb370"
BLOBS={
    "src/Playwright/Core/Browser.cs":"d977433b22f2188118d198b538d9912f17fd4660",
    "src/Playwright/Core/PlaywrightImpl.cs":"20bd6f7e58fa72d74d8b2c5496ebed2c20bdbea5",
    "src/Playwright/Transport/Connection.cs":"d897febf8212751004e1a817dff87d606c5680ac",
    "src/Playwright/Core/Page.cs":"abeb40d1454d0bd065c58f631fe930ea4509a943",
    "src/Playwright/Core/BrowserContext.cs":"34e575050c214b9c47c681f6045e6c785fc74cb5",
}
def require(ok,reason):
    if not ok:
        raise AssertionError("P48_UPSTREAM_SOURCE_GUARD: "+reason)

def method_body(text, signature):
    require(text.count(signature)==1,"exact method signature "+signature)
    start=text.index("{",text.index(signature))
    depth=0
    for i in range(start,len(text)):
        if text[i]=="{":
            depth+=1
        elif text[i]=="}":
            depth-=1
            if depth==0:
                return text[start:i+1]
    raise AssertionError("P48_UPSTREAM_UNCLOSED_METHOD "+signature)

def prove(texts):
    browser=method_body(texts["src/Playwright/Core/Browser.cs"],
        "public async Task CloseAsync(BrowserCloseOptions? options = default)")
    page=method_body(texts["src/Playwright/Core/Page.cs"],
        "public async Task CloseAsync(PageCloseOptions? options = default)")
    context=method_body(texts["src/Playwright/Core/BrowserContext.cs"],
        "public async Task CloseAsync(BrowserContextCloseOptions? options = default)")
    implementation=method_body(texts["src/Playwright/Core/PlaywrightImpl.cs"],
        "private void Dispose(bool disposing)")
    connection=method_body(texts["src/Playwright/Transport/Connection.cs"],
        "private void Dispose(bool disposing)")
    require('catch (Exception e) when (DriverMessages.IsTargetClosedError(e))' in browser,
        "IBrowser.CloseAsync exact TargetClosed predicate internally handles exception")
    require("Swallow exception" in browser,
        "Browser.CloseAsync source did not document swallow")
    require("await _closedTcs.Task" in browser,
        "Browser.CloseAsync dispatch/close wait structure changed")
    require('catch (Exception e) when (DriverMessages.IsTargetClosedError(e) && options?.RunBeforeUnload != true)' in page,
        "IPage.CloseAsync exact documented TargetClosed swallow changed")
    require('await SendMessageToServerAsync("close"' in context
        and 'catch (Exception e) when (DriverMessages.IsTargetClosedError(e))' not in context,
        "IBrowserContext.CloseAsync source boundary changed")
    require("_connection?.Dispose();" in implementation,
        "IPlaywright.Dispose no longer delegates to internal Connection.Dispose")
    require("_closedError ??= new TargetClosedException" in connection
        and "TaskCompletionSource.TrySetException(_closedError)" in connection
        and "_queue.Dispose();" in connection
        and "Close?.Invoke(this, new TargetClosedException" in connection,
        "Connection.Dispose callback delivery source shape changed")
    # This is *not* a whole-program no-throw proof: event callbacks may
    # fail and arbitrary plugin code remains outside captured source.
    return {
        "schema":"migrator-preview48-upstream-playwright-close-source/v1",
        "upstreamCommit":REV,"playwrightPackageVersion":"1.63.0",
        "evidenceKind":"exact_pinned_upstream_source_only",
        "browserCloseTargetClosed":"internally_caught_and_swallowed_in_pinned_upstream",
        "pageCloseTargetClosed":"internally_caught_except_beforeunload_path",
        "contextCloseTargetClosed":"not_internally_swallowed_by_same_filter",
        "playwrightDispose":"delegates_to_connection_dispose",
        "connectionDispose":"sets_targetclosed_on_pending_tasks_and_dispatches_close",
        "browserCloseActualException":"not_claimed",
        "playwrightDisposeActualException":"not_claimed",
        "eventHandlerAndArbitraryFailures":"not_proven",
        "nunitPrecedence":"must_be_observed_independently",
        "sourceContractIsNotRuntimeExceptionProof":True,
    }

def negative_controls(original):
    checks=[
      ("Browser missing filter","src/Playwright/Core/Browser.cs",
       "DriverMessages.IsTargetClosedError(e)","false"),
      ("Page missing filter","src/Playwright/Core/Page.cs",
       "DriverMessages.IsTargetClosedError(e)","false"),
      ("Context alternate close","src/Playwright/Core/BrowserContext.cs",
       'await SendMessageToServerAsync("close"', 'await Task.CompletedTask; // no command'),
      ("Playwright missing delegation","src/Playwright/Core/PlaywrightImpl.cs",
       "_connection?.Dispose();","_connection = null;"),
      ("Connection missing callback","src/Playwright/Transport/Connection.cs",
       "TaskCompletionSource.TrySetException(_closedError)",
       "TaskCompletionSource.TrySetCanceled()"),
    ]
    for label,path,old,new in checks:
        require(old in original[path],"negative control has no source anchor: "+label)
        modified=dict(original)
        modified[path]=original[path].replace(old,new,1)
        try:prove(modified)
        except AssertionError:continue
        raise AssertionError("P48_UPSTREAM_NEGATIVE_CONTROL_ACCEPTED: "+label)
    return len(checks)

def git(root,*args):
    p=subprocess.run(["git","-C",str(root),*args],
        capture_output=True,text=True,check=True)
    return p.stdout.strip()

def main():
    require(len(sys.argv)==2,"usage: <exact-upstream-checkout>")
    upstream=Path(sys.argv[1]).resolve()
    require(git(upstream,"rev-parse","HEAD")==REV,"upstream commit moved")
    originals={}
    for path,blob in BLOBS.items():
        p=upstream/path
        require(p.is_file(),"source file absent: "+path)
        require(git(upstream,"hash-object",path)==blob,
            "upstream Git blob mismatch: "+path)
        originals[path]=p.read_text(encoding="utf-8-sig")
    summary=prove(originals)
    summary["upstreamGitBlobs"]=BLOBS
    summary["negativeControlsBlocked"]=negative_controls(originals)
    dest=Path("artifacts/preview48")
    dest.mkdir(parents=True,exist_ok=True)
    (dest/"upstream-oracle.json").write_text(
        json.dumps(summary,indent=2,sort_keys=True)+"\n",encoding="utf-8")
    print("POM_PREVIEW48_PINNED_UPSTREAM_CLOSE_SOURCE_ORACLE_PASSED")
    print(json.dumps({k:v for k,v in summary.items() if k not in
        ("upstreamGitBlobs",)},sort_keys=True))
if __name__=="__main__":main()
