"""Portable public source identity controls; no internet or user-code execution."""
import hashlib
import tempfile
import unittest
from pathlib import Path

import p2_p3_public_portfolio as portfolio


class CanonicalGitSourceControls(unittest.TestCase):
    def test_lf_crlf_gives_identical_canonical_git_content(self):
        canonical = b"\xef\xbb\xbfclass Page {\n  void Click() {}\n}\n"
        checkout = canonical.replace(b"\n", b"\r\n")
        self.assertTrue(portfolio.only_checkout_eol_difference(canonical, checkout))
        self.assertNotEqual(hashlib.sha256(canonical).hexdigest(),
                            hashlib.sha256(checkout).hexdigest())
        self.assertEqual(hashlib.sha256(canonical).hexdigest(),
                         hashlib.sha256(checkout.replace(b"\r\n", b"\n")).hexdigest())

    def test_changed_action_never_hides_behind_line_endings(self):
        canonical = b"class Page { void Click() {} }\n"
        changed = b"class Page { void Clear() {} }\r\n"
        self.assertFalse(portfolio.only_checkout_eol_difference(canonical, changed))

    def test_changed_selector_or_argument_is_not_eol_normalization(self):
        canonical = b'var e = driver.FindElement(By.Id("save"));\n'
        changed = b'var e = driver.FindElement(By.Id("delete"));\r\n'
        self.assertFalse(portfolio.only_checkout_eol_difference(canonical, changed))

    def test_empty_and_binary_content_not_implicitly_rewritten(self):
        self.assertTrue(portfolio.only_checkout_eol_difference(b"", b""))
        self.assertFalse(portfolio.only_checkout_eol_difference(
            b"\x00\x01\n", b"\x00\x02\r\n"))


if __name__ == "__main__":
    unittest.main()
