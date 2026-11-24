#!/usr/bin/env python3
"""Checks an HCG Mail iOS build, or the signing setup before a build. Standard library only; uses Apple's command-line
tools (codesign, security, xcrun assetutil) when they are available, and says so when a check could not run.

  verify-ios-build.py app  <HCGMail.iOS.app | HCG Mail .ipa> [--kind device|simulator] [--app-store]
                           [--marketing-version X.Y.Z] [--build-number N]
      bundle ID, marketing version and build number, minimum iOS, device families, the SDK and Xcode it was built
      with (Apple's App Store minimum), app icon, launch screen, privacy manifest, Microsoft sign-in URL scheme and —
      for a signed build — signature, entitlements (Keychain groups) and the embedded provisioning profile.
      --app-store: the build must be signed for App Store distribution (what export-ipa.sh produces).

  verify-ios-build.py profile --profile <name | UUID | path> --identity <signing identity> [--app-store]
      before signing: the profile is for exactly this bundle ID (the App ID of the App Store Connect app record), from
      the identity's team, not expired, of the right type, and contains the identity's certificate.

Exit code 0 when nothing failed. Prints one line per check: OK, WARN, FAIL or INFO.
"""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile

IOS_ROOT = pathlib.Path(__file__).resolve().parents[1]
APP_SOURCE = IOS_ROOT / "src" / "HCG.Mail.iOS"
BUNDLE_ID = os.environ.get("HCG_IOS_BUNDLE_ID", "com.hakmicapitalgroup.hcgmail")
MSAL_KEYCHAIN_GROUP = "com.microsoft.adalcache"
# Entitlements an App Store build of HCG Mail may carry; anything else was added by a profile or by mistake.
ALLOWED_ENTITLEMENTS = {"application-identifier", "com.apple.developer.team-identifier", "keychain-access-groups",
                        "get-task-allow", "beta-reports-active"}


def toolchain(name):
    text = (IOS_ROOT / "toolchain.props").read_text(encoding="utf-8")
    m = re.search(rf"<{name}>([^<]*)</{name}>", text)
    return m.group(1).strip() if m else ""


class Report:
    def __init__(self, title):
        self.failed = 0
        print(title)

    def line(self, status, check, detail):
        if status == "FAIL":
            self.failed += 1
        print(f"  {status:<5} {check:<20} {detail}")

    def ok(self, check, detail): self.line("OK", check, detail)
    def warn(self, check, detail): self.line("WARN", check, detail)
    def fail(self, check, detail): self.line("FAIL", check, detail)
    def info(self, check, detail): self.line("INFO", check, detail)
    def expect(self, condition, check, good, bad):
        (self.ok if condition else self.fail)(check, good if condition else bad)


def tool(*args):
    """Runs an Apple tool; returns (exit code, stdout, stderr) or None when the tool is not installed."""
    if shutil.which(args[0]) is None:
        return None
    p = subprocess.run(args, capture_output=True)
    return p.returncode, p.stdout, p.stderr.decode("utf-8", "replace")


def version_tuple(text):
    return tuple(int(x) for x in re.findall(r"\d+", text)[:3])


def png_info(path):
    """(width, height, has_alpha) from the PNG header."""
    data = path.read_bytes()[:33]
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        return None
    width, height, _, color_type = struct.unpack(">IIBB", data[16:26])
    return width, height, color_type in (4, 6)


def decode_profile(path):
    res = tool("security", "cms", "-D", "-i", str(path))
    if res is None or res[0] != 0:
        return None
    return plistlib.loads(res[1])


def profile_kind(profile):
    ent = profile.get("Entitlements", {})
    if ent.get("get-task-allow"):
        return "Development"
    if profile.get("ProvisionsAllDevices"):
        return "Enterprise"
    if profile.get("ProvisionedDevices"):
        return "Ad Hoc"
    return "App Store"


def check_profile(r, profile, team, app_store, label):
    app_id = profile.get("Entitlements", {}).get("application-identifier", "")
    profile_team = (profile.get("TeamIdentifier") or [""])[0]
    expected = f"{profile_team}.{BUNDLE_ID}"
    r.expect(app_id == expected, f"{label} app ID", f"{app_id} (profile \"{profile.get('Name', '?')}\")",
             f"{app_id or 'none'} — expected {expected}. The profile must be made for the explicit App ID "
             f"{BUNDLE_ID} (no wildcard), the same App ID as the App Store Connect app record.")
    if team:
        r.expect(profile_team == team, f"{label} team", profile_team, f"profile team {profile_team}, identity team {team}")
    expires = profile.get("ExpirationDate")
    if isinstance(expires, datetime.datetime):
        now = datetime.datetime.now(expires.tzinfo) if expires.tzinfo else datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)
        days = (expires - now).days
        if days < 0:
            r.fail(f"{label} expiry", f"expired on {expires:%Y-%m-%d} — download or regenerate it in the developer account")
        else:
            (r.ok if days >= 30 else r.warn)(f"{label} expiry", f"valid until {expires:%Y-%m-%d} ({days} days)")
    kind = profile_kind(profile)
    if app_store:
        r.expect(kind == "App Store", f"{label} type", "App Store distribution",
                 f"{kind} — App Store Connect accepts only builds signed with an App Store distribution profile")
    else:
        r.info(f"{label} type", kind)
    groups = profile.get("Entitlements", {}).get("keychain-access-groups", [])
    r.expect(any(g in (f"{profile_team}.*", f"{profile_team}.{BUNDLE_ID}") for g in groups), f"{label} Keychain",
             f"allows {', '.join(groups)}", f"does not allow the app's Keychain group ({groups})")
    return profile_team


# ---------------------------------------------------------------------------------------------------------------- app

def check_app(args):
    tmp = None
    path = pathlib.Path(args.path)
    if path.suffix == ".ipa":
        tmp = tempfile.TemporaryDirectory()
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            z.extractall(tmp.name)
        apps = sorted(pathlib.Path(tmp.name, "Payload").glob("*.app"))
        if len(apps) != 1 or any(not n.startswith(("Payload/", "Symbols/", "SwiftSupport/")) for n in names):
            print(f"error: {path} is not a well-formed .ipa (one Payload/*.app expected)")
            return 1
        app = apps[0]
    else:
        app = path
    r = Report(f"HCG Mail build check: {path}")
    try:
        return run_app_checks(r, app, args)
    finally:
        if tmp:
            tmp.cleanup()


def run_app_checks(r, app, args):
    info_path = app / "Info.plist"
    if not info_path.is_file():
        r.fail("bundle", f"{app} has no Info.plist")
        return 1
    info = plistlib.loads(info_path.read_bytes())
    platform = info.get("DTPlatformName", "")
    kind = args.kind or ("simulator" if platform == "iphonesimulator" else "device")

    r.expect(info.get("CFBundleIdentifier") == BUNDLE_ID, "bundle ID", BUNDLE_ID,
             f"{info.get('CFBundleIdentifier')} — expected {BUNDLE_ID}")
    executable = app / info.get("CFBundleExecutable", "")
    r.expect(executable.is_file(), "executable", executable.name, f"CFBundleExecutable {executable.name} missing")

    short = str(info.get("CFBundleShortVersionString", ""))
    build = str(info.get("CFBundleVersion", ""))
    r.expect(re.fullmatch(r"\d+(\.\d+){0,2}", short) is not None, "marketing version", short,
             f"'{short}' — App Store Connect needs one to three period-separated integers")
    r.expect(re.fullmatch(r"\d+(\.\d+){0,2}", build) is not None, "build number", build, f"'{build}' is not a valid CFBundleVersion")
    if args.marketing_version:
        r.expect(short == args.marketing_version, "marketing version", f"matches {args.marketing_version}", f"{short}, requested {args.marketing_version}")
    if args.build_number:
        r.expect(build == args.build_number, "build number", f"matches {args.build_number}", f"{build}, requested {args.build_number}")

    minimum = toolchain("HcgMinimumIos")
    r.expect(info.get("MinimumOSVersion") == minimum, "minimum iOS", minimum, f"{info.get('MinimumOSVersion')} — toolchain.props says {minimum}")
    families = info.get("UIDeviceFamily", [])
    r.expect(1 in families and 2 in families, "devices", "iPhone and iPad", f"UIDeviceFamily {families}")

    # SDK and Xcode: Apple rejects uploads built with an SDK older than its current minimum.
    sdk_name = info.get("DTSDKName", "")
    required_sdk = toolchain("HcgAppStoreMinimumSdk")
    expected_platform = "iphoneos" if kind == "device" else "iphonesimulator"
    r.expect(platform == expected_platform, "platform", platform, f"{platform or 'missing'} — expected {expected_platform}")
    if sdk_name:
        sdk_version = re.sub(r"^[a-z]+", "", sdk_name)
        good = version_tuple(sdk_version) >= version_tuple(required_sdk)
        r.expect(good, "SDK", f"{sdk_name} (App Store minimum: iOS {required_sdk} SDK), Xcode build {info.get('DTXcode', '?')} ({info.get('DTXcodeBuild', '?')})",
                 f"{sdk_name} — App Store Connect requires the iOS {required_sdk} SDK or later")
        # Apps built with the iOS 27 SDK or later must use the UIScene life cycle or iOS stops them at launch (TN3187).
        # Avalonia 11 (the UI framework) has no scene support; Avalonia 12 adds it.
        if version_tuple(sdk_version) >= (27,):
            r.expect("UIApplicationSceneManifest" in info, "scene life cycle",
                     "UIApplicationSceneManifest present (required from the iOS 27 SDK)",
                     f"built with {sdk_name} without UIApplicationSceneManifest — iOS would not launch it. Build with the "
                     f"Xcode {toolchain('HcgRequiredXcode')} SDK (toolchain.props) until the app adopts UIScene")
    else:
        (r.fail if kind == "device" else r.warn)("SDK", "DTSDKName missing from Info.plist")

    # App icon: the asset catalog must be compiled and referenced; the source must be 1024 px and opaque.
    primary = info.get("CFBundleIcons", {}).get("CFBundlePrimaryIcon", {})
    ipad = info.get("CFBundleIcons~ipad", {}).get("CFBundlePrimaryIcon", {})
    r.expect(primary.get("CFBundleIconName") == "AppIcon" and ipad.get("CFBundleIconName") == "AppIcon", "icon (Info.plist)",
             "CFBundleIconName AppIcon for iPhone and iPad", f"CFBundleIcons {primary} / ~ipad {ipad}")
    car = app / "Assets.car"
    if not car.is_file():
        r.fail("icon (Assets.car)", "Assets.car missing — the AppIcon asset catalog was not compiled")
    else:
        res = tool("xcrun", "--sdk", "iphoneos", "assetutil", "--info", str(car))
        if res is None or res[0] != 0:
            r.info("icon (Assets.car)", f"present ({car.stat().st_size} bytes); assetutil not available to list it")
        else:
            entries = json.loads(res[1] or b"[]")
            icons = [e for e in entries if e.get("Name") == "AppIcon"]
            r.expect(bool(icons), "icon (Assets.car)", f"AppIcon compiled ({len(icons)} rendition(s))", "Assets.car has no AppIcon")
    source_icon = APP_SOURCE / "Assets.xcassets" / "AppIcon.appiconset" / "AppIcon-1024.png"
    png = png_info(source_icon) if source_icon.is_file() else None
    r.expect(png is not None and png[:2] == (1024, 1024) and not png[2], "icon (source)",
             "AppIcon-1024.png is 1024×1024 without alpha", f"{source_icon.name}: {png} — App Store icons must be 1024×1024 and opaque")

    # Launch screen.
    storyboard = info.get("UILaunchStoryboardName", "")
    compiled = app / f"{storyboard}.storyboardc"
    r.expect(storyboard == "LaunchScreen" and compiled.is_dir(), "launch screen", f"{compiled.name} compiled",
             f"UILaunchStoryboardName '{storyboard}', {compiled.name} present: {compiled.is_dir()}")

    # Privacy manifest.
    manifest = app / "PrivacyInfo.xcprivacy"
    if not manifest.is_file():
        r.fail("privacy manifest", "PrivacyInfo.xcprivacy missing from the app bundle")
    else:
        privacy = plistlib.loads(manifest.read_bytes())
        declared = {d.get("NSPrivacyAccessedAPIType", "").replace("NSPrivacyAccessedAPICategory", "")
                    for d in privacy.get("NSPrivacyAccessedAPITypes", [])}
        tracking = privacy.get("NSPrivacyTracking")
        collected = privacy.get("NSPrivacyCollectedDataTypes", [])
        r.expect(tracking is False and not privacy.get("NSPrivacyTrackingDomains"), "privacy manifest",
                 f"no tracking, {len(collected)} collected data type(s), required-reason APIs: {', '.join(sorted(declared))}",
                 f"NSPrivacyTracking {tracking}, domains {privacy.get('NSPrivacyTrackingDomains')}")
        if executable.is_file():
            binary = executable.read_bytes()
            for category, marker in (("UserDefaults", b"NSUserDefaults"), ("ActiveKeyboards", b"activeInputModes")):
                if marker in binary and category not in declared:
                    # App Store Connect rejects such uploads (ITMS-91053); on a simulator build it is only a warning.
                    (r.fail if kind == "device" else r.warn)(
                        "privacy manifest", f"the executable uses {marker.decode()} but NSPrivacyAccessedAPICategory{category} is not "
                                            "declared in PrivacyInfo.xcprivacy — App Store Connect rejects this (ITMS-91053)")
                elif marker in binary:
                    r.ok("privacy manifest", f"{marker.decode()} used and {category} declared")

    schemes = [s for t in info.get("CFBundleURLTypes", []) for s in t.get("CFBundleURLSchemes", [])]
    r.expect(f"msauth.{BUNDLE_ID}" in schemes, "URL scheme", f"msauth.{BUNDLE_ID} (Microsoft sign-in)", f"msauth.{BUNDLE_ID} missing: {schemes}")
    if "ITSAppUsesNonExemptEncryption" in info:
        r.info("export compliance", f"ITSAppUsesNonExemptEncryption = {info['ITSAppUsesNonExemptEncryption']}")
    else:
        r.info("export compliance", "ITSAppUsesNonExemptEncryption not set — App Store Connect asks about encryption for each build")

    check_signature(r, app, kind, args.app_store)
    print("Result:", "OK" if r.failed == 0 else f"{r.failed} check(s) failed")
    return 1 if r.failed else 0


def entitlements_of(app):
    for args in (["codesign", "-d", "--entitlements", "-", "--xml", str(app)], ["codesign", "-d", "--entitlements", ":-", str(app)]):
        res = tool(*args)
        if res is None:
            return None
        if res[0] == 0 and res[1].strip():
            try:
                return plistlib.loads(res[1])
            except Exception:
                continue
    return {}


def check_signature(r, app, kind, app_store):
    signed = (app / "_CodeSignature" / "CodeResources").is_file()
    if not signed:
        if app_store:
            r.fail("signature", "not signed — App Store builds come from scripts/export-ipa.sh with a distribution identity")
        else:
            r.info("signature", "unsigned build (compile check): signature and entitlements are verified on the signed build by export-ipa.sh")
        return
    res = tool("codesign", "--verify", "--deep", "--strict", "--verbose=2", str(app))
    if res is None:
        r.info("signature", "signed; codesign not available to verify it")
        return
    r.expect(res[0] == 0, "signature", "valid (codesign --verify --deep --strict)", res[2].strip().splitlines()[-1] if res[2].strip() else "invalid")
    details = tool("codesign", "-dvv", str(app))
    authority = [l.split("=", 1)[1] for l in (details[2] if details else "").splitlines() if l.startswith("Authority=")]
    if app_store:
        r.expect(bool(authority) and authority[0].startswith(("Apple Distribution", "iPhone Distribution")), "signing identity",
                 authority[0] if authority else "", f"{authority[0] if authority else 'ad-hoc'} — App Store builds need an Apple Distribution identity")
    else:
        r.info("signing identity", authority[0] if authority else "ad-hoc (simulator)")

    ent = entitlements_of(app)
    if ent is None:
        return
    if kind == "simulator":
        r.info("entitlements", f"simulator signature carries: {', '.join(sorted(ent)) or 'none'} (device builds are checked in full)")
        return
    app_id = ent.get("application-identifier", "")
    team = app_id.split(".", 1)[0] if "." in app_id else ""
    r.expect(app_id == f"{team}.{BUNDLE_ID}" and team, "app identifier", app_id, f"application-identifier '{app_id}'")
    expected_groups = [f"{team}.{BUNDLE_ID}", f"{team}.{MSAL_KEYCHAIN_GROUP}"]
    r.expect(ent.get("keychain-access-groups") == expected_groups, "Keychain groups", ", ".join(expected_groups),
             f"{ent.get('keychain-access-groups')} — expected {expected_groups} (Entitlements.plist)")
    unexpected = sorted(set(ent) - ALLOWED_ENTITLEMENTS)
    r.expect(not unexpected, "entitlements", "only application identifier, team, Keychain groups", f"unexpected: {unexpected}")
    if app_store:
        r.expect(ent.get("get-task-allow") is False, "get-task-allow", "false (distribution)", f"{ent.get('get-task-allow')} — a development-signed build cannot be uploaded")
    profile_path = app / "embedded.mobileprovision"
    if not profile_path.is_file():
        (r.fail if app_store else r.warn)("profile", "embedded.mobileprovision missing")
        return
    profile = decode_profile(profile_path)
    if profile is None:
        r.info("profile", "embedded; `security` not available to decode it")
        return
    check_profile(r, profile, team, app_store, "profile")


# ------------------------------------------------------------------------------------------------------------ profile

PROFILE_DIRS = [pathlib.Path.home() / "Library/MobileDevice/Provisioning Profiles",
                pathlib.Path.home() / "Library/Developer/Xcode/UserData/Provisioning Profiles"]


def find_profile(name_or_uuid):
    given = pathlib.Path(name_or_uuid).expanduser()
    if given.is_file():
        return given, decode_profile(given)
    for d in PROFILE_DIRS:
        for f in sorted(d.glob("*.mobileprovision")) if d.is_dir() else []:
            p = decode_profile(f)
            if p and name_or_uuid in (p.get("UUID"), p.get("Name")):
                return f, p
    return None, None


def check_signing_setup(args):
    r = Report(f"Signing setup for {BUNDLE_ID}")
    if shutil.which("security") is None:
        r.fail("host", "the `security` tool is missing — run this on the Mac that signs the app")
        return 1
    # Identity: present in the keychain with its private key, and its team.
    res = tool("security", "find-identity", "-v", "-p", "codesigning")
    identities = re.findall(r'\)\s+([0-9A-F]{40})\s+"([^"]+)"', res[1].decode() if res else "")
    match = [(sha, name) for sha, name in identities if args.identity in (sha, name)]
    if not match:
        r.fail("identity", f"\"{args.identity}\" is not a valid code-signing identity in this Mac's keychain "
                           f"(available: {', '.join(n for _, n in identities) or 'none'}). Install the certificate with its private key.")
        return 1
    sha, name = match[0]
    team = (re.search(r"\(([A-Z0-9]{10})\)\s*$", name) or [None, ""])[1]
    r.ok("identity", f"{name} ({sha[:8]}…)")
    if args.app_store:
        r.expect(name.startswith(("Apple Distribution", "iPhone Distribution")), "identity type", "distribution",
                 "an App Store build needs an \"Apple Distribution\" identity")

    path, profile = find_profile(args.profile)
    if profile is None:
        r.fail("profile", f"no installed provisioning profile named or with UUID \"{args.profile}\" "
                          f"(looked in {', '.join(str(d) for d in PROFILE_DIRS)}). Download it in Xcode › Settings › Accounts › Download Manual Profiles.")
        return 1
    r.ok("profile", f"\"{profile.get('Name')}\" {profile.get('UUID')} ({path.name})")
    check_profile(r, profile, team, args.app_store, "profile")
    certificates = {hashlib.sha1(c).hexdigest().upper() for c in profile.get("DeveloperCertificates", [])}
    r.expect(sha in certificates, "certificate", "the profile contains the identity's certificate",
             "the profile was made for a different certificate — regenerate it with this certificate selected")
    print("Result:", "OK — ready to sign" if r.failed == 0 else f"{r.failed} check(s) failed — fix them before building")
    return 1 if r.failed else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    a = sub.add_parser("app")
    a.add_argument("path")
    a.add_argument("--kind", choices=["device", "simulator"])
    a.add_argument("--app-store", action="store_true")
    a.add_argument("--marketing-version")
    a.add_argument("--build-number")
    p = sub.add_parser("profile")
    p.add_argument("--profile", required=True)
    p.add_argument("--identity", required=True)
    p.add_argument("--app-store", action="store_true")
    args = parser.parse_args()
    sys.exit(check_app(args) if args.command == "app" else check_signing_setup(args))


if __name__ == "__main__":
    main()
