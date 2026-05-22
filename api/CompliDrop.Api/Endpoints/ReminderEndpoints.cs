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
                emailSubjectTemplate = r.EmailSubjectTemplate,
                emailBodyTemplate = r.EmailBodyTemplate
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
        r.EmailBodyTemplate = req.EmailBodyTemplate;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> ListHistory(AppDbContext db, CancellationToken ct)
    {
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
                documentId = l.DocumentId
            })
            .ToListAsync(ct);
        return Results.Ok(new { data = logs, error = (object?)null });
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
        if (string.IsNullOrWhiteSpace(secret))
        {
            // No signing secret configured: reject in production; allow (with a warning) only in
            // Development so local end-to-end testing isn't blocked.
            if (!env.IsDevelopment())
            {
                logger.LogWarning("Resend webhook rejected: Resend:WebhookSecret is not configured.");
                return Results.Unauthorized();
            }
            logger.LogWarning("Resend webhook signature check SKIPPED — Resend:WebhookSecret not configured (Development only).");
        }
        else
        {
            var verification = SvixWebhookVerifier.Verify(
                raw,
                http.Request.Headers["svix-id"].FirstOrDefault(),
                http.Request.Headers["svix-timestamp"].FirstOrDefault(),
                http.Request.Headers["svix-signature"].FirstOrDefault(),
                secret,
                timeProvider.GetUtcNow());

            if (verification != SvixWebhookVerifier.Result.Valid)
            {
                logger.LogWarning("Resend webhook rejected: signature verification failed ({Result}).", verification);
                return Results.Unauthorized();
            }
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
            "email.delivered" => "delivered",
            "email.bounced" => "bounced",
            "email.complained" => "complained",
            "email.opened" => "opened",
            "email.clicked" => "clicked",
            _ => null
        };
        if (status is null) return Results.Ok(new { data = (object?)null, error = (object?)null });

        var log = await db.ReminderLogs.FirstOrDefaultAsync(l => l.ResendMessageId == messageId, ct);
        if (log is not null)
        {
            log.Status = status;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { data = (object?)null, error = (object?)null });
    }
}

public record ReminderUpdateRequest(
    bool NotifyInternalUser,
    bool NotifyVendor,
    bool IsActive,
    string? EmailSubjectTemplate,
    string? EmailBodyTemplate);
