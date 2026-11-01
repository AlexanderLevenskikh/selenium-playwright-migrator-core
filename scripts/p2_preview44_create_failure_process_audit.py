#!/usr/bin/env python3
"""Real Playwright.CreateAsync failure and bounded OS Chrome/driver PID evidence.
A missing PLAYWRIGHT_DRIVER_SEARCH_PATH exercises the actual .NET driver
resolution. Only observed exact Chrome/Playwright node PIDs are assessed;
the script never claims whole-system or unreturned-resource equivalence.
"""
import hashlib,json,os,re,subprocess,sys,tempfile,threading,time
from pathlib import Path
from http.server import ThreadingHTTPServer
import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview39_external_exception_real_chromium as p39
import p2_preview43_failure_atomic_real_chromium as p43

FIXTURE=r"""
using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Playwright;
namespace Fictional.P44;
public sealed class SyntheticOriginal:Exception {
    public SyntheticOriginal():base("P44_PRIMARY"){}
}
[TestFixture,NonParallelizable] public sealed class ActualDriverTests {
    [Test] public async Task Probe() {
        var stage=Environment.GetEnvironmentVariable("POM44_STAGE")!;
        IPlaywright? pw=null;
        IBrowser? browser=null;
        IBrowserContext? context=null;
        IPage? page=null;
        try {
            pw=await Microsoft.Playwright.Playwright.CreateAsync();
            browser=await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
                ExecutablePath=stage=="launch-real"
                    ?Path.Combine(Path.GetTempPath(),"p44-no-browser","missing")
                    :Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN"),
                Headless=true,Args=new[]{"--no-sandbox","--disable-dev-shm-usage"}
            });
            context=await browser.NewContextAsync();
            page=await context.NewPageAsync();
            if(!Object.ReferenceEquals(page.Context,context))
                throw new InvalidOperationException("WRONG_CONTEXT");
            await page.GotoAsync(Environment.GetEnvironmentVariable("POM44_URL")!);
            if(stage=="secondary")throw new SyntheticOriginal();
            var snapshot=await page.EvaluateAsync<string>(
                "() => JSON.stringify(window.snapshot())");
            Assert.That(snapshot,Does.Contain("ready"));
            await Task.Delay(4000); // make Chrome observable from the host
        } catch {
            try {
                if(page!=null) {
                    await page.CloseAsync();
                    if(stage=="secondary")throw new IOException("SECONDARY_PAGE");
                }
            } catch {
                try { System.Console.Error.WriteLine(
                    "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED"); } catch {}
            }
            try {
                if(context!=null) {
                    await context.CloseAsync();
                    if(stage=="secondary")throw new IOException("SECONDARY_CONTEXT");
                }
            } catch {
                try { System.Console.Error.WriteLine(
                    "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED"); } catch {}
            }
            try {
                if(browser!=null) {
                    await browser.CloseAsync();
                    if(stage=="secondary")throw new IOException("SECONDARY_BROWSER");
                }
            } catch {
                try { System.Console.Error.WriteLine(
                    "MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED"); } catch {}
            }
            try {
                if(pw!=null) {
                    pw.Dispose();
                    if(stage=="secondary")throw new IOException("SECONDARY_PLAYWRIGHT");
                }
            } catch {
                try { System.Console.Error.WriteLine(
                    "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED"); } catch {}
            }
            throw; // preserve original CLR identity, stack, and precedence
        }
        try { if(page!=null)await page.CloseAsync(); }
        finally {
            try { if(context!=null)await context.CloseAsync(); }
            finally {
                try { if(browser!=null)await browser.CloseAsync(); }
                finally { pw?.Dispose(); }
            }
        }
    }
}
"""
CODES=("MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
       "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED",
       "MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED",
       "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED")
STAGES=("healthy","create-real","launch-real","secondary")
def snapshot(chrome):
    found=set()
    if os.name=="nt":
        cmd=r"""
        $ErrorActionPreference='Stop'
        $all=@(Get-CimInstance Win32_Process)
        foreach($p in $all) {
          if($p.ExecutablePath -and $p.ExecutablePath -ieq $env:MIGRATOR_CHROME_BIN) {
            Write-Output "$($p.ProcessId)|chrome"
          } elseif($p.Name -ieq 'node.exe' -and $p.CommandLine -and
             $p.CommandLine -match 'playwright.*cli\.js') {
            Write-Output "$($p.ProcessId)|driver"
          }
        }
        """
        r=subprocess.run(["powershell","-NoProfile","-NonInteractive","-Command",cmd],
            capture_output=True,text=True,timeout=30)
        if r.returncode:raise RuntimeError("OS_PROCESS_INSPECTION_UNAVAILABLE")
        for line in r.stdout.splitlines():
            m=re.fullmatch(r"(\d+)\|(chrome|driver)",line.strip())
            if m:found.add((int(m.group(1)),m.group(2)))
        return found
    if not sys.platform.startswith("linux") or not Path("/proc").exists():
        raise RuntimeError("OS_PROCESS_INSPECTION_UNSUPPORTED")
    expected=os.path.realpath(chrome)
    for p in Path("/proc").iterdir():
        if not p.name.isdecimal():continue
        try:
            path=os.path.realpath(os.readlink(p/"exe"))
            if path==expected:found.add((int(p.name),"chrome"));continue
            command=(p/"cmdline").read_bytes().replace(b"\0",b" ").lower()
            if b".playwright" in command and b"cli.js" in command:
                found.add((int(p.name),"driver"))
        except (FileNotFoundError,PermissionError,ProcessLookupError,OSError):
            continue
    return found

def canonical_source(repo):
    page,base,fixture=p43.atomic_source(repo)
    for code in ("MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED",
                 "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED",
                 "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
                 "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED"):
        pos=fixture.find("catch {}")
        p37.check(pos>=0,"Canonical source missing guarded catch")
        changed='catch { try { System.Console.Error.WriteLine("'+code+'"); } catch {} }'
        fixture=fixture[:pos]+changed+fixture[pos+len("catch {}"):]
        # Skip nested diagnostic sink catch when finding the next outer catch.
        nextpos=pos+len(changed)
        fixture=fixture[:nextpos]+fixture[nextpos:]
        # Move insertion boundary by searching after already inserted codes.
        # Before next iteration, protect previous inner catch from replacement.
        fixture=fixture[:pos]+fixture[pos:].replace(
            'catch {} }','catch {} }',1)
    return page,base,fixture

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    p37.check(chrome.is_file() and driver.is_file(),"Chrome and Driver required")
    cli=repo/"Migrator.AssemblyCli/bin/Release/net10.0/Migrator.AssemblyCli.dll"
    p37.check(cli.is_file(),"Compiled Core CLI missing")
    evidence=repo/"artifacts/pom-preview44";evidence.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="fictional-p44-") as tmp:
        root=Path(tmp);pom=root/"pom";pom.mkdir()
        # Match the canonical source-only Roslyn fixture, exactly.
        page,base,fixture=p43.atomic_source(repo)
        cursor=0
        for code in ("MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED",
                     "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED",
                     "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
                     "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED"):
            pos=fixture.find("catch {}",cursor)
            p37.check(pos>=0,"No source cleanup catch "+code)
            block='catch { try { System.Console.Error.WriteLine("'+code+'"); } catch {} }'
            fixture=fixture[:pos]+block+fixture[pos+len("catch {}"):]
            cursor=pos+len(block)
        for name,value in (("Page.cs",page),("TestBase.cs",base),
                           ("NUnitFixture.cs",fixture)):
            (pom/name).write_bytes(value.encode("utf-8"))
        files={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in pom.iterdir()}
        sha=p39.cli_json(["dotnet",cli,"pom-inspect",pom],repo,0)["sourceTreeHash"]
        plans={}
        for method in ("Missing","Timeout"):
            args=["dotnet",cli,"pom-safe-cleanup-diagnostics",pom,
                "Fictional.P42.SourcePage",method,sha,
                "Page","Context","Browser","PW"]
            x=p39.cli_json(args,repo,0)
            proof=x["fixtureLifecycle"]
            p37.check(x["status"]=="requires_review"
                and x["schemaVersion"]=="migrator-pom-external-safe-diagnostics-count/v1"
                and proof["safeSecondaryDiagnosticCodes"]
                and proof["diagnosticSinkFailurePreservesPrimary"]
                and proof["primaryExceptionRethrown"]
                and proof["diagnosticSurface"]=="fixed_code_stderr_only"
                and len(x["asyncMethods"])==3
                and all(z["targetBody"] is None for z in x["asyncMethods"])
                and not x["canAutoApply"] and not x["runtimeVerified"],
                "Source-backed protected fixed logger unverified "+method+" "+str(x))
            plans[method]={"sourceSHA":x["sourceMethodSha256"],
                "fixtureSHA":proof["fixtureSourceSha256"],
                "oneSetSHA":proof["oneTimeSetUpSha256"],
                "setUpSHA":proof["setUpSha256"]}
        p37.check({p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in pom.iterdir()}==files,"Read-only Core wrote sources")
        project=root/"tests";project.mkdir()
        csproj=p37.CSPROJ.replace(
            '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />',"")
        p37.check(csproj!=p37.CSPROJ and "OpenQA.Selenium" not in FIXTURE,
            "Playwright-only fixture includes Selenium")
        (project/"Synthetic.Tests.csproj").write_text(csproj,encoding="utf-8")
        (project/"ActualDriverTests.cs").write_text(FIXTURE,encoding="utf-8")
        compiled=p37.run(["dotnet","build","-c","Release","--nologo"],
            project,timeout=360)
        p37.check(compiled["code"]==0,"NUnit fixture compile failed "+str(compiled))
        deps=project/"bin/Release/net10.0/Synthetic.Tests.deps.json"
        p37.check(deps.is_file() and "Selenium.WebDriver" not in
            deps.read_text(encoding="utf-8"),"Playwright-only deps include Selenium")
        outcomes={}
        with ThreadingHTTPServer(("127.0.0.1",0),p37.Handler) as server:
            threading.Thread(target=server.serve_forever,daemon=True).start()
            try:
                for stage in STAGES:
                    baseline=snapshot(str(chrome))
                    env=dict(os.environ,POM44_STAGE=stage,
                        POM44_URL="http://127.0.0.1:"+str(server.server_port)+"/")
                    env.pop("PLAYWRIGHT_DRIVER_SEARCH_PATH",None)
                    env.pop("PLAYWRIGHT_NODEJS_PATH",None)
                    driverMissing=project/"p44-missing-driver"
                    p37.check(not driverMissing.exists(),"Invalid driver path exists")
                    if stage=="create-real":
                        env["PLAYWRIGHT_DRIVER_SEARCH_PATH"]=str(driverMissing)
                    cmd=["dotnet","test","-c","Release","--no-build",
                        "--no-restore","--nologo",
                        "--logger","trx;LogFileName="+stage+".trx",
                        "--results-directory",str(evidence),
                        "--filter","FullyQualifiedName~ActualDriverTests&Name~Probe"]
                    begin=time.monotonic()
                    proc=subprocess.Popen(cmd,cwd=project,env=env,
                        stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                        text=True,encoding="utf-8",errors="replace")
                    seen=set()
                    while proc.poll() is None:
                        seen |= snapshot(str(chrome))-baseline
                        p37.check(time.monotonic()-begin<180,
                            "NUnit fault stage exceeded 180 sec "+stage)
                        time.sleep(.3 if os.name!="nt" else .5)
                    out,err=proc.communicate(timeout=30)
                    seen |= snapshot(str(chrome))-baseline
                    remaining=set()
                    deadline=time.monotonic()+12
                    while True:
                        remaining=seen & (snapshot(str(chrome))-baseline)
                        if not remaining or time.monotonic()>=deadline:break
                        time.sleep(.5)
                    trx=evidence/(stage+".trx")
                    p37.check(trx.is_file(),
                        "Missing actual NUnit TRX "+stage+" "+out[-1000:])
                    statuses=p37.read_trx(trx)
                    expected="Passed" if stage=="healthy" else "Failed"
                    p37.check(statuses=={"Probe":expected}
                        and (proc.returncode==0)==(stage=="healthy"),
                        "Wrong NUnit failure parity "+stage+" "+str(statuses)+
                        " "+out[-1200:]+err[-700:])
                    trxText=trx.read_text(encoding="utf-8")
                    if stage=="create-real":
                        p37.check("Microsoft.Playwright.PlaywrightException" in trxText,
                            "Actual Playwright.CreateAsync driver-resolution exception absent")
                    elif stage=="launch-real":
                        p37.check("Microsoft.Playwright.PlaywrightException" in trxText,
                            "Actual Playwright LaunchAsync exception absent")
                    elif stage=="secondary":
                        p37.check("Fictional.P44.SyntheticOriginal" in trxText,
                            "Secondary error replaced original NUnit exception")
                        combined=out+err+trxText
                        p37.check(all(code in combined for code in CODES),
                            "Fixed diagnostic codes absent from captured NUnit output")
                    if stage=="healthy":
                        p37.check(any(k=="chrome" for _,k in seen),
                            "Live real Chrome executable PID not observed")
                    p37.check(not remaining,"Observed Chrome/driver PID leak "+
                        stage+" "+str(sorted(remaining)))
                    outcomes[stage]={
                        "outcome":expected,"actualPlaywrightApiFailure":
                            stage in ("create-real","launch-real"),
                        "syntheticCleanupError":stage=="secondary",
                        "sourceOnlySafeDiagnosticCodes":list(CODES)
                            if stage=="secondary" else [],
                        "seenChromeCount":sum(1 for _,k in seen if k=="chrome"),
                        "seenNodeDriverCount":sum(1 for _,k in seen if k=="driver"),
                        "survivingObservedPids":sorted(p for p,_ in remaining),
                        "inspection":"exact_executable_path_plus_driver_commandline",
                        "durationSec":round(time.monotonic()-begin,2)
                    }
            finally:server.shutdown()
        result={
            "schema":"p44-source-proof-real-create-failure-os-process/v1",
            "sourceTreeSha256":sha,"sourceFilesSha256":files,"plans":plans,
            "chromiumVersion":os.environ.get("MIGRATOR_CHROME_VERSION"),
            "chromedriverVersion":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
            "chromeSha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
            "outcomes":outcomes,
            "realCreateAsyncFailure":"driver_resolution_override_not_synthetic",
            "processAuditScope":"observed_exact_Chrome_and_Playwright_node_PIDs",
            "unobservedOrUnreturnedProcesses":"not_verified",
            "arbitraryProjectEquivalence":"not_verified","canAutoApply":False,
            "privateArBilling":"not_run"
        }
        (evidence/"summary.json").write_text(
            json.dumps(result,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        sums={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in evidence.iterdir()
            if p.is_file() and p.name!="SHA256SUMS.json"}
        (evidence/"SHA256SUMS.json").write_text(
            json.dumps(sums,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        print("POM_PREVIEW44_REAL_CREATE_ASYNC_OS_PROCESS_AUDIT_PASSED")
        print(json.dumps(result,sort_keys=True))
if __name__=="__main__":main()
