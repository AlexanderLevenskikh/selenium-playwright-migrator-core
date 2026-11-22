#!/usr/bin/env python3
"""Fail closed if the RC gate status is accidentally promoted without evidence."""
import json,sys
from pathlib import Path
def verify(data):
    if data.get("schema") != "migrator-rc-readiness/v1":
        raise ValueError("RC_GATE_SCHEMA_REQUIRED")
    p=data.get("gate",{})
    required={"p1Issue59":"open",
      "historicalNativeLiveOrphan":"confirmed_failed",
      "productCleanupFix":"not_proven",
      "directCrashpadClientWitness":"unknown",
      "realPublicProjectBehavioralEquivalence":"unknown",
      "localSyntheticOracle":"bounded_verified"}
    if any(p.get(k)!=v for k,v in required.items()):
        raise ValueError("RC_GO_NO_GO_FACTS_CHANGED_WITHOUT_ACCEPTANCE")
    if data.get("notRunAsPass") is not False or data.get("autoApplyAllowed") is not False:
        raise ValueError("RC_NOT_RUN_OR_AUTOAPPLY_WAS_PROMOTED")
    if any(p.get(k) is not False for k in
           ("releaseAllowed","mergeAuthorized","publishAuthorized")):
        raise ValueError("RC_RELEASE_GATE_BYPASSED")
    if data.get("integration",{}).get("coreFinalSha")=="pending" and p.get("releaseAllowed") is not False:
        raise ValueError("RC_INTEGRATION_SHA_NOT_PROVEN")
    return {"status":"expected_no_go_confirmed","releaseAllowed":False}
if __name__=="__main__":
    result=verify(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8")))
    print(json.dumps(result,sort_keys=True))
