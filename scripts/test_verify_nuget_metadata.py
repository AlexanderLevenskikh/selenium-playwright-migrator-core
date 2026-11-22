"""Negative controls for actual .nuspec provenance; no dotnet, network or binaries."""
import io
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile
import verify_nuget_metadata as v

SHA = "a" * 40


def package_bytes(name="Migrator.Core", description=None, repo=v.REPO,
                  commit=SHA, version=v.VERSION, license_="MIT",
                  project_url=v.REPO, internal_dep_version=v.VERSION):
    dll, default_description = v.PACKAGES[name]
    description = default_description if description is None else description
    xml = f"""<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
<metadata><id>{name}</id><version>{version}</version>
<description>{description}</description>
<license type="expression">{license_}</license>
<projectUrl>{project_url}</projectUrl>
<repository type="git" url="{repo}" commit="{commit}"/>
<dependencies><group targetFramework="net10.0">
<dependency id="Migrator.Roslyn" version="{internal_dep_version}"/>
</group></dependencies>
</metadata></package>"""
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as z:
        z.writestr(name + ".nuspec", xml)
        z.writestr("lib/net10.0/" + dll, b"compiled-test-placeholder")
    return stream.getvalue()


class PackageProvenance(unittest.TestCase):
    def check(self, opts=None, error=None):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "Migrator.Core.0.1.0-preview.48.nupkg"
            p.write_bytes(package_bytes(**(opts or {})))
            if error:
                with self.assertRaisesRegex(ValueError, error):
                    v.validate_package(p, "Migrator.Core", SHA)
            else:
                self.assertEqual(v.validate_package(p, "Migrator.Core", SHA),
                                 "Migrator.Core")

    def test_positive(self):
        self.check()

    def test_old_owner_url_blocked(self):
        self.check({"repo": "https://github.com/AlexanderLevenskikh/selenium-playwright-migrator-core"}, "repository URL/commit")

    def test_pr_merge_commit_cannot_impersonate_source_sha(self):
        self.check({"commit": "8c805d0c79303dc0e508fbb35f778bac3e63b06f"}, "repository URL/commit")

    def test_placeholder_description_rejected(self):
        self.check({"description": "Package Description"}, "description")

    def test_wrong_project_url_rejected(self):
        self.check({"project_url": "https://github.com/old/repo"}, "project URL")

    def test_wrong_license_rejected(self):
        self.check({"license_": "Apache-2.0"}, "SPDX MIT")

    def test_wrong_version_rejected(self):
        self.check({"version": "0.2.0"}, "version")

    def test_internal_dependency_version_rejected(self):
        self.check({"internal_dep_version": "0.9.0"}, "dependency version")

    def test_invalid_expected_commit_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "pkg.nupkg"
            p.write_bytes(package_bytes())
            with self.assertRaisesRegex(ValueError, "exact lowercase SHA40"):
                v.validate_package(p, "Migrator.Core", "not-a-sha")

    def test_missing_package_feed_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(ValueError, "exactly five"):
                v.validate_feed(tmp, SHA)


if __name__ == "__main__":
    unittest.main()
