#!/usr/bin/env python3
"""Finds the development signing for installing HCG Mail on one device (used by install-device.sh).

    security find-identity -v -p codesigning | python3 -I scripts/find-dev-signing.py <bundle id> <device UDID>

Picks an Apple Development identity from the keychain listing on stdin and an installed development profile for
<TEAMID>.<bundle id> that contains that identity's certificate and the device, and has not expired (the one valid
longest). HCG_IOS_DEV_IDENTITY / HCG_IOS_DEV_PROFILE restrict the choice. Prints three lines: identity name, profile
UUID, profile name. Exits 1 with the reason if there is none.
"""
import datetime
import hashlib
import os
import pathlib
import plistlib
import re
import subprocess
import sys

PROFILE_DIRS = [pathlib.Path.home() / "Library/Developer/Xcode/UserData/Provisioning Profiles",
                pathlib.Path.home() / "Library/MobileDevice/Provisioning Profiles"]


def main():
    bundle, udid = sys.argv[1], sys.argv[2]
    want_id = os.environ.get("HCG_IOS_DEV_IDENTITY", "")
    want_profile = os.environ.get("HCG_IOS_DEV_PROFILE", "")

    ids = [(sha, name) for sha, name in re.findall(r'\)\s+([0-9A-F]{40})\s+"([^"]+)"', sys.stdin.read())
           if name.startswith(("Apple Development", "iPhone Developer")) and (not want_id or want_id in (sha, name))]
    if not ids:
        print("error: no Apple Development certificate (with its private key) in this Mac's keychain"
              + (f' matching "{want_id}"' if want_id else ""), file=sys.stderr)
        return 1

    given = pathlib.Path(want_profile).expanduser() if want_profile else None
    files = [given] if given and given.is_file() else [f for d in PROFILE_DIRS if d.is_dir() for f in d.glob("*.mobileprovision")]
    now = datetime.datetime.now(datetime.timezone.utc)
    best, candidates = None, []
    for f in files:
        out = subprocess.run(["security", "cms", "-D", "-i", str(f)], capture_output=True).stdout
        if not out:
            continue
        p = plistlib.loads(out)
        if want_profile and not (given and given.is_file()) and want_profile not in (p.get("Name"), p.get("UUID")):
            continue
        ent = p.get("Entitlements", {})
        team = (p.get("TeamIdentifier") or [""])[0]
        if ent.get("application-identifier") != f"{team}.{bundle}" or not ent.get("get-task-allow"):
            continue
        reasons = []
        expires = p.get("ExpirationDate")
        if isinstance(expires, datetime.datetime):
            expires = expires if expires.tzinfo else expires.replace(tzinfo=datetime.timezone.utc)
            if expires < now:
                reasons.append("expired")
        if udid not in (p.get("ProvisionedDevices") or []) and not p.get("ProvisionsAllDevices"):
            reasons.append("does not include this device")
        certs = {hashlib.sha1(c).hexdigest().upper() for c in p.get("DeveloperCertificates", [])}
        match = [name for sha, name in ids if sha in certs]
        if not match:
            reasons.append("made for another certificate")
        candidates.append(f'"{p.get("Name")}": ' + (", ".join(reasons) if reasons else "usable"))
        if not reasons and (best is None or expires > best[3]):
            best = (match[0], p.get("UUID"), p.get("Name"), expires)

    if best is None:
        print(f"error: no usable development profile for {bundle}"
              + (" — found: " + "; ".join(candidates) if candidates else " is installed"), file=sys.stderr)
        return 1
    print(best[0])
    print(best[1])
    print(best[2])
    return 0


if __name__ == "__main__":
    sys.exit(main())
