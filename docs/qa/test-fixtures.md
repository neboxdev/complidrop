# Test fixtures — what files you need before QA

The QA plan asks you to upload many kinds of files. This page lists every fixture, where to put it, and how to fabricate the synthetic ones.

## Folder layout

Create a local-only folder (DO NOT commit) for the fixtures:

```
C:/NewStart/Product/complidrop-qa-fixtures/
├── happy-path/
│   ├── sample-coi.pdf                 # clean, machine-printed COI (ACORD 25)
│   ├── sample-license.pdf             # state contractor or trade license
│   └── sample-permit.pdf              # building / operational permit
│
├── extraction-edge/
│   ├── photo-of-coi.jpg               # phone photo of a printed COI (tilted, glare)
│   ├── sample-photo.heic              # iPhone HEIC/HEIF photo (#220) — portal accepts + transcodes to JPEG
│   ├── scanned-coi.pdf                # 200dpi scan
│   ├── handwritten-license.jpg        # mostly-printed but a few handwritten fields
│   ├── multi-page-coi.pdf             # 3+ pages, fields on page 2
│   ├── rotated-coi.pdf                # 90° rotated
│   └── low-confidence.pdf             # blurry / watermark over text — should trigger the "Needs your review" (ManualRequired) path
│
├── validation-edge/
│   ├── tiny.pdf                       # < 1 KB, valid PDF magic bytes
│   ├── exactly-10mb.pdf               # 10485760 bytes (the boundary)
│   ├── over-10mb.pdf                  # 10485761+ bytes (must reject)
│   ├── fake.pdf                       # actually a .txt file renamed to .pdf
│   ├── fake.jpg                       # actually a .pdf renamed to .jpg
│   ├── empty.pdf                      # 0 bytes
│   ├── png-real.png                   # real PNG
│   ├── jpeg-real.jpg                  # real JPEG
│   └── docx-disallowed.docx           # Word doc — must reject (not in allow-list)
│
└── stress/
    └── 25-mixed-docs/                 # 25 COIs/licenses for a bulk-upload test
```

## Where to get them

### Happy-path fixtures (real shapes, real data)

ACORD 25 (COI) samples are widely distributed:

- **Filled-out ACORD 25 sample** — Google `"ACORD 25 sample filled"` and pick a downloadable PDF. The form is a public standard.
- **Sample state license** — your state's contractor licensing board usually publishes a sample. Search `"sample [state] contractor license PDF"`.
- **Sample permit** — most municipalities show a sample permit format. Search `"sample building permit PDF"`.

Sanitize before saving: open in any PDF tool, replace personal names/numbers with `JOHN DOE` / `LICENSE 12345` / etc. The fixtures are local-only but treat them as if they could leak.

If finding clean samples is taking more than 15 minutes, fall back to the synthetic generation below — extraction quality may differ, but you'll exercise every UI surface either way.

### Extraction-edge fixtures (force interesting failure modes)

- **`photo-of-coi.jpg`** — print one of your happy-path COIs on paper, take a phone photo at a slight angle with a window glare in the corner. Resize to ~2000px wide.
- **`scanned-coi.pdf`** — print + scan one of your happy-path COIs at 200 dpi.
- **`handwritten-license.jpg`** — print a sample license, fill in 1–2 fields by hand with a black pen, photograph.
- **`multi-page-coi.pdf`** — combine two PDFs into one using `pdfunite` (Linux) or [pdfjoiner.com] / Acrobat. Or use the @anthropic-skills:pdf skill: ask Claude to "combine two PDFs from happy-path/ into a 3-page doc with the COI as pages 2–3".
- **`rotated-coi.pdf`** — open a PDF, rotate 90° clockwise, save.
- **`low-confidence.pdf`** — open a PDF in an editor, add a thick gray watermark across the body, lower contrast. The goal is to make the LLM return `NeedsReprocessing = true` and trigger the **"Needs your review"** (ManualRequired) path.
- **`sample-photo.heic`** — an iPhone-format photo for the HEIC end-to-end test (§6.9.4, #220). Easiest source: **copy the committed test fixture** at `api/CompliDrop.Api.Tests/TestFixtures/sample-photo.heic`. To make your own, take a photo on an iPhone with **Settings → Camera → Formats → High Efficiency** (the default), or generate one with Python `pillow-heif`:
  ```python
  # pip install pillow pillow-heif
  from PIL import Image
  import pillow_heif
  pillow_heif.register_heif_opener()
  Image.open("extraction-edge/photo-of-coi.jpg").save("extraction-edge/sample-photo.heic")
  ```
  Note: HEIC is accepted only via the **vendor portal** (§6). The dashboard dropzone still filters it client-side (§4.6.4 / §16.4) — that's expected, not a fixture problem.

### Validation-edge fixtures (force the file-validation gates)

Use PowerShell to fabricate the size-boundary ones — no LLM needed, just bytes.

```powershell
# Create a valid PDF of exactly 10 MB (10485760 bytes)
$pdfHeader = [byte[]](0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A)  # "%PDF-1.4\n"
$padding   = New-Object byte[] (10485760 - $pdfHeader.Length)
[System.IO.File]::WriteAllBytes("C:\NewStart\Product\complidrop-qa-fixtures\validation-edge\exactly-10mb.pdf", $pdfHeader + $padding)

# Create one byte over the limit
$padding   = New-Object byte[] (10485761 - $pdfHeader.Length)
[System.IO.File]::WriteAllBytes("C:\NewStart\Product\complidrop-qa-fixtures\validation-edge\over-10mb.pdf", $pdfHeader + $padding)

# Fake PDF (actually plain text)
"This is not a PDF, even though the extension says it is." | Out-File -Encoding ascii "C:\NewStart\Product\complidrop-qa-fixtures\validation-edge\fake.pdf"

# Empty PDF
New-Item -ItemType File -Force "C:\NewStart\Product\complidrop-qa-fixtures\validation-edge\empty.pdf" | Out-Null

# Tiny but valid PDF (~50 bytes — just enough magic + EOF)
$tinyPdf = [byte[]](0x25,0x50,0x44,0x46,0x2D,0x31,0x2E,0x34,0x0A,0x25,0xE2,0xE3,0xCF,0xD3,0x0A,0x25,0x25,0x45,0x4F,0x46)
[System.IO.File]::WriteAllBytes("C:\NewStart\Product\complidrop-qa-fixtures\validation-edge\tiny.pdf", $tinyPdf)

# Fake JPG (actually a PDF renamed)
Copy-Item "C:\NewStart\Product\complidrop-qa-fixtures\happy-path\sample-coi.pdf" `
          "C:\NewStart\Product\complidrop-qa-fixtures\validation-edge\fake.jpg"
```

For `docx-disallowed.docx`: open Word, write "test", save as .docx. Or download any public sample .docx. The point is the magic bytes are `50 4B 03 04` (ZIP container), which the file-validation service must reject.

For `png-real.png` and `jpeg-real.jpg`: any screenshot or photo from your phone works. Just confirm the file size is < 10 MB.

### Stress fixture (bulk upload)

For `stress/25-mixed-docs/`, simplest approach: duplicate your happy-path COIs:

```powershell
$src = "C:\NewStart\Product\complidrop-qa-fixtures\happy-path\sample-coi.pdf"
$dst = "C:\NewStart\Product\complidrop-qa-fixtures\stress\25-mixed-docs"
New-Item -ItemType Directory -Force $dst | Out-Null
1..25 | ForEach-Object {
    Copy-Item $src "$dst\coi-bulk-$($_.ToString("00")).pdf"
}
```

This is sufficient for testing the upload UI and the queue's `FOR UPDATE SKIP LOCKED` behavior under load. Real diversity isn't needed for the user-facing test.

## Stripe test cards

Three numbers cover everything you'll touch in §10 (billing):

| Number | Behavior |
|---|---|
| `4242 4242 4242 4242` | Always succeeds. Default happy-path. |
| `4000 0025 0000 3155` | Requires 3D Secure authentication. Tests the "popup challenge" UX. |
| `4000 0000 0000 9995` | Declined for insufficient funds. Tests the error toast. |
| `4000 0000 0000 0341` | Card succeeds but later subscription invoice fails. Tests `past_due` status banner. |

For all four: any future expiry, any 3-digit CVC, any zip code.

If Stripe is in **live mode** for some reason, stop and switch to test mode before §10. Live mode uses real money.

## Test email accounts

You need at least two email addresses you can read in real time:

| Role | Suggested account | Why |
|---|---|---|
| **Test admin A** | `ruben+qaA@yourdomain` (Gmail+alias) or a dedicated test inbox | Receives reminder emails, Stripe receipts |
| **Test admin B** | `ruben+qaB@yourdomain` | For §13 multi-tenancy: separate org, must NEVER see A's emails |
| **Test vendor** | `ruben+vendor@yourdomain` | Vendor portal recipient — the "external contractor" persona |

Gmail's `+suffix` aliasing routes everything to the same inbox, so one Gmail account covers all three. Just filter by `to:` in the inbox.

If you want a more aggressive isolation test, use three separate providers (Gmail, Outlook, ProtonMail) so an accidental cross-tenant leak is obviously visible.

## Browser setup before you start

- **Browser A** (Chrome or Firefox) — Profile: "QA Admin A". Cookies cleared. DevTools attached.
- **Browser B** (different browser, e.g. Edge or Firefox) — Profile: "QA Vendor / Admin B". This avoids cookie collisions when testing multi-tenant + portal-uploads-from-different-identity.
- **Mobile** — your real phone for §6 portal-on-mobile and §15 responsive checks.
- **DevTools** — keep Network + Console panels open the whole time. Console errors that don't surface in the UI are still bugs.

## What to throw away after

The fixtures folder is local-only. The test accounts can stay (you'll want them next time anyway). Delete the test Stripe customers from your Stripe Dashboard's test-mode customer list when you're done — it keeps the dashboard readable for the next session.
