using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CompliDrop.Api.Services;

public interface IIdempotencyService
{
    /// <summary>
    /// Fast-path replay: the cached response for an existing record under (orgId, key), or null if
    /// none exists. A committed record is a permanent idempotency claim — see <see cref="IdempotencyService"/>.
    /// </summary>
    Task<IdempotencyHit?> TryGetAsync(Guid organizationId, string key, CancellationToken ct);

    /// <summary>
    /// Builds the dedupe record the caller ADDS to its OWN DbContext, to be committed in the SAME
    /// <c>SaveChanges</c> as the side-effect entity (the new Document / sample). Co-committing makes the
    /// <c>(OrganizationId, Key)</c> unique index an atomic claim: a CONCURRENT same-key request's commit
    /// fails with a unique violation (detected via <see cref="IsKeyConflict"/>) instead of landing a
    /// second side effect. Replaces the old check-then-store, which ran the handler before the insert and
    /// so let two concurrent requests both execute (#336).
    /// </summary>
    IdempotencyRecord BuildRecord(Guid organizationId, string key, string requestPath, int statusCode, object? responseBody);

    /// <summary>
    /// True when the exception is the <c>(OrganizationId, Key)</c> idempotency unique-index violation —
    /// i.e. a concurrent request already committed this exact key. Matches on the INDEX name (not just
    /// SqlState) so it never swallows an unrelated unique violation firing in the same transaction (e.g.
    /// the sample partial index in <c>SampleEndpoints</c>).
    /// </summary>
    bool IsKeyConflict(DbUpdateException ex);
}

public record IdempotencyHit(int StatusCode, string? ResponseJson);

/// <summary>
/// Idempotency for mutating POSTs (document upload, sample seed, billing checkout). The dedupe record is
/// written in the SAME transaction as the request's side effect, so the <c>(OrganizationId, Key)</c>
/// unique index is an airtight concurrent-duplicate backstop: of two in-flight same-key requests exactly
/// one commit wins, and the loser catches the unique violation and replays the winner's response (the
/// model the sample endpoint already used via its own partial unique index — generalized here, #336).
/// A committed record is a PERMANENT claim that replays for as long as the row exists (no expiry filter
/// on read): clients mint a single-use key per action, so "replay forever" can never mask a legitimate
/// retry, and it guarantees a record is never "present but ignored" — which would otherwise let a
/// same-key insert conflict with a row <see cref="TryGetAsync"/> refuses to replay, wedging the key.
/// <c>ExpiresAt</c> is retained purely as a future garbage-collection hint. See ADR 0029.
/// </summary>
public class IdempotencyService(SystemDbContext db) : IIdempotencyService
{
    /// <summary>
    /// The EF-generated name of the <c>(OrganizationId, Key)</c> unique index, which Npgsql reports as
    /// <see cref="PostgresException.ConstraintName"/> on a 23505. Pinned by a test so a future index
    /// rename can't silently turn the concurrent-loser replay path back into an unhandled 500.
    /// </summary>
    public const string KeyIndexName = "IX_IdempotencyRecords_OrganizationId_Key";

    private const int TtlHours = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<IdempotencyHit?> TryGetAsync(Guid organizationId, string key, CancellationToken ct) =>
        await db.IdempotencyRecords
            .AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && i.Key == key)
            .Select(i => new IdempotencyHit(i.StatusCode, i.ResponseJson))
            .FirstOrDefaultAsync(ct);

    public IdempotencyRecord BuildRecord(Guid organizationId, string key, string requestPath, int statusCode, object? responseBody) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Key = key,
            RequestPath = requestPath,
            StatusCode = statusCode,
            ResponseJson = responseBody is null ? null : JsonSerializer.Serialize(responseBody, JsonOptions),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(TtlHours)
        };

    public bool IsKeyConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, KeyIndexName, StringComparison.Ordinal);
}
