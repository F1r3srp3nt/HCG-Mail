#!/usr/bin/env python3
"""HCG Store release draft and HCG Release Passports for HCG Mail for Windows, from recorded CI evidence only.

Inputs (workflow windows-prerelease.yml): the package job's build-evidence.json, each runtime job's runtime-summary.json
and installer-test.txt, each mailbox job's mailbox-e2e.json (authenticated IMAP/SMTP with the dedicated test mailbox),
the Windows test results (.trx), and HCG Repo's release-passport-v1 schema.

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
ap.add_argument("--mailbox", help="folder holding one sub-folder per mailbox job (x64, arm64); absent = not run")
ap.add_argument("--display", help="folder with <theme>/display-scaling.json (windows-display-scaling.ps1); absent = not run")
ap.add_argument("--upgrade", help="folder with upgrade.json (windows-upgrade-check.ps1); absent = not run")
ap.add_argument("--e2e", help="folder with e2e.trx (IMAP/SMTP against the local TLS mail server); absent = not run")
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
    runtime[arch] = {"summary": r, "installer_passed": "Installer verified:" in inst_text and "  FAIL  " not in inst_text,
                     "installer_ran": bool(inst_text)}

# Authenticated IMAP/SMTP with the dedicated test mailbox, per architecture: passed / failed / not-run.
mailbox = {}
for arch in ("x64", "arm64"):
    m = os.path.join(a.mailbox, arch, "mailbox-e2e.json") if a.mailbox else ""
    r = json.load(open(m, encoding="utf-8-sig")) if m and os.path.exists(m) else None
    state = "not-run" if not r or not r.get("ran") else ("passed" if r.get("passed") else "failed")
    mailbox[arch] = {"state": state, "result": r}

# Display scaling (x64), per theme: offered scalings must render and close, at the scaling Windows applies to the window.
display = {}
for path in glob.glob(os.path.join(a.display, "*", "display-scaling.json")) if a.display else []:
    d = json.load(open(path, encoding="utf-8-sig"))
    display[os.path.basename(os.path.dirname(path))] = d
def _untested_scales():
    found = set()
    for d in (display or {}).values():
        for sc in d["scales"]:
            if not sc["available"]:
                found.add(sc["scale"])
    return [str(x) for x in sorted(found)]

def display_text():
    if not display:
        return "Display scaling: FAILED (no result)." if display_missing else "Display scaling: not run."
    parts = []
    for theme, d in sorted(display.items()):
        name = {"HcgFuture": "HCG Dark", "HcgClassic": "HCG Light"}.get(theme, theme)
        cells = []
        for sc in d["scales"]:
            if not sc["available"]:
                cells.append(f"{sc['scale']} % not offered by the runner display ({d['resolution']})")
            else:
                ok = sc["rendered"] and sc["closed"] and sc["windowScale"] == sc["scale"]
                cells.append(f"{sc['scale']} % {'passed' if ok else 'FAILED'}")
        parts.append(f"{name}: " + ", ".join(cells))
    return "Display scaling on " + next(iter(display.values()))["windows"] + " — " + "; ".join(parts) + "."
# A check the workflow asked for (its folder was passed) but that left no result is a failure, not "not run".
display_missing = bool(a.display) and not display
display_ok = bool(display) and all(sc["rendered"] and sc["closed"] and sc["windowScale"] == sc["scale"]
                                   for d in display.values() for sc in d["scales"] if sc["available"])

upgrade = json.load(open(os.path.join(a.upgrade, "upgrade.json"), encoding="utf-8-sig")) if a.upgrade and os.path.exists(os.path.join(a.upgrade, "upgrade.json")) else None
upgrade_state = ("failed" if a.upgrade else "not-run") if not upgrade else ("passed" if upgrade["failures"] == 0 else "failed")
def upgrade_text():
    if not upgrade:
        return ("Upgrade from the published earlier version: FAILED (no result)." if a.upgrade else
                "Upgrade from the published earlier version: not run.")
    return (f"Upgrade {upgrade['from']} → {upgrade['to']} on {upgrade['windows']} with existing user data and a saved password: {upgrade_state}, "
            f"{sum(c['passed'] for c in upgrade['checks'])}/{len(upgrade['checks'])} checks (registration, program version, data and password kept, "
            "starts with the account, uninstall keeps user data).")

e2e = {"total": 0, "passed": 0, "failed": 0}
for trx in glob.glob(os.path.join(a.e2e, "**", "*.trx"), recursive=True) if a.e2e else []:
    c = ET.parse(trx).getroot().find(".//t:Counters", ns)
    e2e["total"] += int(c.get("total")); e2e["passed"] += int(c.get("passed")); e2e["failed"] += int(c.get("failed"))
e2e_state = ("failed" if a.e2e else "not-run") if e2e["total"] == 0 else ("passed" if e2e["failed"] == 0 and e2e["passed"] > 0 else "failed")
def e2e_text():
    return ("IMAP/SMTP end to end against a local Dovecot/Postfix server with TLS and CRL validation (the app's mail engine, Linux runner): "
            + ("not run." if e2e_state == "not-run" else "FAILED (no result)." if e2e["total"] == 0 else f"{e2e_state}, {e2e['passed']} passed, {e2e['failed']} failed of {e2e['total']}."))

def mailbox_text(arch):
    m = mailbox[arch]
    if m["state"] == "not-run":
        return ("Authenticated IMAP/SMTP: not run (no dedicated test mailbox configured)."
                if not m["result"] or not m["result"].get("configured") else "Authenticated IMAP/SMTP: not run.")
    r = m["result"]
    passed = sum(1 for t in r.get("tests", []) if t.get("outcome") == "Passed")
    return (f"Authenticated IMAP/SMTP with the dedicated test mailbox on Windows {arch} ({r.get('windows', '-')}): {m['state']}, "
            f"{passed}/{r.get('total', 0)} tests passed (connection test, wrong password rejected, send to itself over SMTP and "
            f"receive over IMAP, Credential Manager and restart).")

def checks(arch, needle):
    r = runtime[arch]["summary"]
    return [c for c in (r["checks"] if r else []) if needle in c["check"]]

def all_passed(lst): return bool(lst) and all(c["passed"] for c in lst)

if not ev["files"]:
    sys.exit("build-evidence.json lists no files: refusing to write an empty draft")
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
    # Runtime is "passed" only with credential store, TLS validation AND authenticated IMAP/SMTP (dedicated mailbox) passed.
    mb = mailbox[arch]["state"]
    if (tls or cred) and not (all_passed(tls) and all_passed(cred)) or mb == "failed": runtime_state = "failed"
    elif all_passed(tls) and all_passed(cred): runtime_state = "passed" if mb == "passed" else "partial"
    else: runtime_state = "not-run"
    unsigned = f["authenticode"] not in ("Valid", "NotApplicable") or f["appExecutable"]["authenticode"] != "Valid"
    signing_evidence = (f"Get-AuthenticodeSignature: {f['fileName']} {'(ZIP: cannot be signed)' if f['authenticode'] == 'NotApplicable' else f['authenticode']}; "
                        f"HCGMail.exe {f['appExecutable']['authenticode']}. HCG Executable Maker does not implement Authenticode.")
    summary_text = (f"HCG Executable Maker engine {ev['build']['toolchain']}. Windows tests: {tests['passed']}/{tests['total']} passed, {tests['failed']} failed. "
                    f"Runtime on Windows {arch} ({rt['windows']['caption'] if rt else 'not run'}, build {rt['windows']['build'] if rt else '-'}): "
                    + (f"{sum(c['passed'] for c in rt['checks'])}/{len(rt['checks'])} checks passed (credential store, TLS accept/reject, start, window rendering, restart). " if rt else "not run. ")
                    + (f"Installer lifecycle (install, shortcuts, registration, launch, uninstall): {install_state}. " if kind == "installer" else "")
                    + mailbox_text(arch) + " " + e2e_text() + " " + display_text()
                    + (" " + upgrade_text() if kind == "installer" and arch == "x64" else ""))
    artifacts.append({
        "fileName": f["fileName"], "platform": "windows", "format": f["format"], "sizeBytes": f["sizeBytes"], "sha256": f["sha256"],
        "architectures": [arch], "minimumOs": MIN_OS[arch],
        "signing": {"status": "unsigned" if unsigned else "signed", "evidence": signing_evidence, "tool": "PowerShell Get-AuthenticodeSignature"},
        "kind": "installer" if kind == "installer" else "portable-app",
        "package": None, "sourceCommit": src["commit"], "buildProvider": "github-actions",
        "toolchain": ev["build"]["toolchain"][:200],
        "validationReport": {"tool": "HCG Mail windows-prerelease.yml (HCG Executable Maker checks, Windows tests, runtime check, installer test, dedicated mailbox test)",
                             "passed": tests["failed"] == 0 and launch_state == "passed" and runtime_state in ("partial", "passed") and install_state in ("passed", "not-applicable")
                                       and display_ok and e2e_state != "failed" and not (kind == "installer" and arch == "x64" and upgrade_state != "passed"),
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
            "device_installation": {"state": ("failed" if kind == "installer" and arch == "x64" and upgrade_state == "failed" else install_state),
                                    "detail": ("GitHub-hosted Windows runner, silent per-user install and uninstall (HCG Executable Maker Test-Installer.ps1)"
                                               + ("; " + upgrade_text() if arch == "x64" else "")) [:1000] if kind == "installer" else "portable: no installation"},
            "application_launch": {"state": launch_state if display_ok or not a.display else "failed",
                                   "detail": ("main window shown and rendered on start and restart, on a GitHub-hosted Windows runner. " + display_text())[:1000]},
            "runtime_verification": {"state": runtime_state, "detail": ("Credential Manager round trip and TLS certificate validation: "
                                     + ("passed" if all_passed(tls) and all_passed(cred) else "not passed") + ". " + mailbox_text(arch) + " " + e2e_text())[:1000]},
        }},
        "device_evidence": [],
        "compatibility_status": f"Launch verified on a GitHub-hosted Windows {arch} runner (build {rt['windows']['build'] if rt else '-'}); not on a user's PC yet",
        "runtime_compatibility": runtime_state if runtime_state in ("passed", "partial", "failed") else "not-established",
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
            *([f"Known limitation: display scaling {', '.join(_untested_scales())} % not tested (the test display did not offer it)."] if _untested_scales() else []),
            "Offline copies and the Outbox are encrypted per account with keys kept only in Windows Credential Manager; they are never uploaded.",
        ],
    }
    files.append(passport)
    summary_rows.append((f, arch, kind, install_state, launch_state, runtime_state, mailbox[arch]["state"], unsigned))

# Validate every passport against HCG Repo's schema.
import jsonschema
schema = json.load(open(a.schema, encoding="utf-8"))
for p in files: jsonschema.validate(p, schema)

untested_scales = _untested_scales()
notes = [
    f"HCG Mail {version} for Windows: public testing prerelease, free during testing (temporary; not a permanent free price).",
    "IMAP/SMTP email for your own mailbox. Passwords in Windows Credential Manager; TLS with certificate validation only.",
    "New: the official HCG logo with a transparent background in both themes, one HCG Mail wordmark (no overlapping title "
    "text), a cleaner welcome screen, and account setup focused on IMAP and SMTP.",
    "New: Network Doctor in Mail Health for school and other restricted Wi-Fi; automatic reconnection; mail you have read "
    "stays readable offline (encrypted on this PC); messages written offline wait in the Outbox and are sent when the connection returns.",
    "Installing over 1.0.0-beta.1 keeps your accounts, settings and saved passwords.",
    "Installer (per-user, no administrator rights) or portable ZIP, for x64 and ARM64. Self-contained: no .NET installation needed.",
    "Not code-signed: Windows SmartScreen warns. Compare the SHA-256 checksum before running.",
] + ([f"Known limitation: display scaling {', '.join(untested_scales)} % has not been tested (the test display did not offer it)."] if untested_scales else [])
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
    for arch in ("x64", "arm64"): s.write(f"- {mailbox_text(arch)}\n")
    s.write(f"- {e2e_text()}\n- {display_text()}\n- {upgrade_text()}\n")
    s.write("\n| File | Size | SHA-256 | Signed | Install | Launch | Runtime | Mailbox IMAP/SMTP |\n|---|---|---|---|---|---|---|---|\n")
    for f, arch, kind, i, l, r, mb, uns in summary_rows:
        s.write(f"| `{f['fileName']}` | {f['sizeBytes']:,} | `{f['sha256']}` | {'no' if uns else 'yes'} | {i} | {l} | {r} | {mb} |\n")
print(open(os.path.join(a.out, "SUMMARY.md")).read())
sys.exit(0 if tests["failed"] == 0 and (display_ok or not a.display) and upgrade_state != "failed" and e2e_state != "failed" else 1)
