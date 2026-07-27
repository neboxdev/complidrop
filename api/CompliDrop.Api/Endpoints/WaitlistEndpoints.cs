using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Waitlist;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Endpoints;

public static class WaitlistEndpoints
{
    public static void MapWaitlistEndpoints(this WebApplication app)
    {
        app.MapPost("/api/waitlist", async (WaitlistRequest request, SystemDbContext db) =>
        {
            var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                return Results.BadRequest(new
                {
                    data = (object?)null,
                    error = new { code = "validation.email", message = "A valid email is required." }
                });
            }

            // ANONYMOUS route: every one of these lands verbatim in a bounded varchar, and Npgsql does
            // not truncate — before #389 an over-length value failed the whole SaveChanges with a
            // Postgres 22001 and surfaced as a generic 500. Reject the three the visitor TYPED so they
            // can shorten and retry; the length is measured on the value actually written (the
            // normalized email, not the raw property). See ADR 0046 for the reject-vs-clamp split.
            if (InputLength.FirstViolation(
                    (email, InputLengths.WaitlistEmail, "Email"),
                    (request.CompanyName, InputLengths.WaitlistCompanyName, "Company name"),
                    (request.Industry, InputLengths.WaitlistIndustry, "Industry")) is { } tooLong)
                return tooLong;

            // Source is the exception: an attribution tag the PAGE sets, never something the visitor
            // typed or reads back, so refusing a signup over it would punish the visitor for the
            // caller's bug. Clamp it (ColumnClamp.To — the one surrogate-safe truncation, ADR 0044).
            var source = ColumnClamp.To(request.Source, InputLengths.WaitlistSource);

            var exists = await db.WaitlistEntries.AnyAsync(e => e.Email == email);
            if (exists) return AlreadyOnTheList();

            db.WaitlistEntries.Add(new WaitlistEntry
            {
                Id = Guid.NewGuid(),
                Email = email,
                CompanyName = request.CompanyName,
                Industry = request.Industry,
                Source = source,
                CreatedAt = DateTime.UtcNow
            });

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (WaitlistSignup.IsDuplicateEmail(ex))
            {
                // The check above is a read-then-write TOCTOU: two concurrent submissions of the same
                // address both see "not on the list" and both insert, and the loser's commit hits the
                // (Email) unique index. That is a 500 on a public marketing form for a visitor who did
                // nothing wrong — and the honest answer is the one the sequential duplicate already
                // gets, because by the time the loser fails the address IS on the list. Same
                // catch-the-unique-violation-and-replay shape the idempotency layer uses (ADR 0029).
                //
                // Deliberately NOT fixed by ADDING an index: the unique index already exists, so this
                // needs no schema change. (Creating one over a table that may already hold duplicates
                // would fail the startup auto-migration and take production down — ADR 0016.)
                return AlreadyOnTheList();
            }

            return AlreadyOnTheList();
        })
        .RequireRateLimiting("waitlist");
    }

    /// <summary>
    /// The single friendly success body — identical for a first signup, a repeat signup, and the loser
    /// of a concurrent duplicate race, so a visitor can never tell (or be 500'd by) which one they hit.
    /// </summary>
    private static IResult AlreadyOnTheList() =>
        Results.Ok(new
        {
            data = new { message = "You're on the list!" },
            error = (object?)null
        });
}
