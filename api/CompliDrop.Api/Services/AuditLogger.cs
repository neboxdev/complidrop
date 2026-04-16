using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

public interface IAuditLogger
{
    Task LogAsync(
        string action,
        string entityType,
        Guid? entityId,
        object? before = null,
        object? after = null,
        Guid? organizationIdOverride = null,
        Guid? userIdOverride = null,
        CancellationToken ct = default);
}

public class AuditLogger(
    SystemDbContext db,
    ICurrentUser currentUser) : IAuditLogger
{
    public async Task LogAsync(
        string action,
        string entityType,
        Guid? entityId,
        object? before = null,
        object? after = null,
        Guid? organizationIdOverride = null,
        Guid? userIdOverride = null,
        CancellationToken ct = default)
    {
        var orgId = organizationIdOverride ?? currentUser.OrganizationId;
        if (orgId is null || orgId == Guid.Empty) return;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId.Value,
            UserId = userIdOverride ?? currentUser.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = before is null ? null : System.Text.Json.JsonSerializer.Serialize(before),
            AfterJson = after is null ? null : System.Text.Json.JsonSerializer.Serialize(after),
            IpAddress = currentUser.IpAddress,
            UserAgent = currentUser.UserAgent,
            CorrelationId = currentUser.CorrelationId,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
    }
}
