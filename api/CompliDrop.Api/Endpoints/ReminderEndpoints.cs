using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Endpoints;

public static class ReminderEndpoints
{
    public static void MapReminderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/reminders").RequireAuthorization();

        group.MapGet("/", ListReminders);
        group.MapPut("/{id:guid}", UpdateReminder);
        group.MapGet("/history", ListHistory);
        group.MapGet("/gaps", ListGaps);

        app.MapPost("/api/reminders/resend-webhook", ResendWebhook);
    }

    private static async Task<IResult> ListReminders(AppDbContext db, CancellationToken ct)
    {
        var reminders = await db.Reminders
            .OrderBy(r => r.DaysBefore)
            .Select(r => new
            {
                id = r.Id,
                daysBefore = r.DaysBefore,
                notifyInternalUser = r.NotifyInternalUser,
                notifyVendor = r.NotifyVendor,
                isActive = r.IsActive,
                emailSubjectTemplate = r.EmailSubjectTemplate
            })
            .ToListAsync(ct);
        return Results.Ok(new { data = reminders, error = (object?)null });
    }

    private static async Task<IResult> UpdateReminder(
        Guid id,
        ReminderUpdateRequest req,
        AppDbContext db,
        CancellationToken ct)
    {
        var r = await db.Reminders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();
        r.NotifyInternalUser = req.NotifyInternalUser;
        r.NotifyVendor = req.NotifyVendor;
        r.IsActive = req.IsActive;
        r.EmailSubjectTemplate = req.EmailSubjectTemplate;
        // Reminder.EmailBodyTemplate is deliberately NOT on the API surface: the send path
        // (ReminderBackgroundService.BuildBody) never read it, so accepting/returning it was a
        // stored-but-ignored lie (#264 / FP-095). The DB column stays dormant until the #241
        // recipient-aware email rewrite decides whether to honor or drop it.
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> ListHistory(AppDbContext db, CancellationToken ct)
    {
        // ReminderLog carries a denormalized OrganizationId with its own AppDbContext tenant query
        // filter (l.OrganizationId == CurrentOrgId) since #309, so the org scoping is implicit here —
        // no EXISTS join against Reminders. The query resolves to WHERE "OrganizationId" = @org
        // ORDER BY "SentAt" DESC LIMIT 200, served by the (OrganizationId, SentAt DESC) index as a
        // range scan (no whole-table scan, no top-N sort). The tenant-isolation guarantee that #242
        // established — an org never sees another org's reminder-log recipients/ids — is preserved by
        // the filter and pinned by TenantIsolationTests.Reminder_history_returns_only_the_callers_org_logs.
        var logs = await db.ReminderLogs
            .OrderByDescending(l => l.SentAt)
            .Take(200)
            .Select(l => new
            {
                id = l.Id,
                recipient = l.RecipientEmail,
                sentAt = l.SentAt,
                sendDate = l.SendDate,
                status = l.Status,
                resendMessageId = l.ResendMessageId,
                reminderId = l.ReminderId,
                documentId = l.DocumentId,
                // FP-090: name WHICH document (+ vendor + rung) the reminder was about — "ops@acme —
                // Delivered" alone is unactionable. Correlated subqueries so it stays one query; a
                // soft-deleted doc / removed reminder resolves to null and the UI shows a fallback.
                documentName = db.Documents.Where(d => d.Id == l.DocumentId).Select(d => d.OriginalFileName).FirstOrDefault(),
                vendorName = db.Documents.Where(d => d.Id == l.DocumentId).Select(d => d.Vendor!.Name).FirstOrDefault(),
                daysBefore = db.Reminders.Where(r => r.Id == l.ReminderId).Select(r => (int?)r.DaysBefore).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return Results.Ok(new { data = logs, error = (object?)null });
    }

    /// <summary>
    /// Silent no-send disclosure (#320 FP-091): vendors with no contact email never receive a
    /// reminder, and documents with no expiration date never match a reminder window — both happen
    /// without a trace today. Surface the counts so Pat can see (and fix) the gap. Disclosure ONLY —
    /// the catch-up/failed-retry send-semantics are #270. "Documents without an expiration date" is
    /// scoped to fully-READ docs so an in-flight upload (no date yet) isn't miscounted as a gap.
    /// </summary>
    private static async Task<IResult> ListGaps(AppDbContext db, CancellationToken ct)
    {
        var vendorsWithoutEmail = await db.Vendors
            .CountAsync(v => v.ContactEmail == null || v.ContactEmail == "", ct);
        var documentsWithoutExpiration = await db.Documents
            .CountAsync(d => d.ExpirationDate == null && d.ExtractionStatus == ExtractionStatus.Completed, ct);
        return Results.Ok(new
        {
            data = new { vendorsWithoutEmail, documentsWithoutExpiration },
            error = (object?)null,
        });
    }

    private static async Task<IResult> ResendWebhook(
        HttpContext http,
        SystemDbContext db,
        IOptions<ResendSettings> resendOptions,
        IHostEnvironment env,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body);
        var raw = await reader.ReadToEndAsync(ct);

        var logger = loggerFactory.CreateLogger("ResendWebhook");
        var secret = resendOptions.Value.WebhookSecret;

        // Webhook signature verification (Svix scheme — Resend signs via Svix). Must run on the raw
        // body BEFORE parsing or mutating any state. See CLAUDE.md "Resend:WebhookSecret".
        switch (ResolveSecretPolicy(secret, env.IsDevelopment()))
        {
            case SecretPolicy.RejectUnconfigured:
                logger.LogWarning("Resend webhook rejected: Resend:WebhookSecret is not configured.");
                return Results.Unauthorized();

            case SecretPolicy.SkipInDevelopment:
                logger.LogWarning("Resend webhook signature check SKIPPED — Resend:WebhookSecret not configured (Development only).");
                break;

            case SecretPolicy.Verify:
                var verification = SvixWebhookVerifier.Verify(
                    raw,
                    http.Request.Headers["svix-id"].FirstOrDefault(),
                    http.Request.Headers["svix-timestamp"].FirstOrDefault(),
                    http.Request.Headers["svix-signature"].FirstOrDefault(),
                    secret!,
                    timeProvider.GetUtcNow());
                if (verification != SvixWebhookVerifier.Result.Valid)
                {
                    logger.LogWarning("Resend webhook rejected: signature verification failed ({Result}).", verification);
                    return Results.Unauthorized();
                }
                break;
        }

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            // Signed (or dev-skipped) but unparseable body — nothing actionable.
            return Results.BadRequest();
        }
        using var _ = doc;
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var data = root.TryGetProperty("data", out var d) ? d : default;
        var messageId = data.ValueKind != System.Text.Json.JsonValueKind.Undefined
                        && data.TryGetProperty("email_id", out var eid)
            ? eid.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(type))
            return Results.Ok(new { data = (object?)null, error = (object?)null });

        var status = type switch
        {
            "email.delivered" => ReminderLogStatus.Delivered,
            "email.bounced" => ReminderLogStatus.Bounced,
            "email.complained" => ReminderLogStatus.Complained,
            "email.opened" => ReminderLogStatus.Opened,
            "email.clicked" => ReminderLogStatus.Clicked,
            _ => null
        };
        if (status is null) return Results.Ok(new { data = (object?)null, error = (object?)null });

        // No explicit event-id dedupe (unlike the Stripe webhook's ProcessedStripeEvent): the
        // status mutation is gated by an atomic conditional UPDATE whose WHERE clause encodes the
        // lifecycle precedence rule (ReminderStatusPrecedence.CurrentStatusesToIgnore). This is
        //   - idempotent under redelivery (same status → block list contains it → 0 rows),
        //   - ordering-aware (a late lower-rank positive or a positive-after-negative is excluded
        //     by the WHERE clause → 0 rows, so the displayed state cannot regress),
        //   - race-free under concurrent deliveries for the same ResendMessageId (Postgres
        //     serializes the row updates and re-evaluates the second UPDATE's WHERE against the
        //     first's committed value, per Read Committed semantics — so even an overlapping
        //     bounced + opened pair settles deterministically on the negative).
        // See ReminderStatusPrecedence for the rule and its proof of agreement with ShouldApply.
        var ignoreCurrent = ReminderStatusPrecedence.CurrentStatusesToIgnore(status);
        await db.ReminderLogs
            .Where(l => l.ResendMessageId == messageId && !ignoreCurrent.Contains(l.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, status), ct);
        return Results.Ok(new { data = (object?)null, error = (object?)null });
    }

    /// <summary>
    /// Policy for the inbound webhook when no signing secret is configured. Unlike the Stripe
    /// webhook (which rejects an unset secret unconditionally in every environment), the Resend
    /// webhook deliberately allows unsigned requests in Development only — so local end-to-end
    /// testing isn't blocked — while still failing closed (rejecting) everywhere else. This
    /// divergence is documented in CLAUDE.md.
    /// </summary>
    internal enum SecretPolicy { Verify, SkipInDevelopment, RejectUnconfigured }

    internal static SecretPolicy ResolveSecretPolicy(string? secret, bool isDevelopment) =>
        !string.IsNullOrWhiteSpace(secret) ? SecretPolicy.Verify
        : isDevelopment ? SecretPolicy.SkipInDevelopment
        : SecretPolicy.RejectUnconfigured;
}

public record ReminderUpdateRequest(
    bool NotifyInternalUser,
    bool NotifyVendor,
    bool IsActive,
    string? EmailSubjectTemplate);
