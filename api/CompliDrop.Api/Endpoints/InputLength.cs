using CompliDrop.Api.Services;

namespace CompliDrop.Api.Endpoints;

/// <summary>
/// The ONE "is this request string short enough for the column it is about to be written to?" guard
/// (#389, ADR 0046). The widths themselves live in <see cref="InputLengths"/> under <c>Services/</c>,
/// where <c>ModelConfiguration</c> reads them; this half — the ready-to-return HTTP envelope — lives
/// HERE, beside <see cref="IdempotencyResults"/>, because it is an <c>IResult</c> and knowing what a
/// 400 looks like is the endpoint layer's job, not a service's (#389 review; it was the only type in
/// <c>Services/</c> touching <c>Microsoft.AspNetCore.Http.Results</c>).
/// <para/>
/// Kept as a shared envelope rather than devolved into a <c>(code, message)</c> pair each endpoint's
/// own private <c>Error(...)</c> shapes: the property ADR 0046 §3 actually promises is that a client
/// sees ONE code and ONE message shape for every over-length rejection in the app, and returning the
/// finished result is what makes that true by construction instead of by eight call sites getting it
/// right.
/// <para/>
/// The reject-or-clamp split is deliberate and per-field, NOT a blanket rule:
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
    /// One code across every over-length rejection <b>this guard</b> makes, so a client has one rule for
    /// the #389 family. The actionable detail is in the message, which the frontend surfaces verbatim
    /// (CLAUDE.md § Frontend error-message policy).
    /// <para/>
    /// TWO deliberate pre-existing exceptions elsewhere in the app, both older than this guard and both
    /// kept on purpose — do NOT "unify" them (reviewers.md records the first as a bug if unified):
    /// <list type="bullet">
    ///   <item><description><c>ContactEmail</c>'s <c>validation.contact_email</c> (#369 / ADR 0038),
    ///     reachable on the SAME vendor request as this guard. Over-length is one of several ways a
    ///     vendor contact email can be invalid, and they all answer with one code and a message that
    ///     names the specific problem — splitting length off would give that field two codes.</description></item>
    ///   <item><description><c>AuthEndpoints.IsValidEmail</c>'s <c>validation.email</c>, likewise one
    ///     "enter a valid email" answer covering shape AND length.</description></item>
    /// </list>
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
    /// field fits. Null and blank always fit — an absent value is not an over-length one, which is why
    /// a non-nullable column fed from a nullable-in-practice DTO property still needs its own blank
    /// guard (see <c>UpsertRule</c>'s operator check).
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
