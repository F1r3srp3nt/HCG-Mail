# HCG Mail 0.1.0 (1): App Store Connect submission package

Everything that is entered in App Store Connect for the first version, in the order the pages ask for it.

* Nothing here uploads, submits or publishes. Each step is done by HCG in App Store Connect.
* **⟨HCG to supply⟩** marks a value only HCG can give. Those values are not invented here.

| # | App Store Connect | Where the answer is | Status |
|---|---|---|---|
| 1 | **Apps → + New App**: platform iOS, name, primary language English (U.S.), bundle ID `com.hakmicapitalgroup.hcgmail`, SKU | `metadata/en-US/name.txt`; SKU suggestion `hcg-mail-ios` | ready |
| 2 | **App Information**: subtitle, category | `metadata/en-US/subtitle.txt`; Productivity (secondary: Business) | ready |
| 3 | **App Information**: age rating questionnaire | [APP_STORE_CONNECT.md §4](../APP_STORE_CONNECT.md#4-age-rating--guidance) | answer truthfully; Apple computes the rating |
| 4 | **App Privacy**: privacy policy URL, data collection | [APP_STORE_CONNECT.md §3](../APP_STORE_CONNECT.md#3-app-privacy-nutrition-label--verified-data-flows): "Data Not Collected", with the conditions listed there | URL **⟨HCG to supply⟩**; answer is HCG's decision |
| 5 | **Pricing and Availability** | Free / paid and countries | **⟨HCG to decide⟩** |
| 6 | **Version 0.1.0 → Screenshots** | `screenshots/` in the package zip (see below) | ready: 3 × iPhone 6.9″, 3 × iPad 13″ |
| 7 | **Version 0.1.0 → Promotional text, Description, Keywords** | `metadata/en-US/promotional_text.txt`, `description.txt`, `keywords.txt` | ready (CI-checked) |
| 8 | **Version 0.1.0 → Support URL, Marketing URL** | | **⟨HCG to supply⟩** (marketing optional) |
| 9 | **Version 0.1.0 → Copyright** | `metadata/en-US/copyright.txt` | confirm the legal entity name |
| 10 | **Version 0.1.0 → Build** | the `.ipa` from `scripts/export-ipa.sh`, uploaded **by you** with Transporter ([APP_STORE_SUBMISSION.md §2–3](../APP_STORE_SUBMISSION.md#2-build-the-signed-archive-and-ipa-on-your-mac)) | not built yet: needs the distribution certificate |
| 11 | **TestFlight → export compliance** (asked for the build) | [APP_STORE_CONNECT.md §5](../APP_STORE_CONNECT.md#5-export-compliance--checklist) | HCG's legal decision |
| 12 | **TestFlight → internal testing** on physical devices | [TESTFLIGHT_PLAN.md](../TESTFLIGHT_PLAN.md) | not started |
| 13 | **Version 0.1.0 → App Review Information**: sign-in required, demo mailbox, contact, notes | `metadata/en-US/review_notes.txt`; demo mailbox fields in [APP_STORE_CONNECT.md §6](../APP_STORE_CONNECT.md#6-app-review-information) | demo mailbox and contact **⟨HCG to supply⟩** |
| 14 | **Version Release**: manual release after approval is recommended for the first version | | HCG's choice |
| 15 | **Add for Review → Submit** | final checklist: [APP_STORE_CONNECT.md §7](../APP_STORE_CONNECT.md#7-before-submit-for-review-checklist) | **only with the founder's approval** |

## Text fields

The files in `metadata/en-US/` are the exact text to paste. CI runs `scripts/check-store-metadata.py` on them on
every change, checking:
* Apple's limits;
* no placeholders;
* no mention of Microsoft or Google sign-in, which this build hides.

To change a field, edit its file and let CI check it. Then paste the file's whole content into App Store Connect.

## Screenshots

* **What they are:** real captures of the app in preview mode with its labelled sample mailbox, made by the CI job
  *App Store screenshots*. They show three screens on each device:

  | Order | iPhone 6.9″ (1320 × 2868) | iPad 13″ (2064 × 2752) |
  |---|---|---|
  | 1 | `iPhone17ProMax-inbox.png` | `iPadPro13inchM5-inbox.png` |
  | 2 | `iPhone17ProMax-reader.png` | `iPadPro13inchM5-reader.png` |
  | 3 | `iPhone17ProMax-compose.png` | `iPadPro13inchM5-compose.png` |

* **Where they come from:** release-candidate run
  [45068033617](https://github.com/F1r3srp3nt/HCG-Mail/actions/runs/45068033617), artifact
  `app-store-screenshots`, kept until 20 Feb 2027. Each image was taken only after the app reported that screen
  open.
* **The package copies:** the same pixels with the unused alpha channel removed (opaque RGB), so App Store Connect
  cannot object to transparency. `screenshots/SHA256SUMS` lists their checksums.
* **Retaking them:** run the *iOS* workflow manually (Actions → iOS → *Run workflow*), or open a `release/*` pull
  request.
* **Not committed:** screenshots are build output, so they are not in the repository.

## Not in this package

* Signing material: certificates, profiles and passwords never enter the repository.
* The App Store `.ipa`: built only on your Mac (`scripts/export-ipa.sh`).
* Upload, TestFlight distribution and *Submit for Review*: done by you, manually.
