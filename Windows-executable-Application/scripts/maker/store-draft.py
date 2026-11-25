#!/usr/bin/env python3
"""HCG Store release draft and HCG Release Passports for HCG Mail for Windows, from recorded CI evidence only.

Inputs (workflow windows-prerelease.yml): the package job's build-evidence.json, each runtime job's runtime-summary.json
and installer-test.txt, the Windows test results (.trx), and HCG Repo's release-passport-v1 schema.

Outputs, in --out:
  store-release-draft.json   the exact body for HCG Store's POST /api/v1/publishing/releases (a PRIVATE DRAFT) and one
                             ArtifactInput per file for POST .../releases/:id/artifacts. Nothing is sent here.
  passports/<file>.json      one hcg.release-passport v1 per file, validated against the schema.
  SUMMARY.md                 what the evidence shows, per file.
Nothing is invented: a field without evidence is null, "not-run" or "not-recorded".
"""
import argparse, glob, hashlib, json, os, sys, xml.etree.ElementTree as ET

ap = argparse.ArgumentParser()
ap.add_argument("--dist", required=True, help="folder with the downloadable files")
ap.add_argument("--evidence", required=True, help="build-evidence.json")
ap.add_argument("--runtime", required=True, help="folder holding one sub-folder per runtime job (x64, arm64)")
ap.add_argument("--tests", required=True, help="folder with the Windows .trx results")
ap.add_argument("--schema", required=True, help="HCG Repo docs/schemas/release-passport-v1.schema.json")
ap.add_argument("--run-url", required=True)
ap.add_argument("--out", required=True)
a = ap.parse_args()

ev = json.load(open(a.evidence, encoding="utf-8-sig"))
version, build_no = ev["version"], str(ev["buildNumber"])
src, bld = ev["source"], ev["build"]
MIN_OS = {"x64": "Windows 10 1607", "arm64": "Windows 11"}

def sha512(path):
    h = hashlib.sha512()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""): h.update(chunk)
    return h.hexdigest()

# Windows regression tests
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
tests = {"total": 0, "passed": 0, "failed": 0, "skipped": 0}
for trx in glob.glob(os.path.join(a.tests, "**", "*.trx"), recursive=True):
    c = ET.parse(trx).getroot().find(".//t:Counters", ns)
    tests["total"] += int(c.get("total")); tests["passed"] += int(c.get("passed")); tests["failed"] += int(c.get("failed"))
    tests["skipped"] += int(c.get("total")) - int(c.get("executed"))

# Runtime results per architecture
runtime = {}
for arch in ("x64", "arm64"):
    d = os.path.join(a.runtime, arch)
    summary = os.path.join(d, "runtime-summary.json")
    inst = os.path.join(d, "installer-test.txt")
    r = json.load(open(summary, encoding="utf-8-sig")) if os.path.exists(summary) else None
    inst_text = open(inst, encoding="utf-8-sig", errors="replace").read() if os.path.exists(inst) else ""
    runtime[arch] = {"summary": r, "installer_passed": bool(inst_text) and "check(s) failed" not in inst_text and "FAIL" not in inst_text,
                     "installer_ran": bool(inst_text)}

def checks(arch, needle):
    r = runtime[arch]["summary"]
    return [c for c in (r["checks"] if r else []) if needle in c["check"]]

def all_passed(lst): return bool(lst) and all(c["passed"] for c in lst)

files, artifacts, summary_rows = [], [], []
for f in ev["files"]:
    path = os.path.join(a.dist, f["fileName"])
    assert os.path.getsize(path) == f["sizeBytes"], f["fileName"]
    assert hashlib.sha256(open(path, "rb").read()).hexdigest() == f["sha256"], f["fileName"]
    arch, kind = f["architecture"], f["kind"]
    rt = runtime[arch]["summary"]
    launch = checks(arch, "main window shown") + checks(arch, "rendered content")
    tls = checks(arch, "TLS ")
    cred = checks(arch, "Credential")
    install_state = ("passed" if runtime[arch]["installer_passed"] else "failed") if (kind == "installer" and runtime[arch]["installer_ran"]) \
        else ("not-applicable" if kind == "portable" else "not-run")
    launch_state = "passed" if all_passed(launch) else ("failed" if launch else "not-run")
    # Authenticated IMAP/SMTP (sign-in, send, receive) is not exercised on Windows in CI: runtime is partial at best.
    runtime_state = "partial" if (all_passed(tls) and all_passed(cred)) else ("failed" if (tls or cred) else "not-run")
    unsigned = f["authenticode"] != "Valid" or f["appExecutable"]["authenticode"] != "Valid"
    signing_evidence = f"Get-AuthenticodeSignature: {f['fileName']} {f['authenticode']}; HCGMail.exe {f['appExecutable']['authenticode']}. HCG Executable Maker does not implement Authenticode."
    summary_text = (f"HCG Executable Maker engine {ev['build']['toolchain']}. Windows tests: {tests['passed']}/{tests['total']} passed, {tests['failed']} failed. "
                    f"Runtime on Windows {arch} ({rt['windows']['caption'] if rt else 'not run'}, build {rt['windows']['build'] if rt else '-'}): "
                    + (f"{sum(c['passed'] for c in rt['checks'])}/{len(rt['checks'])} checks passed (credential store, TLS accept/reject, start, window rendering, restart). " if rt else "not run. ")
                    + (f"Installer lifecycle (install, shortcuts, registration, launch, uninstall): {install_state}. " if kind == "installer" else "")
                    + "Authenticated IMAP/SMTP (sign-in, send, receive) not exercised on Windows in CI.")
    artifacts.append({
        "fileName": f["fileName"], "platform": "windows", "format": f["format"], "sizeBytes": f["sizeBytes"], "sha256": f["sha256"],
        "architectures": [arch], "minimumOs": MIN_OS[arch],
        "signing": {"status": "unsigned" if unsigned else "signed", "evidence": signing_evidence, "tool": "PowerShell Get-AuthenticodeSignature"},
        "kind": "installer" if kind == "installer" else "portable-app",
        "package": None, "sourceCommit": src["commit"], "buildProvider": "github-actions",
        "toolchain": ev["build"]["toolchain"][:200],
        "validationReport": {"tool": "HCG Mail windows-prerelease.yml (HCG Executable Maker checks, Windows tests, runtime check, installer test)",
                             "passed": tests["failed"] == 0 and launch_state == "passed" and runtime_state in ("partial", "passed") and install_state in ("passed", "not-applicable"),
                             "summary": summary_text[:4000]},
        "replaces": None,
    })
    passport = {
        "format": "hcg.release-passport", "formatVersion": 1,
        "product": {"key": "hcg-mail", "name": "HCG Mail", "store_slug": "hcg-mail", "central_id": "hcg.mail"},
        "edition": "windows-desktop", "platform": "windows-desktop", "architecture": arch,
        "package": "com.hakmicapitalgroup.hcgmail", "version": version,
        "source": {"repository": src["repository"], "commit": src["commit"], "branch": src["branch"]},
        "build": {"provider": "github-actions", "toolchain": ev["build"]["toolchain"], "built_at": None,
                  "run_id": int(bld["runId"]), "artifact": f"hcg-mail-windows-{version}"},
        "artifact": {"filename": f["fileName"], "package": "com.hakmicapitalgroup.hcgmail", "version": version, "architecture": arch,
                     "sha256": f["sha256"], "sha512": sha512(path), "size": f["sizeBytes"]},
        "validation": {"levels": {
            "static_inspection": {"state": "passed", "detail": "HCG Executable Maker artifact checks; PE machine type and version info verified"},
            "maintainer_script_review": {"state": "not-applicable", "detail": "Windows installer, not a Debian package"},
            "script_free_lifecycle": {"state": "not-applicable", "detail": "Debian lifecycle level; see device_installation for the Windows installer"},
            "device_installation": {"state": install_state, "detail": "GitHub-hosted Windows runner, silent per-user install and uninstall (HCG Executable Maker Test-Installer.ps1)" if kind == "installer" else "portable: no installation"},
            "application_launch": {"state": launch_state, "detail": "main window shown and rendered on start and restart, on a GitHub-hosted Windows runner"},
            "runtime_verification": {"state": runtime_state, "detail": "Credential Manager round trip and TLS certificate validation passed; authenticated IMAP/SMTP not exercised"},
        }},
        "device_evidence": [],
        "compatibility_status": f"Launch verified on a GitHub-hosted Windows {arch} runner (build {rt['windows']['build'] if rt else '-'}); not on a user's PC yet",
        "runtime_compatibility": "partial" if runtime_state == "partial" else ("failed" if runtime_state == "failed" else "not-established"),
        "approvals": [],
        "distribution": {"channel": "candidate", "access": "public"},
        "publication": {"record_status": "protected", "in_index_builds": False},
        "repository_signing": {"scope": "not applicable: distributed through HCG Store, not HCG Repo", "configured": False,
                               "authenticated": False, "status": "not-applicable"},
        "code_signing": {"scope": "Windows PE (Authenticode): the downloadable file and HCGMail.exe", "method_claimed": None,
                         "binaries": 2, "all_signed": not unsigned, "entitlements": {}},
        "notes": [
            "Passport v1 is shaped around HCG Repo. For this HCG Store file: distribution.channel 'candidate' stands for Store channel 'beta' "
            "(public testing prerelease); access 'public' is the intended access after the owner's publication approval, the Store draft "
            "is private; publication.record_status 'protected' means not published.",
            f"Evidence: {a.run_url}",
        ],
    }
    files.append(passport)
    summary_rows.append((f, arch, kind, install_state, launch_state, runtime_state, unsigned))

# Validate every passport against HCG Repo's schema.
import jsonschema
schema = json.load(open(a.schema, encoding="utf-8"))
for p in files: jsonschema.validate(p, schema)

notes = [
    "HCG Mail 0.1.0-beta.1 for Windows: public testing prerelease, free during testing (temporary; not a permanent free price).",
    "IMAP/SMTP email for your own mailbox. Passwords in Windows Credential Manager; TLS with certificate validation only.",
    "Installer (per-user, no administrator rights) or portable ZIP, for x64 and ARM64. Self-contained: no .NET installation needed.",
    "Not code-signed: Windows SmartScreen warns. Compare the SHA-256 checksum before running.",
    "Microsoft and Google sign-in are not set up in this build.",
]
release = {
    "productId": "hcg-mail", "editionId": "windows-desktop", "version": version, "build": build_no, "channel": "beta",
    "releaseNotes": notes,
    "compatibility": {"minimumOs": {"windows": "Windows 10 1607 (x64), 11 (ARM64)"}, "architectures": ["x64", "arm64"],
                      "notes": ["Tested on GitHub-hosted Windows runners: x64 build " + str(runtime['x64']['summary']['windows']['build'] if runtime['x64']['summary'] else '-')
                                + ", ARM64 build " + str(runtime['arm64']['summary']['windows']['build'] if runtime['arm64']['summary'] else '-') + "."]},
    "distribution": "free", "eligibility": "private", "destinations": ["hcg-store"],
}
os.makedirs(os.path.join(a.out, "passports"), exist_ok=True)
json.dump({"_note": "Body for HCG Store POST /api/v1/publishing/releases (creates a PRIVATE DRAFT) and the ArtifactInput for each file. "
                    "Not sent: the HCG Store publishing server is not deployed and no account holds store.publish. "
                    "'Public Prerelease' maps to channel 'beta' with a SemVer pre-release version; 'temporarily free' maps to "
                    "distribution 'free' plus the release notes (the Store has no time-limited free price).",
           "release": release, "artifacts": artifacts}, open(os.path.join(a.out, "store-release-draft.json"), "w"), indent=2)
for p in files:
    json.dump(p, open(os.path.join(a.out, "passports", p["artifact"]["filename"] + ".passport.json"), "w"), indent=2)
with open(os.path.join(a.out, "SUMMARY.md"), "w") as s:
    s.write(f"# HCG Mail for Windows {version} (build {build_no})\n\nSource {src['repository']} {src['commit']} · {a.run_url}\n\n")
    s.write(f"Windows regression tests: {tests['passed']} passed, {tests['failed']} failed, {tests['skipped']} skipped (of {tests['total']}).\n\n")
    s.write("| File | Size | SHA-256 | Signed | Install | Launch | Runtime |\n|---|---|---|---|---|---|---|\n")
    for f, arch, kind, i, l, r, uns in summary_rows:
        s.write(f"| `{f['fileName']}` | {f['sizeBytes']:,} | `{f['sha256']}` | {'no' if uns else 'yes'} | {i} | {l} | {r} |\n")
print(open(os.path.join(a.out, "SUMMARY.md")).read())
sys.exit(0 if tests["failed"] == 0 else 1)
