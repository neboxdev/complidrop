namespace CompliDrop.Api.Endpoints;

/// <summary>
/// Shared runner for the post-commit compliance re-grade fan-outs (#364) — the batched
/// <c>IComplianceCheckService.Reevaluate*</c> passes that follow a rule upsert, a rule delete, a
/// template delete, a vendor checklist reassignment, or a vendor soft-delete (#422: both the vendor
/// delete endpoint and the sample-data clear). Every one of those callers commits its mutation FIRST
/// and then re-grades, so they all share the same two hazards and must handle them identically;
/// keeping the policy here is what stops the six sites drifting apart.
/// </summary>
internal static class PostCommitRegrade
{
    /// <summary>
    /// Ceiling on a single fan-out. <see cref="IHostApplicationLifetime.ApplicationStopping"/> alone
    /// only trips at shutdown, so without this an abandoned request's fan-out would keep paging
    /// documents and holding its Npgsql pool connection indefinitely — the client disconnect used to
    /// reclaim both. Generous enough that a realistic org-sized fan-out finishes well inside it; one
    /// that does not is truncated exactly like a failed page (logged, then healed by the next
    /// user-initiated re-grade).
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs <paramref name="fanout"/> on a token DECOUPLED from the request, and swallows its failures.
    /// <para/>
    /// TOKEN: the triggering mutation has already committed, so there is nothing left to roll back and a
    /// client disconnect or proxy timeout must not truncate the work that keeps the persisted verdicts
    /// consistent with it. On the request's token it did — minimal APIs bind that to
    /// <c>HttpContext.RequestAborted</c> — stranding an arbitrary suffix of the population on
    /// pre-mutation verdicts with no automatic healer (<c>ReevaluateWhereAsync</c> rethrows
    /// cancellation; its per-page catch excludes <see cref="OperationCanceledException"/>). Linked to
    /// <see cref="IHostApplicationLifetime.ApplicationStopping"/> so a real shutdown still interrupts,
    /// plus <see cref="Timeout"/> so an abandoned request cannot pin a connection forever.
    /// <para/>
    /// SWALLOWS: the mutation is committed and the response must say so. Letting a fan-out failure
    /// escape hands the user a 500 for a rule that IS deleted (or created) — they retry and get a 404,
    /// or a 409 "already on this checklist" on the upsert twin. Two triggers are production-reachable
    /// and sit OUTSIDE the fan-out's own per-page catch: the snapshot query at the top of
    /// <c>ReevaluateWhereAsync</c>, and the cancellation this method's own token can raise (a SIGTERM
    /// during a Railway deploy fires ApplicationStopping at t=0 of the drain). A truncated re-grade
    /// leaves stale verdicts that the next user-initiated re-grade heals — "Check again", another rule
    /// edit, or a checklist reassignment — which is strictly better than also lying about whether the
    /// mutation landed. Note the nightly sweep does NOT heal them:
    /// <c>ComplianceSweepBackgroundService</c> only does date-transition updates, never rule evaluation.
    /// </summary>
    /// <param name="fanout">The fan-out to run, given the decoupled token.</param>
    /// <param name="lifetime">Supplies the shutdown token the ceiling is linked to.</param>
    /// <param name="loggerFactory">Used to log a swallowed failure.</param>
    /// <param name="operation">Short description of the committed mutation, for the log line.</param>
    internal static async Task RunAsync(
        Func<CancellationToken, Task> fanout,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        string operation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        cts.CancelAfter(Timeout);
        try
        {
            await fanout(cts.Token);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(nameof(PostCommitRegrade)).LogError(
                ex,
                "Post-commit compliance re-grade fan-out failed after {Operation}; the mutation is committed "
                    + "and affected documents keep their previous verdict until the next re-grade",
                operation);
        }
    }
}
