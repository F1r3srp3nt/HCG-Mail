#!/usr/bin/env python3
"""Release manifest and HCG Release Passports for HCG Mail for macOS, from recorded signing and download evidence only.

Inputs:
  --dist        the distribution files (HCGMail-<version>-osx-<arch>.zip|.pkg|.dmg)
  --signing     folder with signing-evidence-osx-<arch>.json (sign-and-notarize.sh)
  --verify      folder with verification-<file>.json (verify-distribution.sh), in any sub-folders
  --schema      HCG Repo's release-passport-v1 schema (optional: without jsonschema or the schema the passports are
                written and reported as "not validated")
  --run-url     where the evidence comes from (a workflow run, or "signed on the owner's Mac on <date>")
Outputs, in --out: release-manifest.json, passports/<file>.passport.json, SHA256SUMS.txt, SUMMARY.md, and
store-release-draft.json: the body for HCG Store's POST /api/v1/publishing/releases (a PRIVATE DRAFT, edition
macos-desktop, channel beta, free) and one ArtifactInput per file, the same shape as the Windows draft. Nothing is sent.

Nothing is invented: a file is "developer-id-notarized" only when its signing evidence says so AND a download check
on a Mac accepted it as "Notarized Developer ID". Test builds (ad-hoc) are refused: they are never release artifacts.
"""
import argparse, glob, hashlib, json, os, sys

ap = argparse.ArgumentParser()
ap.add_argument("--dist", required=True)
ap.add_argument("--signing", required=True)
ap.add_argument("--verify", required=True)
ap.add_argument("--schema")
ap.add_argument("--run-url", required=True)
ap.add_argument("--out", required=True)
ap.add_argument("--commit", default=os.environ.get("GITHUB_SHA", "not-recorded"))
ap.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", "F1r3srp3nt/HCG-Mail"))
ap.add_argument("--branch", default=os.environ.get("GITHUB_REF_NAME", "not-recorded"))
ap.add_argument("--run-id", type=int, default=int(os.environ.get("GITHUB_RUN_ID", "0") or 0))
a = ap.parse_args()

FORMAT = {".zip": "zip", ".pkg": "pkg", ".dmg": "dmg"}
MIN_OS = {"arm64": "macOS 11 or later (the app's LSMinimumSystemVersion), on .NET 10's supported macOS versions; Apple silicon",
          "x64": "macOS 11 or later (the app's LSMinimumSystemVersion), on .NET 10's supported macOS versions; Intel"}
LIMITATIONS = [
    "The window needs a display at least 1040 points wide.",
    "Passwords are kept in the macOS login Keychain. Passwords saved by an earlier ad-hoc signed test build were bound to "
    "that build's signature: macOS asks once whether HCG Mail may use them (choose Always Allow).",
    "IMAP and SMTP only; Microsoft and Google sign-in are not part of the Public Beta.",
]

def sha(path, algo):
    h = hashlib.new(algo)
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def code_files(ev):
    """Number of Mach-O files in Contents/MacOS, from the layout check ("N files")."""
    layout = next((c for c in ev["checks"] if c["check"].startswith("Contents/MacOS holds only Mach-O")), None)
    return int(layout["detail"].split()[0]) if layout else 0

signing = {}
for p in glob.glob(os.path.join(a.signing, "**", "signing-evidence-osx-*.json"), recursive=True):
    ev = json.load(open(p))
    if ev.get("mode") != "developer-id":
        sys.exit(f"{os.path.basename(p)} is a {ev.get('mode')} build: test builds are never release artifacts")
    for f in ev["files"]:
        signing[f["fileName"]] = ev
if not signing:
    sys.exit("No Developer ID signing evidence: refusing to write a release manifest")
verified = {}
for p in glob.glob(os.path.join(a.verify, "**", "verification-*.json"), recursive=True):
    v = json.load(open(p))
    verified.setdefault(v["file"], []).append(v)

artifacts, passports, rows, store_artifacts = [], [], [], []
for name in sorted(signing):
    ev = signing[name]
    path = os.path.join(a.dist, name)
    fe = next(f for f in ev["files"] if f["fileName"] == name)
    assert os.path.getsize(path) == fe["sizeBytes"] and sha(path, "sha256") == fe["sha256"], f"{name}: bytes differ from the signing evidence"
    kind = FORMAT[os.path.splitext(name)[1]]
    arch = ev["architecture"]
    sign_ok = ev["failures"] == 0 and all(s["status"] == "Accepted" for s in ev["notarization"])
    checks = [c for v in verified.get(name, []) if v["sha256"] == fe["sha256"] for c in v["checks"] if not c.get("info")]
    runs = [v for v in verified.get(name, []) if v["sha256"] == fe["sha256"]]
    def state(needle):
        found = [c for c in checks if needle in c["check"]]
        return ("passed" if all(c["passed"] for c in found) else "failed") if found else "not-run"
    gatekeeper = state("Gatekeeper")
    install = state("Installed") if kind == "pkg" else state("extracted" if kind == "zip" else "copied out")
    launch = state("Launch and runtime checks")
    notarized = sign_ok and gatekeeper == "passed"
    hosts = sorted({f"macOS {v['macos']} ({v['machine']})" for v in runs})
    status = "developer-id-notarized" if notarized else ("developer-id" if sign_ok else "failed")
    signing_record = {
        "status": "signed" if sign_ok else "failed",
        "kind": status,
        "application": ev["signing"]["application"], "installer": ev["signing"]["installer"] if kind == "pkg" else None,
        "teamId": ev["signing"]["teamId"], "hardenedRuntime": True, "entitlements": ev["signing"]["entitlements"],
        "notarization": [s for s in ev["notarization"] if s["container"].startswith(("app-", kind + "-"))],
        "gatekeeper": gatekeeper,
    }
    validation = (f"Signed with {ev['signing']['application']}{' and ' + ev['signing']['installer'] if kind == 'pkg' else ''}, hardened runtime, "
                  f"notarized by Apple ({', '.join(s['submissionId'] + ' ' + s['status'] for s in signing_record['notarization'])}) and stapled. "
                  f"Download check (quarantine kept): Gatekeeper {gatekeeper}, install {install}, launch {launch}"
                  + (f" on {', '.join(hosts)}." if hosts else " — not run yet."))
    artifacts.append({
        "fileName": name, "format": kind, "version": ev["version"], "buildNumber": ev["buildNumber"], "platform": "macos", "architecture": arch,
        "sizeBytes": fe["sizeBytes"], "sha256": fe["sha256"], "sha512": fe["sha512"], "signing": signing_record, "minimumOs": MIN_OS[arch],
        "sourceCommit": a.commit, "validation": {"gatekeeper": gatekeeper, "install": install, "launch": launch, "verifiedOn": hosts, "summary": validation},
        "knownLimitations": LIMITATIONS,
    })
    passports.append({
        "format": "hcg.release-passport", "formatVersion": 1,
        "product": {"key": "hcg-mail", "name": "HCG Mail", "store_slug": "hcg-mail", "central_id": "hcg.mail"},
        "edition": "macos-desktop", "platform": "macos-desktop", "architecture": arch,
        "package": "com.hakmicapitalgroup.hcgmail", "version": ev["version"],
        "source": {"repository": a.repository, "commit": a.commit, "branch": a.branch},
        "build": {"provider": "github-actions" if a.run_id else "owner-mac", "toolchain": f"HCG Executable Maker engine; signed on macOS {ev['host']['macos']} ({ev['host']['machine']})",
                  "built_at": None, "run_id": a.run_id or None, "artifact": f"hcg-mail-macos-{ev['version']}"},
        "artifact": {"filename": name, "package": "com.hakmicapitalgroup.hcgmail", "version": ev["version"], "architecture": arch,
                     "sha256": fe["sha256"], "sha512": fe["sha512"], "size": fe["sizeBytes"]},
        "validation": {"levels": {
            "static_inspection": {"state": "passed" if sign_ok else "failed", "detail": f"{len(ev['checks'])} signing checks: layout (code only in Contents/MacOS), Developer ID signature, hardened runtime, secure timestamp, entitlements, notarization, stapling"},
            "maintainer_script_review": {"state": "passed" if kind == "pkg" else "not-applicable", "detail": "the installer package runs no scripts" if kind == "pkg" else "no scripts"},
            "script_free_lifecycle": {"state": "not-applicable", "detail": "Debian lifecycle level"},
            "device_installation": {"state": install, "detail": f"{kind.upper()} installed with the quarantine attribute kept" + (f" on {', '.join(hosts)}" if hosts else "")},
            "application_launch": {"state": launch, "detail": "started, captured on screen, quit and restarted" + (f" on {', '.join(hosts)}" if hosts else "")},
            "runtime_verification": {"state": "partial" if launch == "passed" else launch, "detail": "credential store, TLS accept/refuse, start-up and restart; authenticated IMAP/SMTP not exercised here"},
        }},
        "device_evidence": [],
        "compatibility_status": f"Gatekeeper {gatekeeper}; launch verified on {', '.join(hosts)}" if hosts else "not verified on a Mac yet",
        "runtime_compatibility": "partial" if launch == "passed" else ("failed" if launch == "failed" else "not-established"),
        "approvals": [],
        "distribution": {"channel": "candidate", "access": "public"},
        "publication": {"record_status": "protected", "in_index_builds": False},
        "repository_signing": {"scope": "not applicable: distributed through HCG Store, not HCG Repo", "configured": False, "authenticated": False, "status": "not-applicable"},
        "code_signing": {"scope": f"macOS {kind.upper()}: the .app bundle and its libraries" + (", and the installer package" if kind == "pkg" else ", and the disk image" if kind == "dmg" else ""),
                         "method_claimed": f"Apple Developer ID ({ev['signing']['application']}), hardened runtime, notarized and stapled",
                         "binaries": code_files(ev),
                         "all_signed": sign_ok, "entitlements": {k: True for k in ev["signing"]["entitlements"]}},
        "notes": ["HCG Mail 1.0 Public Beta for macOS; distribution.channel 'candidate' stands for Store channel 'beta'. publication.record_status 'protected' means not published.",
                  f"Evidence: {a.run_url}"],
    })
    rows.append((name, fe["sizeBytes"], fe["sha256"], status, gatekeeper, install, launch))
    store_artifacts.append({
        "fileName": name, "platform": "macos", "format": kind, "sizeBytes": fe["sizeBytes"], "sha256": fe["sha256"],
        "architectures": [arch], "minimumOs": MIN_OS[arch],
        "signing": {"status": "signed" if notarized else "unsigned",
                    "evidence": f"codesign: {ev['signing']['application']}, hardened runtime, secure timestamp"
                                + (f"; pkgutil: {ev['signing']['installer']}" if kind == "pkg" else "")
                                + f"; notarytool: {', '.join(s['submissionId'] + ' ' + s['status'] for s in signing_record['notarization'])}; stapled"
                                + f"; spctl on the quarantined download: {gatekeeper}",
                    "tool": "codesign, pkgutil, xcrun notarytool, xcrun stapler, spctl"},
        "kind": "installer" if kind == "pkg" else "portable-app",
        "package": "com.hakmicapitalgroup.hcgmail", "sourceCommit": a.commit, "buildProvider": "github-actions" if a.run_id else "owner-mac",
        "toolchain": f"HCG Executable Maker engine; signed on macOS {ev['host']['macos']} ({ev['host']['machine']})",
        "validationReport": {"tool": "HCG Mail macos-release.yml (sign-and-notarize.sh, verify-distribution.sh, HCG Executable Maker Signing Doctor)",
                             "passed": notarized and install == "passed" and launch == "passed", "summary": validation[:4000]},
        "replaces": None,
    })

validated = "not validated (no schema given)"
if a.schema:
    try:
        import jsonschema
        schema = json.load(open(a.schema, encoding="utf-8"))
        for p in passports:
            jsonschema.validate(p, schema)
        validated = "validated against release-passport-v1"
    except ImportError:
        validated = "not validated (python jsonschema is not installed: pip3 install --user jsonschema)"

os.makedirs(os.path.join(a.out, "passports"), exist_ok=True)
json.dump({"release": "HCG Mail 1.0 Public Beta — macOS", "evidence": a.run_url, "passports": validated, "artifacts": artifacts},
          open(os.path.join(a.out, "release-manifest.json"), "w"), indent=2)
for p in passports:
    json.dump(p, open(os.path.join(a.out, "passports", p["artifact"]["filename"] + ".passport.json"), "w"), indent=2)
ev0 = next(iter(signing.values()))
release = {
    "productId": "hcg-mail", "editionId": "macos-desktop", "version": ev0["version"], "build": str(ev0["buildNumber"]), "channel": "beta",
    "releaseNotes": [
        f"HCG Mail {ev0['version']} for Mac: public testing prerelease, free during testing (temporary; not a permanent free price).",
        "IMAP/SMTP email for your own mailbox. Passwords in the macOS Keychain; TLS with certificate validation only.",
        "Signed with HCG's Developer ID and notarized by Apple. Installer package for /Applications, or the app as a ZIP.",
        "For Macs with Apple silicon." if {x["architectures"][0] for x in store_artifacts} == {"arm64"} else "For Macs with Apple silicon and Intel.",
        "Microsoft and Google sign-in are not available in this build (the options are hidden). Use IMAP/SMTP.",
    ],
    "compatibility": {"minimumOs": {"macos": f"macOS {ev0['minimumSystemVersion']}" if ev0.get("minimumSystemVersion") else "not recorded"},
                      "architectures": sorted({x["architectures"][0] for x in store_artifacts}),
                      "notes": ["Verified on " + (", ".join(sorted({h for x in artifacts for h in x["validation"]["verifiedOn"]})) or "no Mac yet") + "."]},
    "distribution": "free", "eligibility": "private", "destinations": ["hcg-store"],
}
json.dump({"_note": "Body for HCG Store POST /api/v1/publishing/releases (creates a PRIVATE DRAFT) and the ArtifactInput for each file. "
                    "Not sent: publication is the owner's, through the Store Control Center. 'Public beta' maps to channel 'beta' with a "
                    "SemVer pre-release version; 'free during the beta' maps to distribution 'free'.",
           "release": release, "artifacts": store_artifacts}, open(os.path.join(a.out, "store-release-draft.json"), "w"), indent=2)
with open(os.path.join(a.out, "SHA256SUMS.txt"), "w") as s:
    for r in rows:
        s.write(f"{r[2]}  {r[0]}\n")
with open(os.path.join(a.out, "SUMMARY.md"), "w") as s:
    s.write(f"# HCG Mail 1.0 Public Beta — macOS\n\n{a.run_url}\n\nPassports: {validated}.\n\n")
    s.write("| File | Size | SHA-256 | Signature | Gatekeeper (quarantined) | Install | Launch |\n|---|---|---|---|---|---|---|\n")
    for r in rows:
        s.write(f"| `{r[0]}` | {r[1]:,} | `{r[2]}` | {r[3]} | {r[4]} | {r[5]} | {r[6]} |\n")
print(open(os.path.join(a.out, "SUMMARY.md")).read())
sys.exit(0 if all(r[3] == "developer-id-notarized" and r[6] == "passed" for r in rows) else 1)
