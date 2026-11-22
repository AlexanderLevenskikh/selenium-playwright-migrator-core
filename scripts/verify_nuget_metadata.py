#!/usr/bin/env python3
"""Fail closed on shipped Core NuGet .nuspec provenance (not build logs).

This checks actual ZIP bytes; the caller must supply the *source checkout*
commit identity, not the PR's synthetic merge SHA. No network or execution.
"""
import argparse
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile

VERSION = "0.1.0-preview.48"
REPO = "https://github.com/levencode-labs/selenium-playwright-migrator-core"
PACKAGES = {
    "Migrator.Core": (
        "Migrator.Core.dll",
        "Source and target verification contracts for controlled Selenium C# to Playwright .NET migration.",
    ),
    "Migrator.Roslyn": (
        "Migrator.Roslyn.dll",
        "Roslyn analysis and review-only planning for Selenium C# to Playwright .NET migration.",
    ),
    "Migrator.SeleniumCSharp": (
        "Migrator.SeleniumCSharp.dll",
        "Selenium C# source adapter primitives for the .NET migration engine.",
    ),
    "Migrator.PlaywrightDotNet": (
        "Migrator.PlaywrightDotNet.dll",
        "Playwright .NET target adapter primitives for the .NET migration engine.",
    ),
    "SeleniumPlaywrightMigrator.Application": (
        "Migrator.Application.dll",
        "Read-only application API for AST-based Selenium C# to Playwright .NET migration.",
    ),
}


def must(ok, message):
    if not ok:
        raise ValueError("NUGET_PROVENANCE_REJECTED: " + message)


def validate_package(path, package_id, expected_sha):
    must(re.fullmatch(r"[0-9a-f]{40}", expected_sha) is not None,
         "expected commit must be exact lowercase SHA40")
    dll, description = PACKAGES[package_id]
    must(path.is_file() and not path.is_symlink(), "missing or symlinked NuGet")
    with zipfile.ZipFile(path) as archive:
        must(archive.testzip() is None, "package CRC failed")
        names = archive.namelist()
        must(len(names) == len(set(names)), "duplicate package entries")
        must(not any(n.startswith("/") or ".." in n.split("/")
                     or "\\" in n for n in names),
             "unsafe package path")
        nuspecs = [n for n in names if n.endswith(".nuspec")]
        must(nuspecs == [package_id + ".nuspec"],
             "exact matching nuspec required")
        must("lib/net10.0/" + dll in names, "net10 target DLL missing")
        must(archive.getinfo(nuspecs[0]).file_size < 100_000,
             "nuspec too large")
        root = ET.fromstring(archive.read(nuspecs[0]))
        metadata = root.find("{*}metadata")
        must(metadata is not None, "metadata missing")

        def value(tag):
            node = metadata.find("{*}" + tag)
            return "" if node is None or node.text is None else node.text.strip()

        must(value("id") == package_id, "package id mismatch")
        must(value("version") == VERSION, "version mismatch")
        must(value("description") == description,
             "missing, generic or incorrect package description")
        license_node = metadata.find("{*}license")
        must(license_node is not None
             and license_node.attrib.get("type") == "expression"
             and (license_node.text or "").strip() == "MIT",
             "SPDX MIT license metadata missing")
        project_url = value("projectUrl")
        must(project_url == REPO, "canonical project URL missing")
        repository = metadata.find("{*}repository")
        must(repository is not None
             and repository.attrib.get("type") == "git"
             and repository.attrib.get("url") == REPO
             and repository.attrib.get("commit") == expected_sha,
             "repository URL/commit must equal source exact SHA")
        for dep in metadata.findall(".//{*}dependency"):
            identifier, version = dep.attrib.get("id", ""), dep.attrib.get("version", "")
            if identifier in PACKAGES:
                must(version == VERSION, "internal dependency version mismatch")
        return package_id


def validate_feed(folder, expected_sha):
    folder = Path(folder)
    must(folder.is_dir() and not folder.is_symlink(),
         "local package directory missing or symlinked")
    names = set()
    paths = sorted(folder.glob("*.nupkg"))
    must(len(paths) == len(PACKAGES), "exactly five Core packages required")
    for path in paths:
        pkg = next((p for p in PACKAGES if path.name ==
                    p + "." + VERSION + ".nupkg"), None)
        must(pkg is not None and pkg not in names,
             "package filename, version or duplicate mismatch")
        validate_package(path, pkg, expected_sha)
        names.add(pkg)
    must(names == set(PACKAGES), "missing Core project packages")
    return {"status": "exact_nuget_provenance_verified",
            "repository": REPO, "commit": expected_sha,
            "version": VERSION, "packages": sorted(names)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("feed", type=Path)
    parser.add_argument("--expected-commit", required=True)
    args = parser.parse_args()
    import json
    print(json.dumps(validate_feed(args.feed, args.expected_commit),
                     sort_keys=True))
