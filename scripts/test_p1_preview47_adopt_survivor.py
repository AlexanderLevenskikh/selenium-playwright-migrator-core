"""Negative and positive contracts; synthetic Win32 fake CANNOT prove Chromium fix."""
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import p1_preview47_adopt_survivor as adopt

FILETIME = '134398122442621909'


def fixture():
    survivor = {'pid': 5028, 'startIdentity': '11/22/2026 09:10:44',
                'kind': 'chrome', 'creationFileTime': FILETIME}
    path = [{'pid': 101, 'creationFileTime': '134398122400000000'},
            {'pid': 201, 'creationFileTime': '134398122410000000'},
            {'pid': 5028, 'creationFileTime': FILETIME}]
    pre = dict(survivor, inHostJob=False, sourceBackedBeforeFault=True,
               pathToWrapper=path, state='live')
    origin = {'observedPid': 5028, 'startIdentity': survivor['startIdentity'],
              'wasInBaselineWithOriginalIdentity': False,
              'creationFileTime': FILETIME,
              'pathToTestProcess': [{'pid': 5028, 'kind': 'chrome'},
                                    {'pid': 201, 'kind': 'driver'}]}
    final = {'pid': 5028, 'startIdentity': survivor['startIdentity'],
             'kind': 'chrome', 'executableMatchesConfiguredChrome': True,
             'presentWithOriginalIdentity': True,
             'chromiumRole': 'crashpad-handler',
             'native': {'state':'live','waitResult': 258,
                        'creationFileTime':FILETIME,
                        'expectedCreationFileTime':FILETIME}}
    f = {'status':'native_live_fail', 'stage':'disconnect_pass',
         'disconnect':True, 'originalPresenceGate':'failed',
         'prefaultMembership':'attested',
         'nativeLiveChrome':[survivor],
         'verifiedBeforeFault':[pre, {'pid':201,'startIdentity':'1', 'kind':'driver',
                                      'sourceBackedBeforeFault': True}]}
    o = {'originalPresenceGate':'failed', 'status':'failed', 'realDriverKilled': True,
         'failure':'P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED: ...',
         'hostOwnedBrowserCleanup':'not_attempted',
         'originalPresenceSurvivors': [{'pid':5028,'startIdentity':survivor['startIdentity']}],
         'observedOwnedAncestry':[origin], 'finalObservedProcesses':[final]}
    wrapper = {'pid':101, 'inHostJob':False,
               'creationFileTime':'134398122400000000'}
    return f,o,wrapper


class SourceContract(unittest.TestCase):
    def test_source_exact_positive(self):
        f,o,w=fixture()
        row=adopt.verify_original_survivors(f, None, w, o)[0]
        self.assertEqual(row['pid'],5028)
        self.assertEqual(row['chromiumRole'],'crashpad-handler')

    def test_dont_adopt_when_no_original_fail(self):
        f,o,w=fixture(); f['originalPresenceGate']='passed'
        with self.assertRaisesRegex(adopt.ProofBlocked,'P1I_ORIGINAL_GATE_NOT_FAILED'):
            adopt.verify_original_survivors(f,None,w,o)

    def test_unattested_prefault_blocks(self):
        f,o,w=fixture(); f['verifiedBeforeFault'][0]['sourceBackedBeforeFault']=False
        with self.assertRaisesRegex(adopt.ProofBlocked, 'P1I_PREFAULT_NATIVE_IDENTITY'):
            adopt.verify_original_survivors(f,None,w,o)

    def test_pid_reuse_filetime_blocks(self):
        f,o,w=fixture(); o['finalObservedProcesses'][0]['native']['creationFileTime']='999'
        with self.assertRaisesRegex(adopt.ProofBlocked,'P1I_POST_GATE_NATIVE_LIVE_IDENTITY'):
            adopt.verify_original_survivors(f,None,w,o)

    def test_process_from_baseline_blocks(self):
        f,o,w=fixture(); o['observedOwnedAncestry'][0]['wasInBaselineWithOriginalIdentity']=True
        with self.assertRaisesRegex(adopt.ProofBlocked,'P1I_ORIGINAL_P47_OWNERSHIP'):
            adopt.verify_original_survivors(f,None,w,o)

    def test_original_survivor_set_must_match(self):
        f,o,w=fixture(); o['originalPresenceSurvivors'].append({'pid':999,'startIdentity':'1'})
        with self.assertRaisesRegex(adopt.ProofBlocked,'P1I_ORIGINAL_SURVIVOR_SET_NOT_EXACT'):
            adopt.verify_original_survivors(f,None,w,o)

    def test_wrong_original_executable_blocks(self):
        f,o,w=fixture(); o['finalObservedProcesses'][0]['executableMatchesConfiguredChrome']=False
        with self.assertRaisesRegex(adopt.ProofBlocked,'P1I_POST_GATE_NATIVE_LIVE_IDENTITY'):
            adopt.verify_original_survivors(f,None,w,o)


class FakeNative:
    def __init__(self, api): self.api=api
    def inspect_handle(self, h, pid, filetime):
        return {'pid':pid,'state':'exited' if self.api.job_closed else 'live',
                'creationFileTime':FILETIME,
                'waitResult':0 if self.api.job_closed else 258}


class FakeAPI:
    def __init__(self, fail_assign=0):
        self.assigned=set();self.closed=[];self.job_closed=False;self.fail_assign=fail_assign
    def QueryInformationJobObject(self, handle, cl, pointer, size, _):
        import ctypes
        if cl==9:
            r=ctypes.cast(pointer,ctypes.POINTER(adopt.jobs.ExtendedLimits))
            r.contents.BasicLimitInformation.LimitFlags=adopt.jobs.KILL_ON_JOB_CLOSE
        elif cl==1:
            r=ctypes.cast(pointer,ctypes.POINTER(adopt.BasicAccounting))
            r.contents.ActiveProcesses=len(self.assigned)
        return True
    def OpenProcess(self, rights, _, pid):return pid
    def AssignProcessToJobObject(self, job, process):
        if self.fail_assign:return False
        self.assigned.add(process);return True
    def WaitForSingleObject(self, process, ms):return 0 if self.job_closed else 258
    def CloseHandle(self, handle):
        self.closed.append(handle)
        if handle==777:self.job_closed=True
        return True


class PostGateOnly(unittest.TestCase):
    def run_adoption(self, fail_assign=0):
        f,o,w=fixture();api=FakeAPI(fail_assign)
        with tempfile.TemporaryDirectory() as temp:
            src=Path(temp)/'original'; src.mkdir()
            (src/'process-disconnect_pass.json').write_text(json.dumps(o))
            real_sizeof=adopt.ctypes.sizeof
            with patch.object(adopt.jobs,'member',side_effect=lambda a,h,job: h in a.assigned if job is not None else False), \
                 patch.object(adopt.ctypes,'sizeof',side_effect=lambda obj: 48 if obj is adopt.BasicAccounting else real_sizeof(obj)), \
                 patch.object(adopt.ctypes,'get_last_error',return_value=87, create=True):
                proof=adopt.postgate_adopt(api,FakeNative(api), {'handle':777}, f,Path(temp),src,w)
            data=json.loads((Path(temp)/'postgate-adoption.json').read_text())
            return proof, data, api

    def test_fake_success_only_claims_os_mechanism(self):
        proof,data,api=self.run_adoption()
        self.assertEqual(proof['status'],'real_orphan_exited_after_postgate_job_close')
        self.assertTrue(proof['osMechanismProvedForAdoptedAOnly'])
        self.assertFalse(proof['fixClaim'])
        self.assertFalse(proof['productPrecreateBMitigationProven'])
        self.assertEqual(proof['nativeExitsAfterClose'][0]['creationFileTime'],FILETIME)
        self.assertEqual(api.closed.count(777),1)

    def test_windows_assign_rejection_must_never_claim_fix(self):
        proof,data,api=self.run_adoption(fail_assign=1)
        self.assertEqual(proof['status'],'blocked_or_unproven')
        self.assertFalse(proof['osMechanismProvedForAdoptedAOnly'])
        self.assertEqual(proof['adoption'][0]['win32Error'],87)
        self.assertFalse(api.job_closed)


if __name__ == '__main__':unittest.main()
