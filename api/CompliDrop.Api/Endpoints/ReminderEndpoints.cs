using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

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
        CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body);
        var raw = await reader.ReadToEndAsync(ct);

        using var doc = System.Text.Json.JsonDocument.Parse(raw);
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
