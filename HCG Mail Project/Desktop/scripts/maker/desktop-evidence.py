#!/usr/bin/env python3
"""Release manifest and HCG Release Passports for HCG Mail for macOS and Linux, from recorded CI evidence only.

Inputs (workflow desktop-prerelease.yml): each package job's build-evidence-<os>.json, the files themselves, each runtime
job's runtime-summary.json and install.txt, and HCG Repo's release-passport-v1 schema.

Outputs, in --out:
  release-manifest.json      version, platform, architecture, build number, SHA-256, size, signing status, validation
                             summary and known limitations for every file.
  passports/<file>.json      one hcg.release-passport v1 per file, validated against the schema.
  SHA256SUMS.txt, SUMMARY.md
Nothing is invented: a value without evidence is "not-run" or "not-recorded".
"""
import argparse, glob, hashlib, json, os, sys

ap = argparse.ArgumentParser()
ap.add_argument("--dist", required=True)
ap.add_argument("--evidence", required=True, help="folder with build-evidence-<os>.json")
ap.add_argument("--runtime", required=True, help="folder with hcg-mail-runtime-<rid>/ sub-folders")
ap.add_argument("--schema", required=True)
ap.add_argument("--run-url", required=True)
ap.add_argument("--out", required=True)
a = ap.parse_args()

PLATFORM = {"osx": ("macos", "macOS"), "linux": ("linux", "Linux")}
# What is established, not assumed: the .NET 10 support matrix decides the oldest version; the tested version is in the
# runtime evidence.
MIN_OS = {"osx-arm64": "macOS versions supported by .NET 10, Apple silicon",
          "osx-x64": "macOS versions supported by .NET 10, Intel",
          "linux-x64": "64-bit Linux distributions supported by .NET 10, with X11 or XWayland",
          "linux-arm64": "64-bit ARM Linux distributions supported by .NET 10, with X11 or XWayland"}
LIMITATIONS = {
    "macos": ["Not signed with an Apple Developer ID and not notarized: macOS Gatekeeper blocks the first start. Allow it in "
              "System Settings › Privacy & Security › Open Anyway only if the SHA-256 matches.",
              "The window needs a display at least 1040 points wide.",
              "Passwords are kept in the macOS Keychain.",
              "IMAP and SMTP only; Microsoft and Google sign-in are not part of the Public Beta."],
    "linux": ["Not code-signed (Linux executables are not signed); verify the SHA-256.",
              "No secure password store integration yet: passwords are kept only while HCG Mail runs and must be entered again after a restart.",
              "Portable ZIP only (no AppImage, .deb or .rpm yet); needs an X11 or XWayland desktop.",
              "IMAP and SMTP only; Microsoft and Google sign-in are not part of the Public Beta."],
}

def sha(path, algo):
    h = hashlib.new(algo)
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

evidence = {}
for path in glob.glob(os.path.join(a.evidence, "**", "build-evidence-*.json"), recursive=True):
    ev = json.load(open(path, encoding="utf-8-sig"))
    for f in ev["files"]:
        evidence[f["fileName"]] = (ev, f)
if not evidence:
    sys.exit("No build evidence lists any file: refusing to write an empty manifest")

def runtime_of(rid):
    d = next(iter(glob.glob(os.path.join(a.runtime, f"*{rid}"))), None)
    if d is None:
        return None, ""
    summary = os.path.join(d, "runtime-summary.json")
    install = os.path.join(d, "install.txt")
    return (json.load(open(summary)) if os.path.exists(summary) else None,
            open(install, errors="replace").read() if os.path.exists(install) else "")

def passed(checks, needle):
    found = [c for c in checks if needle in c["check"]]
    return ("passed" if all(c["passed"] for c in found) else "failed") if found else "not-run"

artifacts, passports, rows = [], [], []
for name in sorted(evidence):
    ev, f = evidence[name]
    path = os.path.join(a.dist, name)
    assert os.path.getsize(path) == f["sizeBytes"] and sha(path, "sha256") == f["sha256"], f"{name}: bytes differ from the build evidence"
    rid = next(r for r in MIN_OS if f"-{r}." in name)
    plat_key, plat_name = PLATFORM[rid.split("-")[0]]
    arch = rid.split("-")[1]
    summary, install = runtime_of(rid)
    checks = summary["checks"] if summary else []
    install_state = "passed" if "INSTALL OK" in install else ("not-run" if not install else "failed")
    if plat_key == "macos" and install_state == "passed" and "SEAL OK" not in install:
        install_state = "failed"  # a broken bundle seal makes macOS call the downloaded app damaged
    launch = passed(checks, "the app started")
    restart = passed(checks, "Restart: the app started")
    cred = passed(checks, "Credential store")
    tls = passed(checks, "TLS ")
    runtime_state = "partial" if (cred == "passed" and tls == "passed") else ("failed" if "failed" in (cred, tls) else "not-run")
    signature = summary["executable"]["codeSignature"] if summary else "not recorded"
    if plat_key == "macos":
        signing = {"status": "unsigned" if "Developer ID" not in signature else "signed",
                   "evidence": f"codesign: {signature}; bundle seal: {'verified (codesign --verify --deep --strict)' if 'SEAL OK' in install else 'NOT verified'}. Ad-hoc only: no Developer ID signature and no notarization."}
    else:
        signing = {"status": "unsigned", "evidence": "Linux executables are not code-signed."}
    os_caption = summary["os"]["caption"] if summary else "not run"
    validation = (f"Built by HCG Executable Maker ({ev['build']['toolchain']}). Install: {install_state}. "
                  f"Runtime on {os_caption} ({summary['os']['architecture'] if summary else '-'}): "
                  + (f"{sum(c['passed'] for c in checks)}/{len(checks)} checks passed (credential store, TLS accept/refuse, start, screen capture, quit, restart). " if summary else "not run. ")
                  + "Authenticated IMAP/SMTP with a real provider: not run on this platform (see the qualification report).")
    artifacts.append({
        "fileName": name, "version": ev["version"], "buildNumber": ev["buildNumber"], "platform": plat_key, "architecture": arch,
        "sizeBytes": f["sizeBytes"], "sha256": f["sha256"], "signing": signing, "minimumOs": MIN_OS[rid],
        "sourceCommit": ev["source"]["commit"], "validation": {"install": install_state, "launch": launch, "restart": restart,
        "credentialStore": cred, "tls": tls, "summary": validation}, "knownLimitations": LIMITATIONS[plat_key],
    })
    passport = {
        "format": "hcg.release-passport", "formatVersion": 1,
        "product": {"key": "hcg-mail", "name": "HCG Mail", "store_slug": "hcg-mail", "central_id": "hcg.mail"},
        "edition": f"{plat_key}-desktop", "platform": f"{plat_key}-desktop", "architecture": arch,
        "package": "com.hakmicapitalgroup.hcgmail", "version": ev["version"],
        "source": {"repository": ev["source"]["repository"], "commit": ev["source"]["commit"], "branch": ev["source"]["branch"]},
        "build": {"provider": "github-actions", "toolchain": ev["build"]["toolchain"], "built_at": None,
                  "run_id": int(ev["build"]["runId"]), "artifact": f"hcg-mail-{plat_key}-{ev['version']}"},
        "artifact": {"filename": name, "package": "com.hakmicapitalgroup.hcgmail", "version": ev["version"], "architecture": arch,
                     "sha256": f["sha256"], "sha512": sha(path, "sha512"), "size": f["sizeBytes"]},
        "validation": {"levels": {
            "static_inspection": {"state": "passed", "detail": "HCG Executable Maker bundle/archive checks during packaging"},
            "maintainer_script_review": {"state": "not-applicable", "detail": "no maintainer scripts"},
            "script_free_lifecycle": {"state": "not-applicable", "detail": "Debian lifecycle level"},
            "device_installation": {"state": install_state, "detail": "DMG verified, mounted and the .app copied out on a GitHub-hosted Mac" if plat_key == "macos" else "ZIP extracted on a GitHub-hosted Linux runner"},
            "application_launch": {"state": launch, "detail": f"started, captured on screen and quit on {os_caption}; restart {restart}"},
            "runtime_verification": {"state": runtime_state, "detail": f"credential store {cred}, TLS accept/refuse {tls}; authenticated IMAP/SMTP not exercised on this platform"},
        }},
        "device_evidence": [],
        "compatibility_status": f"Launch verified on a GitHub-hosted {plat_name} {arch} runner ({os_caption}); not on a user's computer yet",
        "runtime_compatibility": runtime_state if runtime_state in ("partial", "failed") else "not-established",
        "approvals": [],
        "distribution": {"channel": "candidate", "access": "public"},
        "publication": {"record_status": "protected", "in_index_builds": False},
        "repository_signing": {"scope": "not applicable: distributed through HCG Store, not HCG Repo", "configured": False,
                               "authenticated": False, "status": "not-applicable"},
        "code_signing": {"scope": "macOS .app bundle (codesign)" if plat_key == "macos" else "Linux ELF executable", "method_claimed": None,
                         "binaries": 1, "all_signed": False, "entitlements": {}},
        "notes": ["Public testing prerelease (HCG Mail 1.0 Public Beta); distribution.channel 'candidate' stands for Store channel 'beta'. "
                  "publication.record_status 'protected' means not published.", f"Evidence: {a.run_url}"],
    }
    passports.append(passport)
    rows.append((name, f["sizeBytes"], f["sha256"], signing["status"], install_state, launch, runtime_state))

import jsonschema
schema = json.load(open(a.schema, encoding="utf-8"))
for p in passports:
    jsonschema.validate(p, schema)

os.makedirs(os.path.join(a.out, "passports"), exist_ok=True)
json.dump({"release": "HCG Mail 1.0 Public Beta", "evidence": a.run_url, "artifacts": artifacts},
          open(os.path.join(a.out, "release-manifest.json"), "w"), indent=2)
for p in passports:
    json.dump(p, open(os.path.join(a.out, "passports", p["artifact"]["filename"] + ".passport.json"), "w"), indent=2)
with open(os.path.join(a.out, "SHA256SUMS.txt"), "w") as s:
    for r in rows:
        s.write(f"{r[2]}  {r[0]}\n")
with open(os.path.join(a.out, "SUMMARY.md"), "w") as s:
    s.write(f"# HCG Mail 1.0 Public Beta — macOS and Linux\n\n{a.run_url}\n\n")
    s.write("| File | Size | SHA-256 | Signed | Install | Launch | Runtime |\n|---|---|---|---|---|---|---|\n")
    for r in rows:
        s.write(f"| `{r[0]}` | {r[1]:,} | `{r[2]}` | {r[3]} | {r[4]} | {r[5]} | {r[6]} |\n")
print(open(os.path.join(a.out, "SUMMARY.md")).read())
sys.exit(0 if all(r[5] == "passed" for r in rows) else 1)
