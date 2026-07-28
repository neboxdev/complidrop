using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CompliDrop.Api.Services;

/// <summary>
/// The <c>WaitlistEntry.Email</c> unique-index contract, shared by the schema and the endpoint that
/// catches its violation (#389). Same shape as <see cref="IdempotencyService.KeyIndexName"/> /
/// <see cref="IdempotencyService.IsKeyConflict"/>: the concurrent-duplicate 23505 is recognized by the
/// INDEX name rather than by the SqlState alone, so an unrelated unique violation is never swallowed as
/// a duplicate signup.
/// <para/>
/// It lives in <c>Services/</c>, not on <c>WaitlistEndpoints</c>, because <c>ModelConfiguration</c>
/// consumes the name too (<c>HasDatabaseName</c>, so schema and matcher agree by construction) — and a
/// constant owned by the endpoint layer made the DATA layer compile against
/// <c>Endpoints</c>, the only such dependency in the assembly. Both sides now read it from here, the
/// direction every other shared predicate in this folder already runs (#389 review).
/// <para/>
/// The value is EF's own default name for the index, so pinning it needed no migration. It is checked
/// against what Postgres ACTUALLY reports by a catalog-reading test — the EF-model form would be
/// vacuous here, since <c>ModelConfiguration</c> takes the name FROM this constant.
/// </summary>
public static class WaitlistSignup
{
    /// <summary>
    /// The database name of the unique index on <c>WaitlistEntries.Email</c>, which Npgsql reports as
    /// <see cref="PostgresException.ConstraintName"/> on a 23505.
    /// </summary>
    public const string EmailUniqueIndexName = "IX_WaitlistEntries_Email";

    /// <summary>
    /// True when <paramref name="ex"/> is the duplicate-address violation the signup race produces —
    /// and nothing else.
    /// </summary>
    public static bool IsDuplicateEmail(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, EmailUniqueIndexName, StringComparison.Ordinal);
}
