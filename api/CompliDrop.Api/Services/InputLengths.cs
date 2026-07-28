namespace CompliDrop.Api.Services;

/// <summary>
/// Widths of the bounded <c>varchar(n)</c> columns written straight from REQUEST input (#389,
/// <c>docs/adr/0046-request-input-length-guards.md</c>). Companion to <see cref="AuditColumnLengths"/>
/// (#372 / ADR 0044), which does the same job for the three audit columns fed by request HEADERS.
/// <para/>
/// These are the SOURCE of the widths, not a mirror of them: <c>ModelConfiguration</c> calls
/// <c>HasMaxLength(InputLengths.X)</c> — the <c>ContactEmail.MaxLength</c> / <c>AuditColumnLengths</c>
/// pattern — so the column and the edge guard that feeds it agree by construction and a widened or
/// narrowed column cannot silently leave the guard behind.
/// <para/>
/// Why the guards exist at all: <b>Npgsql does not truncate.</b> An over-length string written to a
/// bounded column fails the WHOLE <c>SaveChanges</c> with Postgres <c>22001</c>, which surfaces as an
/// unhandled <c>DbUpdateException</c> → generic 500 on a route where the honest answer is a 400. On the
/// public routes (portal upload, register, waitlist) the input is chosen by an untrusted third party,
/// and on the dashboard upload the failure also stranded an already-uploaded blob.
/// <para/>
/// Only the columns an ENDPOINT writes from request input are listed. Columns fed by compile-time
/// literals (<c>UploadedBy</c>, the status enums), by a value whose length the server controls
/// (<c>BlobStoragePath</c>, <c>Token</c>), or by a vocabulary that is length-safe by construction
/// (<c>DocumentType</c> via <see cref="CanonicalDocumentTypes"/>, <c>TimeZone</c> via
/// <see cref="TimeZones"/>) are deliberately absent — adding them here would imply a guard that does
/// not and need not exist. The extraction worker's own widths stay on
/// <c>ExtractionWorker</c> (a different writer with a different — truncating — policy, ADR 0045 §4).
/// <para/>
/// This file holds the NUMBERS only. The guard that turns one into a 400 — <c>InputLength</c> — lives
/// in <c>Endpoints/</c>, because it returns an <c>IResult</c> and nothing else in <c>Services/</c>
/// knows what an HTTP envelope looks like (#389 review).
/// <para/>
/// One entry — <see cref="DocumentFieldUpdatesPerRequest"/> — bounds a COLLECTION COUNT rather than a
/// column width. It is here because it is the same decision (bound the request at the edge, in one
/// reviewed place) even though it has no column to be bound to; its own doc says so.
/// </summary>
public static class InputLengths
{
    // Register (anonymous) — POST /api/auth/register
    public const int OrganizationName = 200;
    public const int OrganizationIndustry = 100;
    public const int OrganizationCompanySize = 20;
    public const int UserFullName = 200;

    /// <summary>
    /// <c>User.Email</c> and the pending <c>EmailVerificationToken.NewEmail</c> that becomes it —
    /// written from the ANONYMOUS register body and from the change-email request. Bounded by
    /// <c>AuthEndpoints.IsValidEmail</c>, which reads this constant, so the column and its guard are
    /// one number rather than the two hand-copied 256s they were.
    /// <para/>
    /// That guard keeps its OWN error code (<c>validation.email</c>) and is NOT folded into
    /// <c>InputLength.TooLongCode</c>: an unparseable-or-too-long account email is one
    /// "enter a valid email" answer on a field the user is looking at, and splitting it into two codes
    /// mid-form would be a worse experience than the single rule it has today. Nor is
    /// <c>IsValidEmail</c>'s laxness otherwise touched — the lax-account-email vs strict-vendor-contact
    /// split is deliberate and recorded in ADR 0038 (an account email is proven by the verification
    /// mail, so a typo self-corrects; a vendor contact email is never proven and fails silently forever).
    /// </summary>
    public const int UserEmail = 256;

    // Waitlist (anonymous) — POST /api/waitlist
    public const int WaitlistEmail = 256;
    public const int WaitlistCompanyName = 200;
    public const int WaitlistIndustry = 100;
    public const int WaitlistSource = 100;

    // Uploads — POST /api/documents/upload and the PUBLIC POST /api/portal/{token}/upload
    public const int DocumentOriginalFileName = 500;

    // Manual field correction — PUT /api/documents/{id}/fields
    public const int DocumentFieldName = 200;
    public const int DocumentFieldValue = 2000;

    /// <summary>
    /// Most field corrections one <c>PUT /api/documents/{id}/fields</c> may carry. The ONE entry here
    /// that bounds a COUNT rather than a column width, and it is here so it is pinned and reviewed
    /// beside its siblings rather than buried as a magic number in the endpoint — same family of
    /// decision (bound the request at the edge), different unit. Deliberately absent from the
    /// <c>ModelConfiguration</c> binding tests for that reason: there is no column to agree with.
    /// <para/>
    /// Bounding each element's LENGTH is not enough on its own. Kestrel admits a 10 MB body and the
    /// array is uncapped, so ~45 bytes per entry buys a single authenticated PUT a six-figure element
    /// count — walked twice by the guard, grouped, then written row by row against the tracked
    /// document. Cheap for the caller, expensive for the CPU and the DB.
    /// <para/>
    /// Sized an order of magnitude above reality: the extraction schema defines ~20 canonical fields
    /// and the detail page renders one input per extracted field, so a genuine save is a handful.
    /// Exactly this many is accepted; one more is a <c>validation.too_many_fields</c> 400 — a distinct
    /// code from <c>InputLength.TooLongCode</c> on purpose, because "you sent too many things" and
    /// "one thing was too long" are different problems with different fixes.
    /// </summary>
    public const int DocumentFieldUpdatesPerRequest = 200;

    // Vendors — POST/PUT /api/vendors. ContactEmail is bounded by ContactEmail.MaxLength (#369).
    public const int VendorName = 200;
    public const int VendorContactPhone = 50;
    public const int VendorCategory = 100;

    // Checklists — /api/compliance/templates and their rules
    public const int TemplateName = 200;
    public const int TemplateDescription = 500;
    public const int RuleFieldName = 200;
    public const int RuleOperator = 50;
    public const int RuleExpectedValue = 500;
    public const int RuleErrorMessage = 500;

    // Reminders — PUT /api/reminders/{id}
    public const int ReminderEmailSubjectTemplate = 500;

    /// <summary><c>IdempotencyRecord.Key</c>, the stored column.</summary>
    public const int IdempotencyKey = 200;

    /// <summary>
    /// The longest client <c>Idempotency-Key</c> header any endpoint honors, shared by all four
    /// idempotent routes so they cannot drift. Sized for the PUBLIC portal's worst case, which stores a
    /// NAMESPACED key rather than the raw header: <c>"portal:{token}:{key}"</c> is 8 + 32 + 128 = 168,
    /// ample headroom under <see cref="IdempotencyKey"/>. A UUID/nonce — what every real client mints —
    /// is 36.
    /// <para/>
    /// That arithmetic is NOT load-bearing prose: raising this constant (or lengthening the portal
    /// token) goes red on <c>VendorPortalEndpointsTests.The_namespaced_portal_key_always_fits_the_
    /// idempotency_column</c>, which computes the worst case from
    /// <c>VendorPortalEndpoints.NamespacedIdempotencyKey</c> and the real <c>PortalLink.GenerateToken</c>
    /// rather than restating the numbers. Three authenticated routes read this constant too, so the
    /// PUBLIC route's safety otherwise depended on a value it does not own. (#389 review)
    /// </summary>
    public const int ClientIdempotencyKey = 128;
}
