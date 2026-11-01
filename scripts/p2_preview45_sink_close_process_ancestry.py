#!/usr/bin/env python3
"""preview.45 public real Chrome, actual C# Console.Error sink failure and
observed parent/child process ancestry on Linux and Windows.

The fixture is a standalone fault PROBE, not auto-migrated target output.
Real CloseAsync/Dispose exceptions are reported only if the actual API
throws. Post-close injected errors are explicitly labelled synthetic.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import threading
import time
from http.server import ThreadingHTTPServer

import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview39_external_exception_real_chromium as p39
import p2_preview42_fixture_lifecycle_real_chromium as p42
import p2_preview44_create_failure_process_audit as p44
import p2_preview44_create_failure_process_audit as p44

FIXTURE=r"""
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Playwright;
namespace Fictional.P45;
public sealed class OriginalFault : Exception {
    public OriginalFault():base("P45_PRIMARY_MARKER"){}
}
public sealed class ThrowingSink : StringWriter {
    public int FailedWrites {get;private set;}
    public override void WriteLine(string? value) {
        FailedWrites++;
        throw new IOException("P45_THROWING_SINK_INJECTED");
    }
}
[TestFixture, NonParallelizable] public sealed class FaultEvidenceTests {
    [Test] public async Task Probe() {
        var stage=Environment.GetEnvironmentVariable("POM45_STAGE")!;
        var folder=Environment.GetEnvironmentVariable("POM45_EVIDENCE")!;
        IPlaywright? pw=null;
        IBrowser? browser=null;
        IBrowserContext? context=null;
        IPage? page=null;
        var closeApiErrors=new System.Collections.Generic.List<string>();
        var disposeApiErrors=new System.Collections.Generic.List<string>();
        bool sinkFailureObserved=false, sinkRestored=false;
        bool syntheticAfterRealClose=false;
        bool pageCloseAttempted=false,contextCloseAttempted=false;
        bool browserCloseAttempted=false,disposed=false;
        bool killAcknowledged=false;
        string? originalType=null;
        try {
            pw=await Microsoft.Playwright.Playwright.CreateAsync();
            browser=await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
                ExecutablePath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN"),
                Headless=true,Args=new[]{"--no-sandbox","--disable-dev-shm-usage",
                    "--window-size=1280,720"}
            });
            context=await browser.NewContextAsync();
            page=await context.NewPageAsync();
            Assert.That(Object.ReferenceEquals(page.Context,context),Is.True);
            await page.GotoAsync(Environment.GetEnvironmentVariable("POM45_URL")!);
            Assert.That(await page.EvaluateAsync<string>(
                "() => JSON.stringify(window.snapshot())"),Does.Contain("ready"));
            if(stage=="healthy") {
                await Task.Delay(7000);
            } else if(stage=="real-close-after-kill") {
                var ready=Path.Combine(folder,"kill-ready");
                var ack=Path.Combine(folder,"kill-ack");
                File.WriteAllText(ready,"ready");
                for(int i=0;i<120&&!File.Exists(ack);i++)
                    await Task.Delay(250);
                Assert.That(File.Exists(ack),Is.True,"OS kill acknowledgement missing");
                killAcknowledged=File.ReadAllText(ack)=="killed";
                try {await page.CloseAsync();pageCloseAttempted=true;}
                catch(Exception e) {
                    pageCloseAttempted=true;
                    closeApiErrors.Add("IPage.CloseAsync:"+e.GetType().FullName);
                }
                try {await context.CloseAsync();contextCloseAttempted=true;}
                catch(Exception e) {
                    contextCloseAttempted=true;
                    closeApiErrors.Add("IBrowserContext.CloseAsync:"+e.GetType().FullName);
                }
                try {await browser.CloseAsync();browserCloseAttempted=true;}
                catch(Exception e) {
                    browserCloseAttempted=true;
                    closeApiErrors.Add("IBrowser.CloseAsync:"+e.GetType().FullName);
                }
                try {pw.Dispose();disposed=true;}
                catch(Exception e) {
                    disposed=true;
                    disposeApiErrors.Add("IPlaywright.Dispose:"+e.GetType().FullName);
                }
                // No exception is invented if the API returned normally.
            } else if(stage=="dispose-repeat") {
                await page.CloseAsync();pageCloseAttempted=true;
                await context.CloseAsync();contextCloseAttempted=true;
                await browser.CloseAsync();browserCloseAttempted=true;
                try {pw.Dispose();disposed=true;}
                catch(Exception e) {
                    disposed=true;
                    disposeApiErrors.Add("IPlaywright.Dispose:"+e.GetType().FullName);
                }
                try {pw.Dispose();}
                catch(Exception e) {
                    disposeApiErrors.Add("IPlaywright.DisposeAgain:"+e.GetType().FullName);
                }
            } else if(stage=="sink-fault-primary") {
                var original=Console.Error;
                var sink=new ThrowingSink();
                try {
                    Console.SetError(sink);
                    try {
                        throw new OriginalFault();
                    }
                    catch(Exception e) when(e is OriginalFault) {
                        originalType=e.GetType().FullName;
                        try {
                            await page.CloseAsync();pageCloseAttempted=true;
                            syntheticAfterRealClose=true;
                            throw new IOException("P45_INJECTED_POST_CLOSE_ERROR");
                        } catch {
                            try {Console.Error.WriteLine(
                                "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED");}
                            catch(IOException){sinkFailureObserved=true;}
                        }
                        try {await context.CloseAsync();contextCloseAttempted=true;}
                        catch {
                            try {Console.Error.WriteLine(
                                "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED");}
                            catch(IOException){sinkFailureObserved=true;}
                        }
                        try {await browser.CloseAsync();browserCloseAttempted=true;}
                        catch {
                            try {Console.Error.WriteLine(
                                "MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED");}
                            catch(IOException){sinkFailureObserved=true;}
                        }
                        try {pw.Dispose();disposed=true;}
                        catch {
                            try {Console.Error.WriteLine(
                                "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED");}
                            catch(IOException){sinkFailureObserved=true;}
                        }
                        Assert.That(sink.FailedWrites,Is.GreaterThan(0),
                            "Actual Console.Error.WriteLine did not fail");
                        throw; // OriginalFault retains CLR type and stack
                    }
                } finally {
                    Console.SetError(original);
                    sinkRestored=true;
                }
            } else {
                Assert.Fail("Unknown stage");
            }
        }
        finally {
            // The receipt has no exception messages or private values.
            if(page!=null && !pageCloseAttempted) {
                try {await page.CloseAsync();pageCloseAttempted=true;}
                catch(Exception e) {
                    pageCloseAttempted=true;
                    closeApiErrors.Add("IPage.CloseAsyncFinally:"+e.GetType().FullName);
                }
            }
            if(context!=null && !contextCloseAttempted) {
                try {await context.CloseAsync();contextCloseAttempted=true;}
                catch(Exception e) {
                    contextCloseAttempted=true;
                    closeApiErrors.Add("IBrowserContext.CloseAsyncFinally:"+e.GetType().FullName);
                }
            }
            if(browser!=null && !browserCloseAttempted) {
                try {await browser.CloseAsync();browserCloseAttempted=true;}
                catch(Exception e) {
                    browserCloseAttempted=true;
                    closeApiErrors.Add("IBrowser.CloseAsyncFinally:"+e.GetType().FullName);
                }
            }
            if(pw!=null && !disposed) {
                try {pw.Dispose();disposed=true;}
                catch(Exception e) {
                    disposed=true;
                    disposeApiErrors.Add("IPlaywright.DisposeFinally:"+e.GetType().FullName);
                }
            }
            File.WriteAllText(Path.Combine(folder,"fault-"+stage+".json"),
                JsonSerializer.Serialize(new {
                    stage,closeApiErrors,disposeApiErrors,
                    sinkFailureObserved,sinkRestored,
                    syntheticAfterRealClose,killAcknowledged,
                    pageCloseAttempted,contextCloseAttempted,
                    browserCloseAttempted,disposed,originalType
                }));
        }
    }
}
"""
# This script has no runtime Selenium dependency.
CODES=("MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
       "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED",
       "MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED",
       "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED")
STAGES=("healthy","sink-fault-primary","real-close-after-kill","dispose-repeat")
def snapshot(chrome):
    """Sanitized process metadata: id, parent, creation identity, label only."""
    data={}
    if os.name=="nt":
        ps=r"""
        $ErrorActionPreference='Stop'
        $all=Get-CimInstance Win32_Process
        foreach($p in $all) {
          $kind='other'
          if($p.ExecutablePath -and $p.ExecutablePath -ieq $env:MIGRATOR_CHROME_BIN) {
            $kind='chrome'
          } elseif($p.Name -ieq 'node.exe' -and $p.CommandLine -and
              $p.CommandLine -match 'playwright.*cli\.js') {
            $kind='driver'
          }
          $created=$p.CreationDate
          Write-Output "$($p.ProcessId)|$($p.ParentProcessId)|$created|$kind"
        }
        """
        r=subprocess.run(["powershell","-NoProfile","-NonInteractive",
            "-Command",ps],capture_output=True,text=True,timeout=35)
        if r.returncode:
            raise RuntimeError("P45_WINDOWS_CIM_PROCESS_METADATA_UNAVAILABLE")
        for line in r.stdout.splitlines():
            parts=line.strip().split("|")
            if len(parts)!=4 or not parts[0].isdigit() or not parts[1].isdigit():
                continue
            pid=int(parts[0])
            data[pid]=(int(parts[1]),str(parts[2]),parts[3])
        if not data:
            raise RuntimeError("P45_WINDOWS_CIM_RETURNED_EMPTY_PROCESS_TABLE")
        return data
    if not sys.platform.startswith("linux") or not Path("/proc").exists():
        raise RuntimeError("P45_LINUX_PROC_UNAVAILABLE")
    exact=os.path.realpath(chrome)
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():continue
        pid=int(entry.name)
        try:
            raw=(entry/"stat").read_text(encoding="utf-8")
            suffix=raw[raw.rfind(")")+2:].split()
            ppid=int(suffix[1])
            start=suffix[19]
            executable=os.path.realpath(os.readlink(entry/"exe"))
            kind="other"
            if executable==exact:kind="chrome"
            else:
                cmd=(entry/"cmdline").read_bytes().replace(b"\x00",b" ").lower()
                if b"playwright" in cmd and b"cli.js" in cmd:
                    kind="driver"
            data[pid]=(ppid,start,kind)
        except (FileNotFoundError,PermissionError,ProcessLookupError,OSError,
                IndexError,ValueError):
            continue
    return data

def ancestry(data,pid,root):
    chain=[]
    seen=set()
    while pid in data and pid not in seen and len(chain)<64:
        seen.add(pid);chain.append(pid)
        if pid==root:return chain
        pid=data[pid][0]
    return None

def run_stage(stage,project,evidence,chrome,url):
    baseline=snapshot(chrome)
    env=dict(os.environ,POM45_EVIDENCE=str(evidence),
        POM45_STAGE=stage,POM45_URL=url)
    command=["dotnet","test","-c","Release","--no-build","--no-restore",
        "--nologo","--logger","trx;LogFileName="+stage+".trx",
        "--results-directory",str(evidence),
        "--filter","FullyQualifiedName~FaultEvidenceTests&Name~Probe"]
    started=time.monotonic()
    proc=subprocess.Popen(command,cwd=project,env=env,stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,text=True,encoding="utf-8",errors="replace")
    root=proc.pid
    observed={}
    rooted=0
    edges=set()
    kill_status="not_requested"
    deadline=started+190
    while proc.poll() is None:
        data=snapshot(chrome)
        for pid,(parent,start,kind) in data.items():
            if kind not in ("chrome","driver"):continue
            # root must be in a verifiable current parent chain.
            chain=ancestry(data,pid,root)
            if chain is None:continue
            key=(pid,start)
            observed[key]=kind
            rooted+=1
            edges.update(zip(chain,chain[1:]))
        if stage=="real-close-after-kill" and (evidence/"kill-ready").exists() \
                and not (evidence/"kill-ack").exists():
            # Choose an exact Chrome root attached to this NUnit invocation.
            roots=[]
            for (pid,start),kind in observed.items():
                if kind!="chrome" or pid not in data or data[pid][1]!=start:continue
                parent=data[pid][0]
                if parent not in data or data[parent][2]!="chrome":
                    roots.append(pid)
            if roots:
                pid=sorted(roots)[0]
                try:
                    os.kill(pid,signal.SIGTERM)
                    kill_status="sent_to_observed_chrome"
                except (OSError,PermissionError):
                    kill_status="kill_not_permitted"
            else:
                kill_status="root_not_observed"
            (evidence/"kill-ack").write_text(
                "killed" if kill_status=="sent_to_observed_chrome" else "skipped",
                encoding="utf-8")
        if time.monotonic()>deadline:
            proc.kill()
            raise AssertionError("P45_NUNIT_PROCESS_TIMEOUT: "+stage)
        time.sleep(0.4 if os.name=="nt" else .2)
    stdout,stderr=proc.communicate(timeout=30)
    data=snapshot(chrome)
    for pid,(parent,start,kind) in data.items():
        if kind in ("chrome","driver"):
            chain=ancestry(data,pid,root)
            if chain:
                observed[(pid,start)]=kind
                edges.update(zip(chain,chain[1:]))
    survivors=set()
    end=time.monotonic()+12
    while True:
        now=snapshot(chrome)
        survivors={key for key in observed
            if key[0] in now and now[key[0]][1]==key[1]}
        if not survivors or time.monotonic()>end:break
        time.sleep(.5)
    trx=evidence/(stage+".trx")
    p37.check(trx.is_file(),"NUnit TRX missing "+stage+
        " "+stdout[-600:]+stderr[-600:])
    results=p37.read_trx(trx)
    shouldPass=stage!="sink-fault-primary"
    p37.check(results=={"Probe":"Passed" if shouldPass else "Failed"}
        and (proc.returncode==0)==shouldPass,
        "Wrong actual NUnit outcome "+stage+" "+str(results)+stdout[-800:])
    receipt=evidence/("fault-"+stage+".json")
    p37.check(receipt.is_file(),"Actual cleanup receipt missing "+stage)
    facts=json.loads(receipt.read_text(encoding="utf-8"))
    p37.check(facts["disposed"] and facts["pageCloseAttempted"]
        and facts["contextCloseAttempted"]
        and facts["browserCloseAttempted"],"Incomplete managed cleanup "+stage)
    if stage=="sink-fault-primary":
        p37.check("Fictional.P45.OriginalFault" in trx.read_text(encoding="utf-8")
            and facts["sinkFailureObserved"] and facts["sinkRestored"]
            and facts["syntheticAfterRealClose"],
            "Real BCL throwing writer did not preserve primary exception")
    if stage=="healthy":
        p37.check(any(kind=="chrome" for kind in observed.values())
            and any(kind=="driver" for kind in observed.values()),
            "Real Chrome and driver were not observed as NUnit descendants")
    p37.check(not survivors,"Owned observed descendant PID survived "+stage+
        " "+str(sorted(survivors)))
    return {
        "outcome":"Passed" if shouldPass else "Failed",
        "actualChromeKill":kill_status,
        "sourceOwnedChromiumProcessesObserved":
            sum(k=="chrome" for k in observed.values()),
        "sourceOwnedPlaywrightDriversObserved":
            sum(k=="driver" for k in observed.values()),
        "observedParentEdges":len(edges),
        "survivingObservedDescendantCount":len(survivors),
        "pidIdentityIncludesStartTime":True,
        "sinkFailureKind":"real_Console_Error_WriteLine_on_injected_throwing_TextWriter"
            if stage=="sink-fault-primary" else "not_run",
        "syntheticPostCloseError":facts["syntheticAfterRealClose"],
        "realCloseAsyncExceptionTypes":facts["closeApiErrors"],
        "realDisposeExceptionTypes":facts["disposeApiErrors"],
        "realCloseAsyncFailure":"observed" if facts["closeApiErrors"]
            else "not_observed",
        "realDisposeFailure":"observed" if facts["disposeApiErrors"]
            else "not_observed",
        "primaryExceptionPreserved":facts["originalType"]==
            "Fictional.P45.OriginalFault" if stage=="sink-fault-primary" else None,
        "elapsedSec":round(time.monotonic()-started,2)
    }

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    p37.check(chrome.is_file() and driver.is_file(),
        "Explicit same Chrome for Testing and chromedriver required")
    cli=repo/"Migrator.AssemblyCli/bin/Release/net10.0/Migrator.AssemblyCli.dll"
    p37.check(cli.is_file(),"Compiled exact source-backed Core CLI missing")
    evidence=repo/"artifacts/pom-preview45"
    evidence.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="fictional-p45-witness-") as tmp:
        root=Path(tmp);pom=root/"pom";pom.mkdir()
        canon=(repo/"Migrator.Application.Smoke/PomPreview44SafeDiagnosticsSmoke.cs")
        p42_source=(repo/"Migrator.Application.Smoke/PomPreview42FixtureLifecycleSmoke.cs")
        code=p42_source.read_text(encoding="utf-8")
        fixture=p42.synthetic("Fixture",code)
        # Read source-only canonical fixture via prior Core generated C#? The
        # preview44 original public test retains the exact same method syntax.
        page,base,atomic=p44.p43.atomic_source(repo)
        cursor=0
        for diag in ("MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED",
                     "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED",
                     "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
                     "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED"):
            i=atomic.find("catch {}",cursor)
            p37.check(i>=0,"Missing source-attested cleanup handler")
            replacement='catch { try { System.Console.Error.WriteLine("'+diag+'"); } catch {} }'
            atomic=atomic[:i]+replacement+atomic[i+len("catch {}"):]
            cursor=i+len(replacement)
        for file,source in (("Page.cs",page),("TestBase.cs",base),
                            ("NUnitFixture.cs",atomic)):
            (pom/file).write_bytes(source.encode("utf-8"))
        file_hash={f.name:hashlib.sha256(f.read_bytes()).hexdigest()
            for f in sorted(pom.iterdir())}
        source_sha=p39.cli_json(["dotnet",cli,"pom-inspect",pom],repo,0)[
            "sourceTreeHash"]
        proven={}
        for method in ("Missing","Timeout"):
            args=["dotnet",cli,"pom-cleanup-fault-witness",pom,
                "Fictional.P42.SourcePage",method,source_sha,
                "Page","Context","Browser","PW"]
            plan=p39.cli_json(args,repo,0)
            proof=plan["fixtureLifecycle"]
            p37.check(plan["status"]=="requires_review" and
                plan["schemaVersion"]=="migrator-pom-external-cleanup-fault-witness/v1"
                and proof["sourceSinkNotRebound"]
                and proof["faultWitnessSourceOnly"]
                and not proof["realCleanupFailureCertified"]
                and not proof["osProcessAncestryCertified"]
                and proof["diagnosticSinkFailurePreservesPrimary"]
                and not plan["canAutoApply"] and not plan["runtimeVerified"]
                and len(plan["asyncMethods"])==3
                and all(m["targetBody"] is None for m in plan["asyncMethods"]),
                "Source-only witness unproven "+str(plan))
            proven[method]={"sourceMethodSha256":plan["sourceMethodSha256"],
                "fixtureSourceSha256":proof["fixtureSourceSha256"],
                "oneTimeSetUpSha256":proof["oneTimeSetUpSha256"],
                "setUpSha256":proof["setUpSha256"]}
        p37.check(file_hash=={f.name:hashlib.sha256(f.read_bytes()).hexdigest()
            for f in pom.iterdir()},"Roslyn changed immutable source")
        project=root/"tests";project.mkdir()
        targetOnly=p37.CSPROJ.replace(
            '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />',"")
        p37.check(targetOnly!=p37.CSPROJ and "OpenQA.Selenium" not in FIXTURE,
            "Unexpected source Selenium runtime dependency")
        (project/"Synthetic.Tests.csproj").write_text(targetOnly,encoding="utf-8")
        (project/"FaultEvidenceTests.cs").write_text(FIXTURE,encoding="utf-8")
        compiled=p37.run(["dotnet","build","-c","Release","--nologo"],
            project,timeout=360)
        p37.check(compiled["code"]==0,
            "Actual Playwright NUnit probe compile failed "+str(compiled))
        deps=project/"bin/Release/net10.0/Synthetic.Tests.deps.json"
        p37.check(deps.is_file() and "Selenium.WebDriver" not in
            deps.read_text(encoding="utf-8"),"Target .deps has Selenium")
        with ThreadingHTTPServer(("127.0.0.1",0),p37.Handler) as server:
            threading.Thread(target=server.serve_forever,daemon=True).start()
            try:
                outcomes={}
                for stage in STAGES:
                    outcomes[stage]=run_stage(stage,project,evidence,str(chrome),
                        "http://127.0.0.1:"+str(server.server_port)+"/")
            finally:server.shutdown()
        summary={
            "schema":"migrator-preview45-source-fault-witness-process-ancestry/v1",
            "sourceTreeSha256":source_sha,"sourceFileSha256":file_hash,
            "sourceBoundPlans":proven,
            "chromeVersion":os.environ.get("MIGRATOR_CHROME_VERSION"),
            "chromedriverVersion":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
            "chromeSha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
            "probeStages":outcomes,
            "realSinkWriteFailure":"BCL Console.Error.WriteLine on synthetic throwing TextWriter",
            "realCloseAndDisposeFailures":"observed_only_if_actual_API_threw",
            "syntheticPostCloseFault":"separately_labeled",
            "boundedOsAncestry":"NUnit dotnet test parent chain plus process start identity",
            "unobservedOrOrphanProcesses":"not_verified",
            "externalAssemblySinkMutation":"not_verified",
            "fullProductEquivalence":"not_verified",
            "privateArBilling":"not_run","canAutoApply":False
        }
        (evidence/"summary.json").write_text(
            json.dumps(summary,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        sums={f.name:hashlib.sha256(f.read_bytes()).hexdigest()
            for f in sorted(evidence.iterdir()) if f.is_file()
            and f.name!="SHA256SUMS.json"}
        (evidence/"SHA256SUMS.json").write_text(
            json.dumps(sums,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        print("POM_PREVIEW45_SOURCE_SINK_FAULT_PROCESS_ANCESTRY_REAL_CHROME_PASSED")
        print(json.dumps(summary,sort_keys=True))
if __name__=="__main__":main()
