# 0018. HEIC/HEIF uploads are transcoded to JPEG on ingest

- **Status:** accepted
- **Date:** 2026-06-05
- **Deciders:** Ruben G.

## Context

[#196](https://github.com/neboxdev/complidrop/issues/196) added a "take a photo" affordance to the
vendor portal (the file input now offers `image/*`, so iOS/Android surface the camera). But the
iPhone camera default ("High Efficiency") produces **HEIC**, and CompliDrop rejected it twice over:

- **Client:** react-dropzone's `accept` map listed only `image/jpeg`, `image/png`, `application/pdf`,
  so a HEIC was rejected in the browser. #196 shipped a copy mitigation ("switch to *Most Compatible*
  and re-shoot, or upload a PDF") so the vendor wasn't stuck.
- **Server:** `FileValidationService` only accepts PDF/JPEG/PNG magic bytes.

[#220](https://github.com/neboxdev/complidrop/issues/220) is to accept HEIC/HEIF end-to-end. The trap
the ticket calls out: simply accepting HEIC at the door would move the failure *downstream* into the
extraction pipeline, which is worse (a silent processing failure instead of a clear rejection). So the
real question is **what makes a HEIC actually readable by everything that touches a stored document**:

1. **Document AI OCR** (always-on first stage) sends the file's bytes + MIME to Google. Document AI's
   OCR processor does **not** accept HEIC/HEIF — it would error, and `ExtractionWorker` would retry to
   the cap and mark the document `Failed`.
2. **The LLM stage.** Gemini (the configured primary) *does* accept `image/heic`/`image/heif` inline;
   Anthropic (the pluggable alternate) does **not**.
3. **The browser preview.** The document detail page renders `BlobStorageUrl` in an `<img>`. No
   browser except Safari renders HEIC, so a stored-as-HEIC blob shows broken for most reviewers.

So HEIC is consumable *directly* by exactly one of the three consumers, and only while the provider
stays Gemini. That is too fragile to lean on.

## Decision

**Transcode HEIC/HEIF to JPEG once, on ingest, before the file is stored** — the ticket's
"converted-on-ingest (simplest for the rest of the pipeline)" option. After the boundary, a HEIC
upload is indistinguishable from a JPEG upload to OCR, *any* LLM provider, the blob store, and the
browser preview. Nothing downstream needs a HEIC code path.

Concretely:

1. **Validation accepts HEIC/HEIF by magic bytes** (`FileValidationService`): an ISO-BMFF `ftyp` box
   at offset 4 with a HEIF-family major brand at offset 8 (`heic`/`heix`/`hevc`/`hevx` →
   `image/heic`; `mif1`/`msf1`/`heim`/`heis`/`hevm`/`hevs`/`mif2` → `image/heif`). The brand check
   gates acceptance so a sibling ISO-BMFF container that also opens with `ftyp` — e.g. an MP4
   (`isom`/`mp42`) — is **not** accepted as an image. Detection stays magic-byte based, never
   Content-Type (CLAUDE.md).
2. **Both upload endpoints transcode on ingest.** `DocumentEndpoints.UploadDocument` and
   `VendorPortalEndpoints.UploadViaPortal` call `IImageTranscoder.NormalizeForStorage`: HEIC/HEIF →
   JPEG (`MagickImageTranscoder`), every already-supported type passes through untouched. The stored
   blob's `ContentType` becomes `image/jpeg`; the user's original filename (`coi.heic`) is preserved
   on `Document.OriginalFileName` for provenance. On the portal, transcode runs **before** the blob
   upload and the quota-reservation transaction, so a photo we can't decode costs the vendor no
   permit and leaves no orphaned blob.
3. **An undecodable HEIC is a clean 400** (`document.unreadable_image`), not a stored-but-broken file
   — the transcoder throws `ImageTranscodeException`, the endpoint maps it to a friendly message.
4. **The transcoder bakes in EXIF orientation and strips metadata.** iPhones store photos upright
   plus an orientation tag; `AutoOrient()` applies it before `Strip()` so the JPEG isn't sideways,
   and stripping drops EXIF/GPS so a vendor's location never lands in our blob store.

**Library: Magick.NET (`Magick.NET-Q8-AnyCPU`).** HEIC decoding requires an HEVC decoder; there is no
pure-managed option. Magick.NET bundles its own ImageMagick + libheif natives per-RID (no separate
ImageMagick install), so the only extra container dependency is the OpenMP runtime `libgomp1`, added
to the Dockerfile's runtime stage. The integration tests decode a real HEIC fixture, so **Linux CI
proves the bundled delegate decodes HEIC on the same platform as the prod container.**

5. **The #196 stopgap copy is relaxed.** The portal dropzone now accepts `image/heic`/`image/heif`,
   and the "switch to Most Compatible and re-shoot" message is replaced with a simple unsupported-type
   message (it now only fires for genuinely unsupported types — a Word doc, a video, a `.zip`).

### Re-extraction note

`Reextract` re-reads `BlobStoragePath`, which already holds the transcoded JPEG, so re-extraction
needs no HEIC handling — the original HEIC bytes are intentionally not retained (we keep the JPEG we
can actually process and display).

## Consequences

### Positive

- A HEIC photo "just works" for every consumer — OCR, any LLM provider, and the browser preview —
  with zero downstream special-casing, exactly the failure mode #220 warned against avoided.
- Provider-independent: switching `Extraction:Provider` to Anthropic can't silently break HEIC.
- The transcode-before-reserve ordering on the portal means a bad photo never burns a paid upload
  permit or leaves an orphaned blob.
- Stripping EXIF removes a real privacy leak (vendor GPS coordinates in photo metadata).

### Negative

- A native image dependency (~tens of MB) + one apt package (`libgomp1`) in the runtime image. The
  decode runs on a cold, human-paced upload path (≤10 MB, bounded by the Kestrel cap), so the CPU/
  memory cost is per-upload, not per-request-hot-path.
- The original HEIC is not retained — only the JPEG. Acceptable: we keep the representation the whole
  product can read and show; the source photo has no independent value once transcoded.
- Transcoding is lossy (JPEG q82). For a document photo this is visually lossless enough for OCR; we
  are not archiving fine-art originals.

### Neutral

- The portal's "PDF, JPEG, or PNG" helper text is left as-is rather than advertising "HEIC" — vendors
  photographing a certificate don't reason in format names, and HEIC now succeeds silently.
- `image/heif` vs `image/heic` is recorded from the brand, but since both are transcoded the exact
  label only affects the (transient, pre-transcode) detected type, not handling.

## Alternatives considered

### Option A — accept HEIC, skip OCR for it, let Gemini read it directly

Make `DocumentAiOcrService` skip formats Document AI can't process (return empty OCR) and pass the
HEIC image straight to the LLM. Rejected: it works *only* while the provider is Gemini (Anthropic
can't read HEIC), leaves the browser preview broken, and discards the OCR signal for those documents
— precisely the fragile, provider-coupled "silent downstream failure" the ticket set out to avoid.

### Option B — transcode in the worker, store the HEIC as-is

Keep the original HEIC blob; transcode to JPEG only in `ExtractionWorker` before OCR. Rejected: the
stored blob stays HEIC, so the detail-page preview is still broken outside Safari, and every future
consumer of the blob (export, thumbnails) inherits the HEIC problem. Converting once at the boundary
is simpler for *everything* downstream.

### Option C — client-side transcode in the browser (e.g. heic2any/WASM)

Convert HEIC→JPEG in the vendor's browser before upload. Rejected: ships a ~1.5 MB WASM decoder to
every portal visitor, is memory-fragile on large phone photos, only covers the portal (not the
authenticated dashboard upload), and would mean the server never needs to accept HEIC magic bytes —
contradicting the AC and leaving the server unprotected if a HEIC arrives by any other path.

### Option D — pure-managed decoder (ImageSharp/SkiaSharp)

Rejected: neither decodes HEIC without a native HEVC delegate, so they don't actually remove the
native dependency — they just decode fewer formats than Magick.NET.

## Test coverage

- `FileValidationServiceTests` — a real HEIC fixture is accepted as `image/heic`; each HEIF-family
  brand maps to the right type; an `ftyp` box with a non-HEIF brand (MP4 `isom`) is rejected; a
  spoofed `image/heic` Content-Type on non-HEIC bytes is still rejected (magic bytes win).
- `ImageTranscoderTests` — `NeedsTranscodeToJpeg` matches only HEIC/HEIF (case-insensitive); a real
  HEIC decodes to a valid, re-decodable JPEG (**this runs on Linux CI, proving the bundled libheif
  delegate works on the prod platform**); undecodable bytes throw `ImageTranscodeException`;
  `NormalizeForStorage` passes non-HEIC through unchanged and returns null on an undecodable HEIC.
- `DocumentEndpointsTests` / `VendorPortalEndpointsTests` — uploading the real HEIC fixture yields a
  stored `image/jpeg` document that reaches the extraction queue with the original filename preserved;
  an undecodable HEIC returns a clean 400 and (portal) consumes no quota or storage.
- `portal/[token]/page.test.tsx` — a dropped HEIC now uploads to "Received" instead of showing the
  retired "Most Compatible" dead-end.

## References

- Ticket: [#220](https://github.com/neboxdev/complidrop/issues/220) (follow-up to
  [#196](https://github.com/neboxdev/complidrop/issues/196); rolling bug epic
  [#48](https://github.com/neboxdev/complidrop/issues/48))
- Code: `api/CompliDrop.Api/Services/ImageTranscoder.cs`,
  `api/CompliDrop.Api/Services/FileValidationService.cs`,
  `api/CompliDrop.Api/Endpoints/DocumentEndpoints.cs`,
  `api/CompliDrop.Api/Endpoints/VendorPortalEndpoints.cs`,
  `api/CompliDrop.Api/Dockerfile`, `frontend/src/app/portal/[token]/page.tsx`
