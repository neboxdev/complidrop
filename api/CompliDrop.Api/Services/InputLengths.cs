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
/// </summary>
public static class InputLengths
{
    // Register (anonymous) — POST /api/auth/register
    public const int OrganizationName = 200;
    public const int OrganizationIndustry = 100;
    public const int OrganizationCompanySize = 20;
    public const int UserFullName = 200;

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
    /// </summary>
    public const int ClientIdempotencyKey = 128;
}

/// <summary>
/// The ONE "is this request string short enough for the column it is about to be written to?" guard
/// (#389, ADR 0046). The reject-or-clamp split is deliberate and per-field, NOT a blanket rule:
/// <list type="bullet">
///   <item><description><b>Reject (this helper)</b> for content the USER TYPED — a company name, a
///     vendor name, a checklist requirement, a corrected field value. Silently truncating a user's own
///     words is data loss they never consented to and cannot see, and on a compliance product a
///     half-stored requirement is worse than a refused save.</description></item>
///   <item><description><b>Clamp (<see cref="ColumnClamp.To"/>)</b> for incidental machine values the
///     user did not author and does not read back — an upload's file NAME, a marketing attribution
///     tag, a client-minted idempotency key. Refusing a vendor's certificate because their phone named
///     it something long would block the actual job the product exists to do.</description></item>
/// </list>
/// Length is measured in UTF-16 code units while <c>varchar(n)</c> counts CODE POINTS, so a string of
/// astral characters (emoji) is judged conservatively — this can reject a value Postgres would have
/// accepted, never the reverse. That is the safe direction (a 400 the user can act on, never a 500),
/// and it matches <see cref="ColumnClamp.To"/>, which cuts by the same measure.
/// </summary>
public static class InputLength
{
    /// <summary>
    /// One code across every over-length rejection, so a client has ONE rule. The actionable detail is
    /// in the message, which the frontend surfaces verbatim (CLAUDE.md § Frontend error-message policy).
    /// </summary>
    public const string TooLongCode = "validation.too_long";

    /// <summary>
    /// Friendly, jargon-free, and it names the limit the user has to get under — the tone of the
    /// existing guards (<c>UpdateOrganization</c>'s "Organization name must be 200 characters or
    /// fewer.", <c>ContactEmail.TooLongMessage</c>). "N or fewer", not "under N": exactly N is accepted.
    /// </summary>
    public static string TooLongMessage(string label, int maxLength) =>
        $"{label} must be {maxLength} characters or fewer.";

    /// <summary>
    /// The ready-to-return 400 envelope for the FIRST over-length field, or <c>null</c> when every
    /// field fits. Null and blank always fit — an absent value is not an over-length one.
    /// <para/>
    /// Pass the value that will actually be WRITTEN (post-<c>Trim()</c> where the endpoint trims), not
    /// the raw request property: trimming only shortens, so checking the raw value would reject input
    /// the write would have accepted.
    /// </summary>
    public static IResult? FirstViolation(params (string? Value, int MaxLength, string Label)[] fields)
    {
        foreach (var (value, maxLength, label) in fields)
        {
            if (value is not null && value.Length > maxLength)
                return Results.Json(
                    new { data = (object?)null, error = new { code = TooLongCode, message = TooLongMessage(label, maxLength) } },
                    statusCode: StatusCodes.Status400BadRequest);
        }
        return null;
    }
}
