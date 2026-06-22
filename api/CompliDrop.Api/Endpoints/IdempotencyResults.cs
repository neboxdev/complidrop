using System.Text.Json;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Endpoints;

/// <summary>
/// Shared shaping of the idempotency replay / in-progress responses, used by every endpoint that honors
/// an <c>Idempotency-Key</c> (document upload, sample seed, billing checkout) so the replay envelope is
/// identical across them. See ADR 0029 for the loser contract (replay the winner; 409 only as a
/// defensive fallback that a committed winner is unreadable).
/// </summary>
public static class IdempotencyResults
{
    /// <summary>Re-emits the winner's cached response verbatim (same status code, same body).</summary>
    public static IResult Replay(IdempotencyHit hit) =>
        Results.Json(
            hit.ResponseJson is null ? null : JsonSerializer.Deserialize<object>(hit.ResponseJson),
            statusCode: hit.StatusCode);

    /// <summary>
    /// Defensive fallback only: a same-key request lost the unique-index race but the winner's record
    /// could not be read back (only reachable if a record were GC'd mid-request, which never happens
    /// today). A retryable 409 rather than a 500.
    /// </summary>
    public static IResult InProgressConflict() =>
        Results.Json(
            new
            {
                data = (object?)null,
                error = new { code = "idempotency.in_progress", message = "This request is still being processed. Please try again in a moment." }
            },
            statusCode: StatusCodes.Status409Conflict);
}
