# 0022. Document bytes are served only via the authenticated API proxy — no SAS, no public container

- **Status:** accepted
- **Date:** 2026-06-11
- **Deciders:** Ruben G., Claude

## Context

Uploaded compliance documents (COIs, licenses, permits) live in a private Azure Blob
container (`PublicAccessType.None`). Until #254 no read path existed at all: the detail
page's "View file" linked the raw blob URI, which Azure rejected on every click — the
affordance had never worked.

Three ways to let a browser see the bytes:

1. **Flip the container public.** Rejected outright: every customer's COIs become
   world-readable to anyone holding (or enumerating) a URL.
2. **Mint SAS tokens.** Time-boxed links the browser fetches straight from Azure. Rejected:
   once handed out a SAS link is bearer-anyone — it cannot be tenant-revoked, outlives
   logout/account deletion until expiry, leaks into browser history / proxies, and its
   lifetime becomes a second auth system to reason about next to the cookie session.
3. **Stream through the API.** An authenticated, tenant-filtered endpoint proxies the blob.

## Decision

**Option 3.** `GET /api/documents/{id}/file` is the ONLY read path for document bytes:

- Inside the `RequireAuthorization` group; the document resolves through the
  tenant-filtered `AppDbContext` set, so cross-org / soft-deleted / unknown ids are
  indistinguishable 404s.
- Streams pass-through (`Results.Stream` over `DownloadStreamingAsync` — no buffering),
  `Content-Disposition: inline` (RFC 6266 filename), the STORED magic-byte-validated /
  ingest-normalized content type, `Cache-Control: private, no-store`, and
  `X-Content-Type-Options: nosniff`.
- Blob-not-found is part of the `IBlobStorageService` contract (`DownloadAsync` returns
  `null`), so no Azure SDK exception types leak past the storage boundary.
- The raw `BlobStorageUrl` is OFF the API surface (removed from the detail DTO): it was
  never usable by clients and disclosed storage-account naming.
- The frontend reaches the endpoint through `api.getBlob` (shared cookie transport,
  coalesced silent 401-refresh, friendly-error mapping — the same machinery as the
  envelope client).

Every future file surface — previews, thumbnails, downloads — routes through this proxy
(or a sibling endpoint with the same auth + tenant shape), never via SAS or public blobs.

## Consequences

- All file bytes flow through the API process. Fine at the 10 MB/file cap and SMB scale;
  if egress ever becomes a bottleneck, the revisit point is short-lived SAS minted
  per-request server-side — a new ADR, not a default.
- No CDN/browser caching of documents (`no-store` is deliberate for compliance data).
- ADR 0018's browser-preview rationale (HEIC→JPEG so the `<img>` preview works) now
  applies through this proxy rather than the raw blob URL it originally referenced; the
  transcode reasoning is unchanged since the proxy serves the stored content type.
