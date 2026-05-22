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

        // AsNoTracking on read-only queries: this worker only writes ReminderLog inserts (which
        // are tracked explicitly via db.ReminderLogs.Add). Tracking orgs / reminders / docs /
        // users buys us nothing and inflates the change tracker.
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

            // Resolved once per org so the worker doesn't re-throw on TZ lookup inside the
            // reminder loop; ToLocal already proved the id resolves.
            var orgTzInfo = TryFindTimeZone(org.TimeZone);

            // Hoisted out of the reminder loop: the internal-user list is identical for every
            // reminder under the same org, so we resolve it once and reuse. OrderBy keeps the
            // recipient order stable across runs (Postgres returns rows in physical order
            // otherwise), which keeps multi-user assertions in tests deterministic.
            var internalUsers = reminders.Any(r => r.NotifyInternalUser)
                ? await db.Users
                    .AsNoTracking()
                    .Where(u => u.OrganizationId == org.Id && u.DeletedAt == null)
                    .OrderBy(u => u.Email)
                    .Select(u => u.Email)
                    .ToListAsync(ct)
                : new List<string>();

            foreach (var reminder in reminders)
            {
                var targetDate = localDate.AddDays(reminder.DaysBefore);
                // Bracket the org's local target day, converted to UTC via the org's own TZ.
                // The previous .ToUniversalTime() on an Unspecified-kind value silently used the
                // SERVER's local zone, so the window matched a different UTC range depending on
                // where the worker ran. Going through TimeZoneInfo.ConvertTimeToUtc makes the
                // window host-independent and aligned with the org's wall clock.
                var targetStart = orgTzInfo is null
                    ? DateTime.SpecifyKind(targetDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
                    : TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(targetDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified),
                        orgTzInfo);
                var targetEnd = orgTzInfo is null
                    ? DateTime.SpecifyKind(targetDate.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc)
                    : TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(targetDate.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Unspecified),
                        orgTzInfo);

                var docs = await db.Documents
                    .AsNoTracking()
                    .Where(d => d.OrganizationId == org.Id
                                && d.DeletedAt == null
                                && d.ExpirationDate >= targetStart
                                && d.ExpirationDate <= targetEnd)
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

                    var sendDate = DateOnly.FromDateTime(nowUtc);

                    // Pull the set of recipients we've already logged for this (reminder, doc,
                    // day) so multi-recipient reminders dedupe per recipient instead of skipping
                    // the doc once any recipient has been sent.
                    var alreadySent = (await db.ReminderLogs
                        .Where(l => l.ReminderId == reminder.Id
                                    && l.DocumentId == doc.Id
                                    && l.SendDate == sendDate)
                        .Select(l => l.RecipientEmail)
                        .ToListAsync(ct))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var recipient in recipients)
                    {
                        if (alreadySent.Contains(recipient)) continue;

                        try
                        {
                            var subject = reminder.EmailSubjectTemplate
                                          ?? $"[{org.Name}] {doc.OriginalFileName} expires in {reminder.DaysBefore} days";
                            var body = BuildBody(org.Name, doc, reminder.DaysBefore);
                            var messageId = await email.SendAsync(recipient, subject, body, ct);

                            db.ReminderLogs.Add(new ReminderLog
                            {
                                Id = Guid.NewGuid(),
                                ReminderId = reminder.Id,
                                DocumentId = doc.Id,
                                RecipientEmail = recipient,
                                SentAt = timeProvider.GetUtcNow().UtcDateTime,
                                SendDate = sendDate,
                                ResendMessageId = messageId,
                                Status = messageId is null ? "failed" : "sent"
                            });
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed sending reminder for doc {DocumentId}", doc.Id);
                        }
                    }

                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateException ex)
                    {
                        logger.LogWarning(ex, "Reminder log upsert conflict (likely concurrent tick) for doc {DocumentId}", doc.Id);

                        // After a failed SaveChanges the pending Added ReminderLog entries stay
                        // in the change tracker. The next doc iteration's SaveChangesAsync would
                        // re-attempt them and throw again — cascading the failure to every doc
                        // remaining in this tick. Detach them so each doc gets a clean slate.
                        foreach (var entry in db.ChangeTracker.Entries<ReminderLog>()
                                     .Where(e => e.State == EntityState.Added)
                                     .ToList())
                        {
                            entry.State = EntityState.Detached;
                        }
                    }
                }
            }
        }
    }

    private static bool IsLocalSendWindow(string tz, DateTime nowUtc)
    {
        var info = TryFindTimeZone(tz);
        if (info is null) return nowUtc.Hour == TargetLocalHour;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, info);
        return local.Hour == TargetLocalHour;
    }

    private static DateTime ToLocal(string tz, DateTime utc)
    {
        var info = TryFindTimeZone(tz);
        return info is null ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, info);
    }

    /// <summary>Returns the named IANA / Windows zone, or null for an unknown id.</summary>
    private static TimeZoneInfo? TryFindTimeZone(string tz)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    private static string BuildBody(string orgName, Document doc, int daysBefore)
    {
        var expiration = doc.ExpirationDate?.ToString("MMMM d, yyyy") ?? "an upcoming date";
        var vendor = doc.Vendor?.Name ?? "a vendor";
        return $"""
            <div style="font-family: system-ui, sans-serif; color: #0c4a6e;">
              <h2 style="color: #0284c7;">Compliance reminder</h2>
              <p>Hi there,</p>
              <p>Your document <strong>{doc.OriginalFileName}</strong> from <strong>{vendor}</strong> expires on <strong>{expiration}</strong> — that's {daysBefore} days from today.</p>
              <p>Log in to {orgName} on CompliDrop to review and upload the renewal.</p>
              <p style="color: #64748b; font-size: 12px;">Sent automatically by CompliDrop. You can adjust reminder cadence in Settings → Reminders.</p>
            </div>
            """;
    }
}
