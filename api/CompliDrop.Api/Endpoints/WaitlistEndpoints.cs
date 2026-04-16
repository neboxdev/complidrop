using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Waitlist;
using CompliDrop.Api.Entities;
using Microsoft.AspNetCore.RateLimiting;
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

            var exists = await db.WaitlistEntries.AnyAsync(e => e.Email == email);
            if (exists)
            {
                return Results.Ok(new
                {
                    data = new { message = "You're on the list!" },
                    error = (object?)null
                });
            }

            db.WaitlistEntries.Add(new WaitlistEntry
            {
                Id = Guid.NewGuid(),
                Email = email,
                CompanyName = request.CompanyName,
                Industry = request.Industry,
                Source = request.Source,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                data = new { message = "You're on the list!" },
                error = (object?)null
            });
        })
        .RequireRateLimiting("waitlist");
    }
}
