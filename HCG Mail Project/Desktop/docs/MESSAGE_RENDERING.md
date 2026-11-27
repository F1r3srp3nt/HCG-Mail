# Message rendering

## Missing verification codes — investigation (2026-11-27)

**Report:** on a physical device, a NOR-SHIPPING 2027 verification email showed its branding and the HTML around it,
but not the verification code. **That message was not available to the investigation.** No root cause is claimed for
that specific email until its source is checked; see *Confirming with the original message* below.

**Method.** I wrote 38 sanitized reproductions of how transactional mail typically presents a code: invented sender,
code `482913`, no personal data. Each was rendered through the real pipeline: MIME → sanitizer → reader → HtmlRenderer,
captured headlessly in Original and Readable view, and run through the plain-text extraction. A code counted as
visible when the rendering with the code differed from a rendering with a different code in the same place. Every
hypothesis in the report was covered: sanitization, CSS removal, colour inheritance, dark-mode rules, MIME, encoding,
unsupported elements, conditional comments and inline content.

**Constructs that lost the code (reproduced, all fixed):**

| Construct | Stage | What happened |
|---|---|---|
| Code in `<button>`, `<input value>`, `<textarea>` | Sanitizer | The controls were removed *with their content*, in every view including Plain text |
| White code on a `background:` shorthand (cell, `<a>` "button") | Renderer | HtmlRenderer does not paint the shorthand: white on white. The branding stayed visible |
| White code on a gradient | Renderer | Gradients are not painted |
| White code on `<td background="https://…">` | Sanitizer | The attribute was removed without the dark stand-in that blocked CSS background images get |
| White text with no backdrop | — | Only a hint was shown; the text stayed invisible |
| Digits in `display:flex` / `grid` boxes | Renderer | Nothing inside flex/grid is drawn |
| Any styling in a `<style>` block | Sanitizer | All ordinary style rules were dropped (HtmlSanitizer requires them to be allowed explicitly), so class-styled mail lost its layout and colours |
| Dark-mode rules `@media (prefers-color-scheme: dark)` | Renderer | Once style rules were kept, HtmlRenderer applied every `@media` block unconditionally |

**Not causes** in these reproductions: MIME structure, quoted-printable/base64, charsets, `rgba()`/`hsl()`/8-digit/named
colours, `!important`, CSS variables, Outlook conditional comments, HTML5 sectioning elements, `bgcolor` cells,
`<font color>`, and table digit boxes all rendered the code.

**Fixes** (`MailHtmlSanitizer`, `FormControls`, `RendererCompatibility`):
* **Form controls** become inert text before sanitizing. A button's content and style become a span; a text-like input's
  value, a textarea's text and a select's chosen option become text. Hidden, password, checkbox and file inputs reveal
  nothing. Forms, actions, names and handlers are still removed, and nothing can be typed or submitted.
* **Backdrops the renderer cannot paint** get an explicit `background-color`, appended after the sender's declaration:
  the colour from the shorthand, or a gradient's first colour stop. Flex and grid become block/inline-block layouts. In
  `<style>` rules this is done with a follow-up rule using the same selector. Nothing is loaded or rewritten.
* **`<style>` rules are kept.** They allow the same properties as inline styles; `url()` loads are filtered, and
  `@import`/`@font-face` stay blocked. `@media` rules apply only when the reading surface matches them. Dark-mode and
  print rules never apply. Phone breakpoints apply on the iPhone (laid out at 420 px) but not in the desktop pane
  (800 px).
* **Legibility net:** contrast is measured against the backdrop the renderer really paints. Text under 1.5:1 gets a
  stand-in backdrop, and the *low contrast — Readable view* hint is shown. Colours are not inverted.

**Regression tests:**
* `App.Tests/TransactionalContentRenderingTests`: 18 constructs × Original and Readable must show the code, and the code
  must survive sanitizing and Plain-text extraction. 13 of these fail on the previous code.
* `Tests/Email/TransactionalContentSanitizerTests`: the security boundary (no forms, no submission, no revealed hidden
  or password values, no remote CSS) and the `@media` behaviour.

### Confirming with the original message

Ask the recipient to forward the original as an attachment, or save it as `.eml`. Replace the code, names and
addresses with invented values, but keep the HTML structure and CSS exactly. Add it to `tests/fixtures/mail/`:
`ReaderAndRenderingTests` will render it in every view and skin. If the code still does not show, the construct is new
and goes into `TransactionalContentRenderingTests`.

## Earlier diagnosis and fixes (2026-11-12)

The screenshots referred to in the request were not available in the development session, so the causes below were
established by reproducing each reported symptom with synthetic, privacy-safe fixtures (`tests/fixtures/mail/*.eml`,
invented senders on example.com/.test) pushed through the real pipeline — MimeKit parsing → body selection →
HtmlSanitizer/AngleSharp → reader view model → the actual `MessageBodyView` (HtmlRenderer) in both skins, captured
headlessly — and, for previews, by loading the fixtures into the local Dovecot server and fetching them as HCG Mail does.

## Root causes found

| Symptom | Root cause (reproduced) | Fix |
|---|---|---|
| **Blank body although the list shows a preview** | (1) Body selection always preferred an HTML part. Some mailers send an HTML alternative that shows nothing (empty body, comments, hidden preheader) next to a real plain-text part; HCG Mail rendered the empty HTML (fixture 08: 0 % ink). (2) Image-only promotions with remote images blocked rendered as an empty white box: blocked images showed nothing, not even their alt text (fixture 07). (3) A whitespace-only body showed a blank pane instead of an "empty" state (fixture 09). (4) Any exception other than a mail error while opening a message left no content, no error and no placeholder. | HTML wins only when it has visible content (parsed with AngleSharp, hidden elements ignored); blocked images become labelled placeholders ("Image: <alt>"), tracking pixels disappear; genuinely empty messages say so; every failure becomes an error state with **Retry**. |
| **Extremely faint text** | Text the sender coloured white/very light that relied on a background the reader did not draw: a remote background image (blocked) or a dark-mode-only design (fixture 11: white text invisible). Also the app's tinted "paper" colour (#F6F8FA in HCG Future) replaced the email's own page background. `prefers-color-scheme: dark` rules were **not** the cause: HtmlRenderer does not apply them (verified, fixture 06). | Where a blocked background image was the backdrop of light text, a dark stand-in colour is drawn; the page behind a message is the email's own `<body>` colour or white — never the app theme; light text without any background triggers a hint offering **Readable view**. Colours are never inverted. |
| **Raw links / formatting fragments in previews** | Previews were the server's snippet (IMAP PREVIEW) or MailKit's fallback — taken verbatim from the sender's plain-text part: tracking URLs, `[image: …]` placeholders, invisible preheader padding (U+034F, U+200C…), `**`/`-----` separators, and raw `<p>` markup from a mislabelled text/plain part. MailKit's fallback also produced an empty preview for 8-bit HTML without a declared charset. | `PreviewText.Clean` converts markup with the HTML parser, decodes entities, and removes URLs, image placeholders, invisible characters and separator runs; used for IMAP and Microsoft Graph previews. |

MIME handling itself (nested multipart/mixed → alternative → related, quoted-printable, base64, ISO-8859-1, UTF-8,
RFC 2047 subjects, CID images with different letter case, charset only in `<meta>`) was verified correct with
MimeKit and is covered by tests. Selection races were already guarded by a version counter; this is now tested.

## Presentation and isolation

* **Original** (default): the sender's layout, sanitized. **Readable**: same text, images and links with the
  sender's colours, backgrounds, fonts and `<style>` removed — dark text on white. **Plain text**: the text body.
  Switch per message above its text; default in Settings → Mail → Message display.
* If the HTML engine reports a document error, the reader switches to plain text and says so; choosing Original or
  Readable tries again.
* Untrusted content: scripts, event handlers, forms, frames, objects, SVG, `<base>`, meta refresh, `javascript:`
  and `file:` URLs are removed by the allow-list sanitizer (HtmlSanitizer + AngleSharp, no regex HTML parsing).
  HtmlRenderer has no script engine. Every image and stylesheet load is intercepted by HCG Mail: CID and safe
  `data:` images are drawn from the message itself; remote images load only after **Load remote images** (or the
  opt-in setting), via a client without cookies, image types only, size/time limits; remote stylesheets,
  `@import`, fonts and CSS backgrounds are never loaded. Links open in the system browser only after a click and a
  confirmation showing the real destination.

## Tests

`Email/RenderingPipelineTests` (MIME/body selection per fixture, sanitizer policy, Readable mode, stand-in
backgrounds, canvas colour, previews), `ReaderAndRenderingTests` (all 13 fixtures × 2 skins × 3 modes must render
visible ink or an explicit state; rapid switching; retry; render and sanitizer failure fallbacks; empty messages;
remote images; server search), E2E `Inbox_previews_are_clean_text_…` on Dovecot. Screenshots of every rendering are
written when `HCG_MAIL_SCREENSHOT_DIR` is set.

## Not verified

The specific messages in the original screenshots were not available; if one still renders badly, save it as `.eml`
(remove personal data), add it to `tests/fixtures/mail` and it will be checked by the same tests.
