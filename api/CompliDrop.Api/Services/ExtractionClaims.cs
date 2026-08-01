namespace CompliDrop.Api.Services;

/// <summary>
/// The extraction queue's CLAIM timing — the one fact the worker and the request path must agree on
/// (#365, <c>docs/adr/0050-reextract-refuses-a-live-extraction-claim.md</c>). Same shape and same reason
/// as <see cref="InputLengths"/>: a value TWO layers depend on is SOURCED here, in <c>Services/</c>, and
/// each layer aliases it — <c>ExtractionWorker.ZombieClaimTimeout</c> (whose <c>ClaimSql</c> interpolates
/// it) and <c>DocumentEndpoints.Reextract</c> (whose guard compares against it). Worker-ONLY numbers stay
/// on the worker (<c>MaxAttempts</c>, <c>MaxClaims</c>, <c>AttemptTimeoutCeilingSeconds</c>, the response
/// clamp widths), exactly as <see cref="InputLengths"/> scopes its own exception; putting this one here is
/// what keeps <c>Endpoints/</c> from compiling against <c>BackgroundServices/</c>, which nothing outside
/// the composition root does.
/// <para/>
/// Why the two sides must be ONE value: <c>ExtractionWorker.ClaimSql</c> reclaims a <c>Processing</c> row
/// whose <c>ProcessingStartedAt</c> is older than this window, and <c>Reextract</c> refuses to re-arm a
/// document whose claim is NEWER than it. Two independently-maintained copies could drift into a window
/// where the endpoint re-queues a document a live worker still holds — the double-OCR/LLM, duplicate-field,
/// double-spend race #365 exists to close. A test parses the interval back out of <c>ClaimSql</c> and
/// compares it to this constant.
/// <para/>
/// The BOUNDARY tests on both sides deliberately keep their own <c>4m30s</c> / <c>5m30s</c> literals (#62):
/// their job is to be the regression discriminator for this value, so hoisting them onto it would make
/// them vacuous.
/// </summary>
public static class ExtractionClaims
{
    /// <summary>
    /// Zombie-reclaim threshold: how long a <c>Processing</c> claim may sit before the worker stops
    /// believing it is live. Held as minutes because <c>ClaimSql</c> interpolates it into a Postgres
    /// <c>interval '{n} minutes'</c> literal (<see cref="ZombieTimeout"/> is the same value as a
    /// <see cref="TimeSpan"/>, for the C# comparisons).
    /// </summary>
    public const int ZombieTimeoutMinutes = 5;

    /// <inheritdoc cref="ZombieTimeoutMinutes"/>
    public static TimeSpan ZombieTimeout => TimeSpan.FromMinutes(ZombieTimeoutMinutes);
}
