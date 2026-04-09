using System.Net.Mail;
using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Waitlist;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Endpoints;

public static class WaitlistEndpoints
{
    public static void MapWaitlistEndpoints(this WebApplication app)
    {
        app.MapPost("/api/waitlist", async (WaitlistRequest request, AppDbContext db) =>
        {
            var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;

            if (!MailAddress.TryCreate(email, out _))
            {
                return Results.BadRequest(new
                {
                    error = new { message = "Valid email is required.", code = "invalid_email" }
                });
            }

            var exists = await db.WaitlistEntries.AnyAsync(e => e.Email == email);
            if (exists)
            {
                return Results.Conflict(new
                {
                    error = new { message = "Email already registered.", code = "duplicate_email" }
                });
            }

            var entry = new WaitlistEntry
            {
                Id = Guid.NewGuid(),
                Email = email,
                CompanyName = request.CompanyName,
                Industry = request.Industry,
                Source = request.Source,
                CreatedAt = DateTime.UtcNow
            };

            db.WaitlistEntries.Add(entry);
            await db.SaveChangesAsync();

            return Results.Created($"/api/waitlist/{entry.Id}", new
            {
                data = new { id = entry.Id, email = entry.Email }
            });
        });
    }
}
