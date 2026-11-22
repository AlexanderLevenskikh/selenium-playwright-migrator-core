"""Negative controls: a green Core CI must NOT silently allow release."""
import json,unittest
import check_rc_gate_contract as rc
class RCStatusControls(unittest.TestCase):
    def data(self):
        return {"schema":"migrator-rc-readiness/v1",
          "integration":{"coreFinalSha":"pending"},
          "gate":{"p1Issue59":"open",
            "historicalNativeLiveOrphan":"confirmed_failed",
            "productCleanupFix":"not_proven",
            "directCrashpadClientWitness":"unknown",
            "realPublicProjectBehavioralEquivalence":"unknown",
            "localSyntheticOracle":"bounded_verified",
            "releaseAllowed":False,"mergeAuthorized":False,"publishAuthorized":False},
          "notRunAsPass":False,"autoApplyAllowed":False}
    def test_known_hard_blocker_keeps_no_go(self):
        self.assertFalse(rc.verify(self.data())["releaseAllowed"])
    def test_green_ci_cannot_switch_to_release(self):
        x=self.data();x["gate"]["releaseAllowed"]=True
        with self.assertRaisesRegex(ValueError,"RC_RELEASE_GATE_BYPASSED"):rc.verify(x)
    def test_unknown_is_not_pass(self):
        x=self.data();x["gate"]["directCrashpadClientWitness"]="passed"
        with self.assertRaisesRegex(ValueError,"FACTS_CHANGED"):rc.verify(x)
    def test_missing_failure_is_not_clean(self):
        x=self.data();x["gate"]["historicalNativeLiveOrphan"]="no_failure"
        with self.assertRaisesRegex(ValueError,"FACTS_CHANGED"):rc.verify(x)
if __name__=="__main__":unittest.main()
