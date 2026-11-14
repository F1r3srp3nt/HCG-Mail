#!/usr/bin/env python3
"""Verifies that a tool importing the whole HCG-Mail repository finds exactly one iOS application.

Two independent checks must agree:
  1. hcg-apps.json at the repository root names the iOS app, its project exists and is net10.0-ios / Exe.
  2. Scanning every *.csproj (what a build tool without the manifest would do): exactly one project is an
     executable whose target framework is iOS; libraries, tests and the desktop app are never candidates.
Every project in the repository must also be classified in hcg-apps.json, so a new project cannot silently
become a second candidate. Runs on any OS (Python 3, standard library only). Exit code 0 = OK.
"""
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parents[2]
manifest = json.loads((root / "hcg-apps.json").read_text(encoding="utf-8"))
errors = []

def prop(text, name):
    m = re.search(rf"<{name}>([^<]*)</{name}>", text)
    return m.group(1).strip() if m else ""

projects = sorted(p for p in root.rglob("*.csproj")
                  if not any(part in ("obj", "bin", "artifacts", ".git") for part in p.relative_to(root).parts))
rel = lambda p: p.relative_to(root).as_posix()

ios_apps = [a for a in manifest["applications"] if "ios" in a["platforms"]]
if len(ios_apps) != 1:
    errors.append(f"hcg-apps.json must list exactly one iOS application, found {len(ios_apps)}")
else:
    app = ios_apps[0]
    path = root / app["project"]
    if not path.is_file():
        errors.append(f"hcg-apps.json: iOS project {app['project']} does not exist")
    else:
        text = path.read_text(encoding="utf-8")
        if prop(text, "TargetFramework") != app["targetFramework"] or not app["targetFramework"].endswith("-ios"):
            errors.append(f"{app['project']}: TargetFramework is '{prop(text, 'TargetFramework')}', manifest says '{app['targetFramework']}'")
        if prop(text, "OutputType") != "Exe":
            errors.append(f"{app['project']}: OutputType must be Exe")
        if prop(text, "ApplicationId") == "" and "HcgBundleId" not in text:
            errors.append(f"{app['project']}: no ApplicationId (bundle identifier)")
    for key in ("solution", "toolchain", "check"):
        if not (root / app[key]).exists():
            errors.append(f"hcg-apps.json: {key} '{app[key]}' does not exist")
    if not (root / app["sharedRoot"] / "src").is_dir():
        errors.append(f"hcg-apps.json: sharedRoot '{app['sharedRoot']}/src' does not exist")

scanned = [rel(p) for p in projects
           if prop(p.read_text(encoding="utf-8"), "OutputType") in ("Exe", "WinExe")
           and "-ios" in prop(p.read_text(encoding="utf-8"), "TargetFramework")]
if ios_apps and scanned != [ios_apps[0]["project"]]:
    errors.append(f"scanning the repository finds these iOS executables: {scanned} (expected only {ios_apps[0]['project'] if ios_apps else '?'})")

classified = {a["project"] for a in manifest["applications"]} | set(manifest["libraries"]) | set(manifest["tests"])
for p in projects:
    if rel(p) not in classified:
        errors.append(f"{rel(p)} is not classified in hcg-apps.json (applications / libraries / tests)")
for c in classified:
    if not (root / c).is_file():
        errors.append(f"hcg-apps.json lists {c}, which does not exist")

if errors:
    print("App selection check FAILED:")
    for e in errors:
        print("  -", e)
    sys.exit(1)
print(f"App selection OK: the iOS application is '{ios_apps[0]['project']}' "
      f"({ios_apps[0]['targetFramework']}, bundle {ios_apps[0]['bundleId']}); {len(projects)} projects classified.")
