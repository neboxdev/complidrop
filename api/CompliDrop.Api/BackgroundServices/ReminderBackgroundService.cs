using System.Data;
using System.Data.Common;
using System.Net;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.BackgroundServices;

public class ReminderBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ReminderBackgroundService> logger) : BackgroundService
{
    private const int TargetLocalHour = 8;

    /// <summary><see cref="ReminderLog.Status"/> values this worker writes. <c>failed</c> means
    /// Resend returned non-2xx and never accepted the mail — the recipient was not served, so the
    /// row is excluded from dedupe and retried in place on a later tick the same local day
    /// (ADR 0025). <c>sent</c> means accepted; the inbound webhook may advance it further
    /// (delivered / bounced / … — see <c>ReminderStatusPrecedence</c>), and every advanced status
    /// keeps counting as served.</summary>
    internal const string StatusSent = "sent";
    internal const string StatusFailed = "failed";

    /// <summary>
    /// Trailing window for the editable-time-zone double-send guard (#205). The org time zone
    /// became user-editable in #185; changing it around the local 08:00 window can open a
    /// <em>second</em> qualifying tick on a different org-local calendar day within ~24h. That
    /// second tick computes a different <see cref="ReminderLog.SendDate"/>, so it slips past both
    /// the <c>(ReminderId, DocumentId, SendDate, RecipientEmail)</c> unique index and the same-day
    /// <c>alreadySent</c> set — re-sending the same document's reminder to the same recipient.
    /// <para/>
    /// The two qualifying ticks are provably &lt;24h apart in UTC: each fires at a fixed offset
    /// from its ~24h org-local expiration window, and both windows must contain the same document's
    /// single (fixed-UTC) expiration instant, so their starts — and therefore the ticks — fall
    /// inside one 24h span. 26h also matches the maximum IANA UTC-offset span (UTC+14…UTC-12), the
    /// most a zone edit can shift the local calendar day. A 26h trailing guard therefore catches
    /// the re-fire with margin while staying far short of a reminder's once-per-document cadence,
    /// so it cannot suppress a legitimately distinct future occurrence. See ADR 0015.
    /// <para/>
    /// Since #270 switched the document-match window to the UTC calendar day of the face date,
    /// two ticks on different org-local days target disjoint UTC-day brackets, so the original
    /// re-fire trigger is structurally unreachable; the guard is retained as defense-in-depth
    /// (it still suppresses any prior accepted send recorded under a different SendDate within
    /// the window). See ADR 0025 §"Interaction".
    /// </summary>
    private static readonly TimeSpan TzEditDedupeWindow = TimeSpan.FromHours(26);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ReminderBackgroundService starting.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessHourlyTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Reminder tick failed."); }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var nextTopOfHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
            var delay = nextTopOfHour - now;
            if (delay < TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ProcessHourlyTickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

        if (!email.IsEnabled)
        {
            logger.LogDebug("Reminder tick: email not configured, skipping.");
            return;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        // Pin one connection for the whole tick. Session-scoped advisory locks (acquired per
        // (org, sendDate) below) live on the connection; if EF Core grabbed a fresh connection
        // per command, the lock would live on a connection returned to the pool and the next
        // SaveChanges would run unprotected. The outer finally closes the connection — Npgsql's
        // DISCARD ALL on pool return releases any still-held advisory locks as a safety net
        // for an exception path that skipped explicit unlock. See ADR 0008.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // AsNoTracking on read-only queries: this worker only writes ReminderLog rows (inserts
            // via Add, in-place retry heals via the tracked priorLogs query below). Tracking orgs /
            // reminders / docs / users buys us nothing and inflates the change tracker.
            var orgs = await db.Organizations
                .AsNoTracking()
                .Where(o => o.DeletedAt == null)
                .Select(o => new { o.Id, o.Name, o.TimeZone })
                .ToListAsync(ct);

            foreach (var org in orgs)
            {
                if (!IsLocalSendWindow(org.TimeZone, nowUtc)) continue;

                var reminders = await db.Reminders
                    .AsNoTracking()
                    .Where(r => r.OrganizationId == org.Id && r.IsActive)
                    .ToListAsync(ct);
                if (reminders.Count == 0) continue;

                var localDate = DateOnly.FromDateTime(ToLocal(org.TimeZone, nowUtc));

                // Per-(org, sendDate) coordination across replicas. If another instance is currently
                // processing this same tuple, skip — its work will populate the ReminderLog rows
                // this instance would otherwise duplicate. The lock is keyed at org+day granularity
                // so disjoint orgs in the same tick can still run on different replicas in parallel.
                // See ADR 0008.
                var lockKey = BuildOrgDayLockKey(org.Id, localDate);
                bool acquired;
                try
                {
                    acquired = await TryAcquireOrgLockAsync(db, lockKey, ct);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown path — propagate so the outer try/finally closes the pinned
                    // connection and the worker exits cleanly.
                    throw;
                }
                catch (Exception ex)
                {
                    // A transient acquire failure (network blip, statement timeout, Neon
                    // disconnect) on one org must not kill the whole tick. Pre-fix, per-org
                    // failures were already bounded (TryFindTimeZone catches TZ errors,
                    // DbUpdateException is caught at save time). Treat the throw as
                    // "couldn't acquire, skip" and let the next hourly tick retry.
                    logger.LogWarning(ex,
                        "Reminder tick: pg_try_advisory_lock failed for org {OrgId} on {LocalDate}; skipping (next tick retries).",
                        org.Id, localDate);
                    continue;
                }
                if (!acquired)
                {
                    logger.LogDebug(
                        "Reminder tick: skipping org {OrgId} on {LocalDate} — advisory lock held by another instance.",
                        org.Id, localDate);
                    continue;
                }

                try
                {
                    // Hoisted out of the reminder loop: the internal-user list is identical for every
                    // reminder under the same org, so we resolve it once and reuse. OrderBy keeps the
                    // recipient order stable across runs (Postgres returns rows in physical order
                    // otherwise), which keeps multi-user assertions in tests deterministic.
                    var internalRecipients = reminders.Any(r => r.NotifyInternalUser)
                        ? await db.Users
                            .AsNoTracking()
                            .Where(u => u.OrganizationId == org.Id && u.DeletedAt == null)
                            .OrderBy(u => u.Email)
                            .Select(u => new InternalRecipient(u.Email, u.EmailVerifiedAt))
                            .ToListAsync(ct)
                        : new List<InternalRecipient>();
                    var internalUsers = internalRecipients.Select(r => r.Email).ToList();

                    // Dead-letter visibility (#184): an unverified internal address may be a
                    // signup typo that bounces every reminder silently. We still SEND (soft-gate
                    // — never withhold a paying org's reminders over a verification gap), but
                    // surface the risk in logs so a forming dead-letter is visible to an operator
                    // instead of invisible. Fires at most once per org per local-08:00 tick.
                    var unverifiedInternal = internalRecipients
                        .Where(r => r.EmailVerifiedAt is null)
                        .Select(r => r.Email)
                        .ToList();
                    if (unverifiedInternal.Count > 0)
                        logger.LogWarning(
                            "Reminder tick: org {OrgId} has {Count} unverified internal recipient(s); reminders may dead-letter to {Emails}.",
                            org.Id, unverifiedInternal.Count, string.Join(", ", unverifiedInternal));

                    foreach (var reminder in reminders)
                    {
                        var targetDate = localDate.AddDays(reminder.DaysBefore);
                        // ExpirationDate stores the document's FACE date at UTC midnight
                        // (CanonicalDocumentFields.ParseUtcDate, AssumeUniversal) — a date, not an
                        // instant in any zone. Match it on the UTC calendar day of targetDate,
                        // consistent with Document.IsExpired / DaysUntilExpiry and the display side
                        // (#263). The pre-#270 code converted the org-local day to UTC instead,
                        // shifting the bracket by the UTC offset: for every UTC-negative org the
                        // UTC-midnight expiry fell into the PREVIOUS local day's bracket, so the
                        // "N days before" email fired N+1 days out while the body claimed N.
                        var targetStart = DateTime.SpecifyKind(targetDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
                        var targetEnd = targetStart.AddDays(1);

                        var docs = await db.Documents
                            .AsNoTracking()
                            .Where(d => d.OrganizationId == org.Id
                                        && d.DeletedAt == null
                                        && d.ExpirationDate >= targetStart
                                        && d.ExpirationDate < targetEnd)
                            .Include(d => d.Vendor)
                            .ToListAsync(ct);
                        if (docs.Count == 0) continue;

                        // reminders.NotifyInternalUser gates whether THIS reminder includes the cached
                        // list; the cache itself is built once per org above.
                        var reminderInternalUsers = reminder.NotifyInternalUser ? internalUsers : new List<string>();

                        foreach (var doc in docs)
                        {
                            var recipients = new List<string>(reminderInternalUsers);
                            if (reminder.NotifyVendor && !string.IsNullOrWhiteSpace(doc.Vendor?.ContactEmail))
                                recipients.Add(doc.Vendor.ContactEmail);
                            // Distinct uses OrdinalIgnoreCase to match the dedupe HashSet below and the
                            // intent of "one mail per real recipient": a user email stored lowercased
                            // ("owner@x.com") and a vendor ContactEmail stored as-typed ("Owner@x.com")
                            // would otherwise be sent to twice within the same tick.
                            recipients = recipients
                                .Where(r => !string.IsNullOrWhiteSpace(r))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (recipients.Count == 0) continue;

                            // SendDate records the *org-local* calendar day this batch belongs to, not the
                            // UTC date at the instant the send happened. For a Tokyo org firing at local
                            // 08:00 on Jan 15 (23:00 UTC Jan 14) the column reads Jan 15 — matching what
                            // any analytics query, the Resend dashboard, or the org's audit log expects.
                            // SentAt (timestamptz) still stores the precise UTC instant.
                            // See ADR 0007 (revises the SendDate Neutral consequence in ADR 0002).
                            var sendDate = localDate;

                            // Pull the prior log rows for this (reminder, doc) within the dedupe
                            // horizon so multi-recipient reminders dedupe per recipient instead of
                            // skipping the doc once any recipient has been sent. Tracked (no
                            // AsNoTracking) deliberately: a same-day "failed" row is healed in
                            // place on retry below.
                            //
                            // Two predicates, OR'd:
                            //   (a) l.SendDate == sendDate — the same-org-local-day dedupe of ADR 0002 /
                            //       ADR 0007. Unchanged; this is the per-recipient invariant the unique
                            //       index enforces.
                            //   (b) l.SentAt >= tzEditGuardStart — the editable-time-zone guard (#205).
                            //       A zone edit (#185) around local 08:00 can re-open the send window on a
                            //       *different* local calendar day within ~24h, computing a different
                            //       SendDate that (a) alone would miss. Suppress a recipient already sent
                            //       this (reminder, doc) within the trailing guard window regardless of
                            //       which SendDate that prior send recorded. See TzEditDedupeWindow / ADR 0015.
                            //
                            // The (ReminderId, DocumentId) prefix of the unique index makes this selective
                            // — a single (reminder, doc) accrues at most a handful of log rows over its
                            // lifetime — so the added SentAt predicate needs no separate index.
                            var tzEditGuardStart = nowUtc - TzEditDedupeWindow;
                            var priorLogs = await db.ReminderLogs
                                .Where(l => l.ReminderId == reminder.Id
                                            && l.DocumentId == doc.Id
                                            && (l.SendDate == sendDate || l.SentAt >= tzEditGuardStart))
                                .ToListAsync(ct);

                            // Only rows Resend actually accepted count as served (ADR 0025): a
                            // "failed" row records an attempt that never left the building, so the
                            // recipient hasn't been served and this tick retries. Every other
                            // status — "sent" plus the webhook-advanced ones (delivered, bounced,
                            // complained, opened, clicked) — describes accepted mail and stays
                            // deduped; in particular a hard bounce must never auto-resend.
                            var alreadyServed = priorLogs
                                .Where(l => l.Status != StatusFailed)
                                .Select(l => l.RecipientEmail)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

                            // Same-day failed rows, keyed per recipient for in-place reuse: the
                            // (ReminderId, DocumentId, SendDate, RecipientEmail) unique index admits
                            // one row per tuple, so the retry must UPDATE, not insert. A failed row
                            // from an earlier SendDate (visible only via the guard arm) keys a
                            // different tuple — today's send inserts fresh and leaves it untouched.
                            // GroupBy is defensive: the index is case-sensitive while this lookup is
                            // not, so case-variant rows written across days could otherwise collide
                            // on the dictionary key.
                            var failedToday = priorLogs
                                .Where(l => l.Status == StatusFailed && l.SendDate == sendDate)
                                .GroupBy(l => l.RecipientEmail, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                            foreach (var recipient in recipients)
                            {
                                if (alreadyServed.Contains(recipient)) continue;

                                try
                                {
                                    var subject = reminder.EmailSubjectTemplate
                                                  ?? $"[{org.Name}] {doc.OriginalFileName} expires in {reminder.DaysBefore} days";
                                    var body = BuildBody(org.Name, doc, reminder.DaysBefore);
                                    var messageId = await email.SendAsync(recipient, subject, body, ct);
                                    var attemptedAt = timeProvider.GetUtcNow().UtcDateTime;
                                    var status = messageId is null ? StatusFailed : StatusSent;

                                    if (failedToday.TryGetValue(recipient, out var failedRow))
                                    {
                                        // Retry path: heal (or re-stamp) the existing row in place.
                                        // SentAt records the LATEST attempt instant (ADR 0025).
                                        failedRow.SentAt = attemptedAt;
                                        failedRow.ResendMessageId = messageId;
                                        failedRow.Status = status;
                                    }
                                    else
                                    {
                                        db.ReminderLogs.Add(new ReminderLog
                                        {
                                            Id = Guid.NewGuid(),
                                            ReminderId = reminder.Id,
                                            DocumentId = doc.Id,
                                            RecipientEmail = recipient,
                                            SentAt = attemptedAt,
                                            SendDate = sendDate,
                                            ResendMessageId = messageId,
                                            Status = status
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "Failed sending reminder for doc {DocumentId}", doc.Id);
                                }
                            }

                            try { await db.SaveChangesAsync(ct); }
                            catch (DbUpdateException ex)
                            {
                                // Defensive: the per-(org, sendDate) advisory lock above makes this
                                // path nearly unreachable in multi-instance deploys — the only residual
                                // trigger is a hashtextextended collision in the lock key space (2^63).
                                // We keep the catch + detach because the cost is zero and the recovery
                                // shape is the same one we'd want if a future code path were to bypass
                                // the lock.
                                logger.LogWarning(ex, "Reminder log upsert conflict (likely concurrent tick) for doc {DocumentId}", doc.Id);

                                // After a failed SaveChanges the pending ReminderLog entries (Added
                                // inserts and Modified retry heals) stay in the change tracker. The
                                // next doc iteration's SaveChangesAsync would re-attempt them and
                                // throw again — cascading the failure to every doc remaining in this
                                // tick. Detach them so each doc gets a clean slate; a detached retry
                                // heal is re-attempted naturally on the next qualifying tick.
                                foreach (var entry in db.ChangeTracker.Entries<ReminderLog>()
                                             .Where(e => e.State is EntityState.Added or EntityState.Modified)
                                             .ToList())
                                {
                                    entry.State = EntityState.Detached;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    // Release even on cancellation/throw. We deliberately do not pass `ct` to the
                    // release — a cancelled tick should still hand the lock back to the pool rather
                    // than rely on DISCARD ALL alone. The pool reset is a safety net, not the
                    // primary release path. Swallow exceptions from the unlock itself: if the
                    // connection is broken the lock dies with the session server-side anyway, and
                    // a throw here would escape the per-org loop and skip every remaining org for
                    // this tick (the very contract this finally exists to maintain).
                    try
                    {
                        await ReleaseOrgLockAsync(db, lockKey);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Reminder tick: pg_advisory_unlock failed for org {OrgId} on {LocalDate}; pool DISCARD ALL will release on connection return.",
                            org.Id, localDate);
                    }
                }
            }
        }
        finally
        {
            // Returning the connection to the pool triggers Npgsql's DISCARD ALL, which runs
            // pg_advisory_unlock_all() — a final safety net for any lock we somehow didn't
            // release above (e.g. unlock-call itself threw). We deliberately do NOT pass `ct`
            // here for the same reason as ReleaseOrgLockAsync's no-CT pattern: cleanup must run
            // even on shutdown so any leftover advisory lock is freed before the connection
            // returns to the pool.
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Stable text key for the per-(org, sendDate) advisory lock. The name encodes the lock's
    /// granularity (one lock per org per local-calendar day) so a future caller can tell from
    /// the call site what bound contention this lock provides. Hashed server-side via
    /// <c>hashtextextended(text, 0)</c> so the same key always maps to the same <c>bigint</c>
    /// across processes — unlike CLR <c>string.GetHashCode</c>, which is randomised per
    /// process from .NET Core 2.1 onwards.
    /// </summary>
    internal static string BuildOrgDayLockKey(Guid orgId, DateOnly sendDate) =>
        $"reminder:{orgId:N}:{sendDate:yyyyMMdd}";

    /// <summary>An org's internal (admin) recipient plus its verification state, projected for the
    /// dead-letter-visibility check (#184). EmailVerifiedAt == null ⇒ a possibly-typo'd address.</summary>
    private sealed record InternalRecipient(string Email, DateTime? EmailVerifiedAt);

    /// <summary>
    /// Tries to acquire the session-scoped Postgres advisory lock keyed by
    /// <paramref name="lockKey"/> on the DbContext's currently-open connection. Returns true if
    /// the lock was acquired, false if another session holds it. Non-blocking
    /// (<c>pg_try_advisory_lock</c>), so a held lock causes an immediate <c>false</c> rather
    /// than a wait.
    /// </summary>
    private static async Task<bool> TryAcquireOrgLockAsync(SystemDbContext db, string lockKey, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))";
        AddTextParam(cmd, "@key", lockKey);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool b && b;
    }

    /// <summary>
    /// Releases the session-scoped advisory lock keyed by <paramref name="lockKey"/>. Best-effort:
    /// if the call itself throws (e.g. the connection is already gone), the pool's DISCARD ALL
    /// will release the lock on connection return, so we log and swallow rather than letting an
    /// unlock error propagate up the per-org loop and skip subsequent orgs.
    /// </summary>
    private static async Task ReleaseOrgLockAsync(SystemDbContext db, string lockKey)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@key, 0))";
        AddTextParam(cmd, "@key", lockKey);
        // ExecuteScalarAsync without a CT — see comment at the call site for why.
        await cmd.ExecuteScalarAsync();
    }

    private static void AddTextParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = DbType.String;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// True from org-local 08:00 through the end of the org-local day. The window is open-ended
    /// (>= rather than ==) so a tick missed during the 08:00 hour — deploy, crash, stuck
    /// instance, or a DST transition that skips the wall-clock hour — is caught up by any later
    /// tick the same local day; the per-recipient dedupe makes the extra qualifying ticks
    /// idempotent, and a failed send gets its retry vehicle. Catch-up deliberately ends at local
    /// midnight: the next day derives different target dates and the email copy ("N days from
    /// today") would no longer be true. See ADR 0025.
    /// </summary>
    private static bool IsLocalSendWindow(string tz, DateTime nowUtc)
    {
        var info = TryFindTimeZone(tz);
        if (info is null) return nowUtc.Hour >= TargetLocalHour;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, info);
        return local.Hour >= TargetLocalHour;
    }

    private static DateTime ToLocal(string tz, DateTime utc)
    {
        var info = TryFindTimeZone(tz);
        return info is null ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, info);
    }

    /// <summary>Returns the named IANA / Windows zone, or null for an unknown id.
    /// Delegates to the shared <see cref="Services.TimeZones"/> policy (#262).</summary>
    private static TimeZoneInfo? TryFindTimeZone(string tz) => Services.TimeZones.TryFind(tz);

    private static string BuildBody(string orgName, Document doc, int daysBefore)
    {
        var expiration = doc.ExpirationDate?.ToString("MMMM d, yyyy") ?? "an upcoming date";
        // HTML-encode every caller-influenced value before interpolating into the
        // email HTML. The org name is user-editable (#185) and the file/vendor
        // names come from uploads + vendor records; reminder emails are delivered
        // to vendors (outside the org's trust boundary), so an unescaped value
        // would be a stored HTML-injection sink. Encode at the render sink.
        var vendor = WebUtility.HtmlEncode(doc.Vendor?.Name ?? "a vendor");
        var fileName = WebUtility.HtmlEncode(doc.OriginalFileName);
        var org = WebUtility.HtmlEncode(orgName);
        return $"""
            <div style="font-family: system-ui, sans-serif; color: #0c4a6e;">
              <h2 style="color: #0284c7;">Compliance reminder</h2>
              <p>Hi there,</p>
              <p>Your document <strong>{fileName}</strong> from <strong>{vendor}</strong> expires on <strong>{expiration}</strong> — that's {daysBefore} days from today.</p>
              <p>Log in to {org} on CompliDrop to review and upload the renewal.</p>
              <p style="color: #64748b; font-size: 12px;">Sent automatically by CompliDrop. You can adjust reminder cadence in Settings → Reminders.</p>
            </div>
            """;
    }
}
