#!/usr/bin/env python3
"""Checks the App Store Connect text in AppStoreConnect/metadata/<locale>/ before it is pasted into App Store Connect:
Apple's length limits, no placeholders, nothing advertised that this build does not offer.

    python3 -I scripts/check-store-metadata.py            # exit 1 if a check fails
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent / "AppStoreConnect" / "metadata"

# App Store Connect limits (characters).
FIELDS = {
    "name.txt": ("App name", 30, True),
    "subtitle.txt": ("Subtitle", 30, True),
    "promotional_text.txt": ("Promotional text", 170, False),
    "description.txt": ("Description", 4000, True),
    "keywords.txt": ("Keywords", 100, True),
    "review_notes.txt": ("App Review notes", 4000, False),
    "copyright.txt": ("Copyright", 200, True),
}
# Sign-in providers this build hides (SignInPresentation): the store text must not advertise them, and other
# companies' trademarks do not belong in keywords.
NOT_IN_THIS_BUILD = re.compile(r"\b(microsoft|outlook|office ?365|exchange|google|gmail)\b", re.IGNORECASE)
PLACEHOLDER = re.compile(r"⟨|⟩|<[A-Z][A-Za-z ]*>|\bTODO\b|\bTBD\b|lorem ipsum", re.IGNORECASE)


def main():
    failed = 0
    locales = sorted(p for p in ROOT.iterdir() if p.is_dir()) if ROOT.is_dir() else []
    if not locales:
        print(f"FAIL  no locales in {ROOT}")
        return 1
    for locale in locales:
        print(f"{locale.name}:")
        for file, (label, limit, required) in FIELDS.items():
            path = locale / file
            if not path.is_file():
                status = "FAIL" if required else "INFO"
                failed += required
                print(f"  {status:5} {label:18} missing ({path.name})")
                continue
            text = path.read_text(encoding="utf-8").rstrip("\n")
            problems = []
            if len(text) > limit:
                problems.append(f"{len(text)} characters, limit {limit}")
            if not text.strip():
                problems.append("empty")
            if PLACEHOLDER.search(text):
                problems.append(f"placeholder: {PLACEHOLDER.search(text).group(0)!r}")
            if file != "review_notes.txt" and NOT_IN_THIS_BUILD.search(text):
                problems.append(f"mentions {NOT_IN_THIS_BUILD.search(text).group(0)!r}, which this build does not offer")
            if any(line != line.rstrip() for line in text.splitlines()):
                problems.append("trailing spaces")
            if file == "keywords.txt":
                words = [w.strip() for w in text.split(",")]
                if "\n" in text or any(not w for w in words):
                    problems.append("must be one comma-separated line without empty entries")
                if len({w.lower() for w in words}) != len(words):
                    problems.append("repeated keyword")
            if problems:
                failed += 1
                print(f"  FAIL  {label:18} " + "; ".join(problems))
            else:
                print(f"  OK    {label:18} {len(text)}/{limit}")
    print("Result:", "OK" if failed == 0 else f"{failed} problem(s)")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
