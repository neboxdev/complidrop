using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Services;

public interface IIdempotencyService
{
    Task<IdempotencyHit?> TryGetAsync(Guid organizationId, string key, CancellationToken ct);
    Task StoreAsync(Guid organizationId, string key, string requestPath, int statusCode, object? responseBody, CancellationToken ct);
}

public record IdempotencyHit(int StatusCode, string? ResponseJson);

public class IdempotencyService(SystemDbContext db) : IIdempotencyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<IdempotencyHit?> TryGetAsync(Guid organizationId, string key, CancellationToken ct)
    {
        var record = await db.IdempotencyRecords
            .Where(i => i.OrganizationId == organizationId && i.Key == key && i.ExpiresAt > DateTime.UtcNow)
            .FirstOrDefaultAsync(ct);
        return record is null ? null : new IdempotencyHit(record.StatusCode, record.ResponseJson);
    }

    public async Task StoreAsync(Guid organizationId, string key, string requestPath, int statusCode, object? responseBody, CancellationToken ct)
    {
        var existing = await db.IdempotencyRecords
            .FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.Key == key, ct);
        if (existing is not null) return;

        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Key = key,
            RequestPath = requestPath,
            StatusCode = statusCode,
            ResponseJson = responseBody is null ? null : JsonSerializer.Serialize(responseBody, JsonOptions),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        await db.SaveChangesAsync(ct);
    }
}
