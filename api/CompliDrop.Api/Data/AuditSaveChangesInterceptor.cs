using System.Text.Json;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CompliDrop.Api.Data;

public class AuditSaveChangesInterceptor(Func<ICurrentUser?> currentUserAccessor) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> NonAuditedTypes = new(StringComparer.Ordinal)
    {
        nameof(AuditLog),
        nameof(IdempotencyRecord),
        nameof(ProcessedStripeEvent),
        nameof(WaitlistEntry),
        // Short-lived auth infra (#184). Excluded so the interceptor never
        // serializes TokenHash into AuditLog.AfterJson on the authenticated
        // resend path — the entity's hash-only-storage contract must hold in the
        // audit log too. The meaningful events are already covered by explicit
        // IAuditLogger calls ("user.registered", "user.email_verified").
        nameof(EmailVerificationToken)
    };

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Apply(eventData);
        return base.SavingChanges(eventData, result);
    }

    private void Apply(DbContextEventData eventData)
    {
        var context = eventData.Context;
        if (context is null) return;

        var now = DateTime.UtcNow;
        var currentUser = currentUserAccessor();
        var pendingAudits = new List<AuditLog>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            var entityTypeName = entry.Entity.GetType().Name;

            SetUpdatedAtIfPresent(entry, now);

            if (entry.State == EntityState.Deleted && HasDeletedAtProperty(entry))
            {
                entry.State = EntityState.Modified;
                entry.Property("DeletedAt").CurrentValue = now;
            }

            if (NonAuditedTypes.Contains(entityTypeName)) continue;
            if (currentUser is null) continue;

            var audit = BuildAudit(entry, entityTypeName, currentUser, now);
            if (audit is not null) pendingAudits.Add(audit);
        }

        if (pendingAudits.Count == 0) return;

        switch (context)
        {
            case AppDbContext appDb: appDb.AuditLogs.AddRange(pendingAudits); break;
            case SystemDbContext sysDb: sysDb.AuditLogs.AddRange(pendingAudits); break;
        }
    }

    private static void SetUpdatedAtIfPresent(EntityEntry entry, DateTime now)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified)) return;
        var prop = entry.Metadata.FindProperty("UpdatedAt");
        if (prop is null) return;
        entry.Property("UpdatedAt").CurrentValue = now;
    }

    private static bool HasDeletedAtProperty(EntityEntry entry) =>
        entry.Metadata.FindProperty("DeletedAt") is not null;

    private static AuditLog? BuildAudit(
        EntityEntry entry,
        string entityType,
        ICurrentUser currentUser,
        DateTime now)
    {
        if (currentUser.OrganizationId is null || currentUser.OrganizationId == Guid.Empty) return null;

        string action;
        string? beforeJson = null;
        string? afterJson = null;

        switch (entry.State)
        {
            case EntityState.Added:
                action = $"{entityType.ToLowerInvariant()}.created";
                afterJson = SerializeSnapshot(entry, current: true);
                break;
            case EntityState.Modified:
                var isSoftDelete = HasDeletedAtProperty(entry)
                    && entry.Property("DeletedAt").CurrentValue is not null
                    && entry.Property("DeletedAt").OriginalValue is null;
                action = isSoftDelete
                    ? $"{entityType.ToLowerInvariant()}.deleted"
                    : $"{entityType.ToLowerInvariant()}.updated";
                beforeJson = SerializeSnapshot(entry, current: false);
                afterJson = SerializeSnapshot(entry, current: true);
                break;
            default:
                return null;
        }

        var entityIdObj = entry.Metadata.FindPrimaryKey()?.Properties
            .FirstOrDefault()?.PropertyInfo?.GetValue(entry.Entity);
        var entityId = entityIdObj is Guid g ? g : (Guid?)null;

        return new AuditLog
        {
            Id = Guid.NewGuid(),
            OrganizationId = currentUser.OrganizationId.Value,
            UserId = currentUser.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            IpAddress = currentUser.IpAddress,
            UserAgent = currentUser.UserAgent,
            CorrelationId = currentUser.CorrelationId,
            CreatedAt = now
        };
    }

    private static string SerializeSnapshot(EntityEntry entry, bool current)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in entry.Properties)
        {
            if (prop.Metadata.IsShadowProperty()) continue;
            var value = current ? prop.CurrentValue : prop.OriginalValue;
            if (value is byte[] || value is System.Text.Json.JsonDocument) continue;
            dict[prop.Metadata.Name] = value;
        }
        return JsonSerializer.Serialize(dict, JsonOptions);
    }
}
