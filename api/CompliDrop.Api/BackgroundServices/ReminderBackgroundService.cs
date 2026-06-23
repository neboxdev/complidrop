using System.Data;
using System.Data.Common;
using System.Net;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.BackgroundServices;

public class ReminderBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ReminderBackgroundService> logger) : BackgroundService
{
    private const int TargetLocalHour = 8;

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
        // Base URL for vendor upload links embedded in vendor reminder emails (#320 FP-092).
        var frontend = scope.ServiceProvider.GetRequiredService<IOptions<FrontendSettings>>().Value;

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

                var localNow = ToLocal(org.TimeZone, nowUtc);
                var localDate = DateOnly.FromDateTime(localNow);

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
                    // instead of invisible. Gated to the org's 08:00 hour so it keeps firing at
                    // most once per org per local day — the ADR 0025 catch-up window re-qualifies
                    // the org on every later tick, and repeating an operator signal (which embeds
                    // the addresses) ~16x/day is noise, not visibility. A day whose 08:00 tick
                    // was missed skips the warning rather than repeating it; the flag is a
                    // visibility aid, not delivery-critical.
                    var unverifiedInternal = internalRecipients
                        .Where(r => r.EmailVerifiedAt is null)
                        .Select(r => r.Email)
                        .ToList();
                    if (unverifiedInternal.Count > 0 && localNow.Hour == TargetLocalHour)
                        logger.LogWarning(
                            "Reminder tick: org {OrgId} has {Count} unverified internal recipient(s); reminders may dead-letter to {Emails}.",
                            org.Id, unverifiedInternal.Count, string.Join(", ", unverifiedInternal));

                    // FP-092: a vendor reminder embeds the vendor's active upload link so the vendor can
                    // actually act — they have no CompliDrop account to "log in" to. The link is a Pro
                    // entitlement (#261), so only mint/embed when the org has the portal; resolve+mint
                    // once per vendor per tick (cached) so a vendor with several expiring docs doesn't
                    // spawn several links. A worker-minted link mirrors GeneratePortalLink's shape.
                    var orgHasPortal = await db.Subscriptions
                        .AnyAsync(s => s.OrganizationId == org.Id && s.HasVendorPortal, ct);
                    var vendorPortalUrls = new Dictionary<Guid, string?>();

                    // #340: addresses this org has hard-bounced or had a spam complaint on — the engine must
                    // not re-send to them (a known-dead/opted-out mailbox). Loaded once per org-tick and
                    // checked per recipient below. Resend's account-level suppression backstops actual
                    // delivery; this stops us even trying (no wasted send / log row) and is what surfaces the
                    // dead address to the operator (the vendor badge + the feed event the webhook wrote).
                    var suppressed = (await db.EmailSuppressions
                            .Where(s => s.OrganizationId == org.Id)
                            .Select(s => s.Email)
                            .ToListAsync(ct))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    async Task<string?> ResolveVendorPortalUrlAsync(Guid vendorId)
                    {
                        if (vendorPortalUrls.TryGetValue(vendorId, out var cached)) return cached;
                        string? url = null;
                        if (orgHasPortal)
                        {
                            var existing = await db.VendorPortalLinks
                                .Where(l => l.VendorId == vendorId && l.IsActive)
                                .OrderByDescending(l => l.CreatedAt)
                                .Select(l => l.Token)
                                .FirstOrDefaultAsync(ct);
                            if (existing is not null)
                            {
                                url = PortalLink.Url(frontend, existing);
                            }
                            else
                            {
                                var token = PortalLink.GenerateToken();
                                db.VendorPortalLinks.Add(new VendorPortalLink
                                {
                                    Id = Guid.NewGuid(),
                                    VendorId = vendorId,
                                    Token = token,
                                    IsActive = true,
                                    MaxUploads = PortalLink.DefaultMaxUploads,
                                    UploadCount = 0,
                                    CreatedAt = nowUtc,
                                });
                                await db.SaveChangesAsync(ct);
                                url = PortalLink.Url(frontend, token);
                            }
                        }
                        vendorPortalUrls[vendorId] = url;
                        return url;
                    }

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
                            // #327: don't remind on a SUPERSEDED cert — if a newer document exists for the
                            // same vendor + type the vendor has already renewed, so the old copy's expiry
                            // must not pester them. Shared DocumentSupersession predicate (ADR 0033).
                            .Where(DocumentSupersession.IsCurrent(db.Documents))
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
                                .Where(l => l.Status != ReminderLogStatus.Failed)
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
                                .Where(l => l.Status == ReminderLogStatus.Failed && l.SendDate == sendDate)
                                .GroupBy(l => l.RecipientEmail, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                            foreach (var recipient in recipients)
                            {
                                if (alreadyServed.Contains(recipient)) continue;
                                // #340: never re-send to a hard-bounced / complained address. Skip silently
                                // (no log row, no cost) — the dead address is already surfaced on the vendor
                                // and the feed via the suppression the webhook recorded.
                                if (suppressed.Contains(recipient)) continue;

                                failedToday.TryGetValue(recipient, out var failedRow);

                                try
                                {
                                    var subject = reminder.EmailSubjectTemplate
                                                  ?? $"[{org.Name}] {doc.OriginalFileName} expires in {reminder.DaysBefore} days";
                                    // Recipient-aware body (FP-092): the vendor (no account) gets the
                                    // upload-link copy; internal users get the log-in-and-review copy.
                                    var isVendorRecipient =
                                        reminder.NotifyVendor
                                        && !string.IsNullOrWhiteSpace(doc.Vendor?.ContactEmail)
                                        && string.Equals(recipient, doc.Vendor!.ContactEmail, StringComparison.OrdinalIgnoreCase);
                                    var body = isVendorRecipient
                                        ? BuildVendorBody(org.Name, doc, reminder.DaysBefore,
                                            doc.VendorId is Guid vId ? await ResolveVendorPortalUrlAsync(vId) : null)
                                        : BuildInternalBody(org.Name, doc, reminder.DaysBefore);
                                    var idempotencyKey = BuildSendIdempotencyKey(
                                        reminder.Id, doc.Id, sendDate, recipient, failedRow?.SentAt);
                                    var messageId = await email.SendAsync(recipient, subject, body, ct, idempotencyKey);
                                    var attemptedAt = timeProvider.GetUtcNow().UtcDateTime;
                                    var status = messageId is null ? ReminderLogStatus.Failed : ReminderLogStatus.Sent;

                                    if (failedRow is not null)
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
                                            // Denormalized org id (#309). The heal path doesn't touch it:
                                            // a failed row already carries the right org from its insert.
                                            OrganizationId = org.Id,
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
                                // We keep the catch because the cost is zero and the recovery shape is
                                // the same one we'd want if a future code path were to bypass the lock.
                                logger.LogWarning(ex, "Reminder log upsert conflict (likely concurrent tick) for doc {DocumentId}", doc.Id);
                            }

                            // One Clear() does two jobs. Recovery: after a failed save the pending
                            // Added/Modified ReminderLog entries would be re-attempted by the NEXT
                            // doc's SaveChangesAsync and cascade the failure to every doc remaining
                            // in this tick; clearing gives each doc a clean slate (a dropped retry
                            // heal is re-attempted naturally on the next qualifying tick). Bounding:
                            // this context spans every org in the tick, and saved rows would
                            // otherwise linger as Unchanged — with the catch-up window the tracker
                            // would grow with the whole day's send volume and each per-doc save's
                            // DetectChanges sweep would re-scan all of it. Nothing references the
                            // tracked entities past this point: every doc iteration re-queries its
                            // own priorLogs.
                            db.ChangeTracker.Clear();
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

    /// <summary>
    /// Deterministic Resend <c>Idempotency-Key</c> for one reminder send. Retries are
    /// at-least-once (ADR 0025): a transport throw after Resend accepted the mail, or a crash
    /// between the accept and the per-doc SaveChanges, leaves no usable record — the next
    /// qualifying tick re-attempts. That re-attempt recomputes the SAME key (its predecessor
    /// recorded nothing, so <paramref name="priorFailedAttemptAt"/> is unchanged) and Resend
    /// dedupes it server-side instead of double-delivering. A retry after a RECORDED failure
    /// salts the key with that attempt's <c>SentAt</c>, so it reaches Resend as a fresh request —
    /// Resend's docs don't specify whether an error response is cached against its key, and a
    /// genuine retry must never be served a cached error. Correct under either caching semantic.
    /// <para/>
    /// The recipient is ASCII-case-folded so the worker's case-insensitive recipient dedupe
    /// can't mint two keys for the same human. SHA-256 keeps the key short (73 chars, Resend
    /// cap 256) and recipient-opaque; Resend retains keys for 24h, and the SendDate component
    /// scopes the key to the org-local day regardless.
    /// </summary>
    internal static string BuildSendIdempotencyKey(
        Guid reminderId, Guid documentId, DateOnly sendDate, string recipient, DateTime? priorFailedAttemptAt)
    {
        var tuple = $"reminder:{reminderId:N}:{documentId:N}:{sendDate:yyyyMMdd}" +
                    $":{FoldAsciiLower(recipient)}:{priorFailedAttemptAt?.Ticks ?? 0}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tuple));
        return $"reminder-{Convert.ToHexStringLower(hash)}";
    }

    /// <summary>
    /// ASCII-only case folding for the idempotency-key recipient component: 'A'–'Z' lowercase,
    /// every other code point verbatim. Deliberately NOT <see cref="string.ToLowerInvariant"/>:
    /// the worker's recipient identity is OrdinalIgnoreCase (Unicode simple folding), under
    /// which e.g. U+0130 ('İ') is DISTINCT from 'i' and both get sent — but ToLowerInvariant
    /// maps U+0130 → 'i', which would mint the SAME key for two recipients the worker treats as
    /// different (worst case at Resend: the second send is replayed from the first and silently
    /// never delivered). ASCII folding merges every case-variant pair the dedupe merges in
    /// practice while guaranteeing two OrdinalIgnoreCase-distinct recipients never share a key;
    /// the residual edge for exotic fold-equal pairs flips to a bounded duplicate email on a
    /// cross-tick representation change — strictly better than silent non-delivery.
    /// </summary>
    private static string FoldAsciiLower(string s) =>
        string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });

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

    // HTML-encode every caller-influenced value before interpolating into the email HTML. The org
    // name is user-editable (#185) and file/vendor names come from uploads + vendor records; reminder
    // emails reach vendors (outside the org's trust boundary), so an unescaped value is a stored
    // HTML-injection sink. The portal URL is config BaseUrl + a base64url token (no HTML-significant
    // chars), matching the auth/portal-invite email pattern.

    /// <summary>The INTERNAL (team) reminder body — they have an account, so "review in CompliDrop".</summary>
    private static string BuildInternalBody(string orgName, Document doc, int daysBefore)
    {
        var expiration = doc.ExpirationDate?.ToString("MMMM d, yyyy") ?? "an upcoming date";
        var vendor = WebUtility.HtmlEncode(doc.Vendor?.Name ?? "a vendor");
        var fileName = WebUtility.HtmlEncode(doc.OriginalFileName);
        var org = WebUtility.HtmlEncode(orgName);
        return $"""
            <div style="font-family: system-ui, sans-serif; color: #0c4a6e;">
              <h2 style="color: #0284c7;">Compliance reminder</h2>
              <p>Hi there,</p>
              <p>Your document <strong>{fileName}</strong> from <strong>{vendor}</strong> expires on <strong>{expiration}</strong> — that's {daysBefore} days from today.</p>
              <p>Open {org} on CompliDrop to review it and collect the renewal.</p>
              <p style="color: #64748b; font-size: 12px;">Sent automatically by CompliDrop. Manage reminders on the Reminders page.</p>
            </div>
            """;
    }

    /// <summary>
    /// The VENDOR reminder body (#320 FP-092). Vendors have NO account, so it never says "log in":
    /// it embeds the secure upload link (no login needed) when the org has the portal, or — when it
    /// doesn't — asks them to send the renewal to the org. Never the "Settings → Reminders" footer
    /// (vendors have no app). <paramref name="portalUrl"/> is null when the org lacks the portal.
    /// </summary>
    private static string BuildVendorBody(string orgName, Document doc, int daysBefore, string? portalUrl)
    {
        var expiration = doc.ExpirationDate?.ToString("MMMM d, yyyy") ?? "an upcoming date";
        var fileName = WebUtility.HtmlEncode(doc.OriginalFileName);
        var org = WebUtility.HtmlEncode(orgName);
        var action = portalUrl is not null
            ? $"""
              <p>Please upload the renewal — no account or password needed:</p>
              <p><a href="{portalUrl}" style="display:inline-block;background:#0284c7;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;">Upload my renewal</a></p>
              <p style="color: #64748b; font-size: 12px;">Or paste this link into your browser:<br>{portalUrl}</p>
              """
            : $"<p>Please send your renewal to {org} so they can keep your file current.</p>";
        return $"""
            <div style="font-family: system-ui, sans-serif; color: #0c4a6e;">
              <h2 style="color: #0284c7;">Your document expires soon</h2>
              <p>Hi there,</p>
              <p>Your <strong>{fileName}</strong> on file with <strong>{org}</strong> expires on <strong>{expiration}</strong> — that's {daysBefore} days from today.</p>
              {action}
              <p style="color: #64748b; font-size: 12px;">Sent on behalf of {org} via CompliDrop.</p>
            </div>
            """;
    }

}
