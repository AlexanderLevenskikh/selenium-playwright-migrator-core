"""P1-O synthetic OS ancestry/PID FILETIME controls, NO process invocation."""
import os
import unittest
from unittest import mock

import p1o_win32_original_p47_one_shot as w


def proc_table():
    return {
        111: (0, "python-created", "other"),  # exact P47 wrapper
        222: (111, "dotnet-created", "other"), # original NUnit child
        333: (222, "chrome-cim-created", "chrome"),
        444: (0, "unrelated-created", "chrome"), # same chrome.exe: NOT owned
    }


class OwnedAncestry(unittest.TestCase):
    def test_only_real_ancestry_and_native_creation_identity(self):
        tracked = set()
        pid_created = {
            333: ("134398240007207002", True),
            444: ("134398240009020979", True),
        }
        table = proc_table()
        with mock.patch.object(w.existing_observer, "snapshot",
                               side_effect=lambda _chrome: table), \
             mock.patch.object(w, "native_identity",
                               side_effect=lambda pid: pid_created.get(pid)):
            original = os.environ.get("MIGRATOR_CHROME_BIN")
            observed = w.poll_owned_chrome("C:/owned/chrome.exe", 111, tracked)
            self.assertEqual({x["pid"] for x in observed}, {333})
            self.assertEqual(tracked, {(333, "134398240007207002")})
            self.assertTrue(observed[0]["observedOriginalAncestry"])
            self.assertEqual(os.environ.get("MIGRATOR_CHROME_BIN"), original)

            # The original parent exited; the precise prior native binding
            # is still valid, while an unrelated same-image PID never is.
            table = {
                333: (4, "orphaned-cim-parent", "chrome"),
                444: (4, "unrelated-created", "chrome"),
            }
            orphan = w.poll_owned_chrome("C:/owned/chrome.exe", 111, tracked)
            self.assertEqual({x["pid"] for x in orphan}, {333})

            # Recycled PID + different creation FILETIME is NOT same process.
            pid_created[333] = ("134398240007207003", True)
            reused = w.poll_owned_chrome("C:/owned/chrome.exe", 111, tracked)
            self.assertEqual(reused, [])

    def test_missing_native_handle_cannot_prove_live(self):
        tracked = set()
        table = proc_table()
        with mock.patch.object(w.existing_observer, "snapshot",
                               return_value=table), \
             mock.patch.object(w, "native_identity", return_value=None):
            self.assertEqual(
                w.poll_owned_chrome("C:/owned/chrome.exe", 111, tracked), [])
        self.assertEqual(tracked, set())

    def test_original_stdout_chrome_path_does_not_authorize_arbitrary_pid(self):
        table = {111: (0, "python", "other"),
                 444: (0, "same-exe-but-foreign", "chrome")}
        with mock.patch.object(w.existing_observer, "snapshot",
                               return_value=table), \
             mock.patch.object(w, "native_identity",
                               return_value=("134398240009020979", True)) as native:
            self.assertEqual(
                w.poll_owned_chrome("C:/owned/chrome.exe", 111, set()), [])
            native.assert_not_called()  # No handle to an unrelated PID.


if __name__ == "__main__":
    unittest.main()
