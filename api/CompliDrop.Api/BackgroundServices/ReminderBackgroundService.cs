using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.BackgroundServices;

public class ReminderBackgroundService(
    IServiceScopeFactory scopeFactory,
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

            var now = DateTime.UtcNow;
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

        var nowUtc = DateTime.UtcNow;

        var orgs = await db.Organizations
            .Where(o => o.DeletedAt == null)
            .Select(o => new { o.Id, o.Name, o.TimeZone })
            .ToListAsync(ct);

        foreach (var org in orgs)
        {
            if (!IsLocalSendWindow(org.TimeZone, nowUtc)) continue;

            var reminders = await db.Reminders
                .Where(r => r.OrganizationId == org.Id && r.IsActive)
                .ToListAsync(ct);
            if (reminders.Count == 0) continue;

            var localDate = DateOnly.FromDateTime(ToLocal(org.TimeZone, nowUtc));

            foreach (var reminder in reminders)
            {
                var targetDate = localDate.AddDays(reminder.DaysBefore);
                var targetStart = targetDate.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
                var targetEnd = targetDate.ToDateTime(TimeOnly.MaxValue).ToUniversalTime();

                var docs = await db.Documents
                    .Where(d => d.OrganizationId == org.Id
                                && d.DeletedAt == null
                                && d.ExpirationDate >= targetStart
                                && d.ExpirationDate <= targetEnd)
                    .Include(d => d.Vendor)
                    .ToListAsync(ct);
                if (docs.Count == 0) continue;

                var internalUsers = reminder.NotifyInternalUser
                    ? await db.Users
                        .Where(u => u.OrganizationId == org.Id && u.DeletedAt == null)
                        .Select(u => u.Email)
                        .ToListAsync(ct)
                    : new List<string>();

                foreach (var doc in docs)
                {
                    var recipients = new List<string>(internalUsers);
                    if (reminder.NotifyVendor && !string.IsNullOrWhiteSpace(doc.Vendor?.ContactEmail))
                        recipients.Add(doc.Vendor.ContactEmail);
                    recipients = recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
                    if (recipients.Count == 0) continue;

                    var sendDate = DateOnly.FromDateTime(nowUtc);
                    var alreadySent = await db.ReminderLogs.AnyAsync(l =>
                        l.ReminderId == reminder.Id
                        && l.DocumentId == doc.Id
                        && l.SendDate == sendDate, ct);
                    if (alreadySent) continue;

                    foreach (var recipient in recipients)
                    {
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
                                SentAt = DateTime.UtcNow,
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
                    }
                }
            }
        }
    }

    private static bool IsLocalSendWindow(string tz, DateTime nowUtc)
    {
        try
        {
            var info = TimeZoneInfo.FindSystemTimeZoneById(tz);
            var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, info);
            return local.Hour == TargetLocalHour;
        }
        catch
        {
            // Unknown tz falls back to UTC 08:00.
            return nowUtc.Hour == TargetLocalHour;
        }
    }

    private static DateTime ToLocal(string tz, DateTime utc)
    {
        try
        {
            var info = TimeZoneInfo.FindSystemTimeZoneById(tz);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, info);
        }
        catch
        {
            return utc;
        }
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
