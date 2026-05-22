using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for <see cref="ReminderBackgroundService"/>: drives
/// <c>ProcessHourlyTickAsync</c> against the Testcontainers Postgres harness with a
/// <see cref="FixedTimeProvider"/> and a <see cref="FakeEmailService"/>, so the local-08:00
/// window, the (ReminderId, DocumentId, SendDate) dedupe key, and recipient selection can be
/// asserted deterministically.
/// </summary>
public sealed class ReminderBackgroundServiceTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // ----- Reference instants ----------------------------------------------------------------
    // Anchored to a winter date so DST is unambiguous for both zones.
    // EST is UTC-5 → 08:00 New_York on Jan 15, 2026 = 13:00 UTC.
    private static readonly DateTimeOffset NyEightAm = new(2026, 1, 15, 13, 0, 0, TimeSpan.Zero);
    // JST is UTC+9, no DST → 08:00 Tokyo on Jan 15, 2026 = 23:00 UTC on Jan 14.
    private static readonly DateTimeOffset TokyoEightAm = new(2026, 1, 14, 23, 0, 0, TimeSpan.Zero);

    private const string NyTz = "America/New_York";
    private const string TokyoTz = "Asia/Tokyo";

    // ----- Helpers ---------------------------------------------------------------------------

    private FakeEmailService Email =>
        (FakeEmailService)Fixture.Factory.Services.GetRequiredService<IEmailService>();

    /// <summary>Builds a worker bound to the host's DI but with a fixed clock.</summary>
    private ReminderBackgroundService BuildWorker(DateTimeOffset nowUtc) =>
        new(
            Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(nowUtc),
            NullLogger<ReminderBackgroundService>.Instance);

    /// <summary>
    /// Mirrors the prod-code calculation of the document expiration window so seed values land
    /// inside it regardless of the test host's local timezone. The worker treats the org's local
    /// date + DaysBefore as a UTC-rendered window via <c>DateOnly.ToDateTime(...).ToUniversalTime()</c>,
    /// which interprets <c>Unspecified</c> kinds as server-local; we reproduce that here.
    /// </summary>
    private static DateTime ExpirationForOrgWindow(string tz, DateTimeOffset nowUtc, int daysBefore)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            nowUtc.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById(tz));
        var targetDate = DateOnly.FromDateTime(local).AddDays(daysBefore);
        return targetDate.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
    }

    private sealed record SeedResult(Guid OrgId, Guid ReminderId, Guid DocumentId, Guid? VendorId, Guid? UserId);

    private async Task<SeedResult> SeedReminderAsync(
        DateTimeOffset nowUtc,
        string timeZone = NyTz,
        int daysBefore = 30,
        bool notifyInternal = true,
        bool notifyVendor = false,
        bool reminderIsActive = true,
        string internalEmail = "owner@example.com",
        string? vendorEmail = "vendor@example.com",
        DateTime? overrideExpirationUtc = null)
    {
        var orgId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var userId = notifyInternal ? Guid.NewGuid() : (Guid?)null;
        var now = nowUtc.UtcDateTime;
        var expiration = overrideExpirationUtc ?? ExpirationForOrgWindow(timeZone, nowUtc, daysBefore);

        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = $"Org-{orgId:N}",
            TimeZone = timeZone,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId,
            OrganizationId = orgId,
            Name = $"Vendor-{vendorId:N}",
            ContactEmail = vendorEmail,
            CreatedAt = now,
            UpdatedAt = now,
        });
        if (userId is { } uid)
        {
            db.Users.Add(new User
            {
                Id = uid,
                OrganizationId = orgId,
                Email = internalEmail,
                PasswordHash = "x",
                FullName = "Owner",
                Role = "admin",
                CreatedAt = now,
            });
        }
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = "policy.pdf",
            BlobStorageUrl = "blob://policy",
            FileSizeBytes = 1024,
            ContentType = "application/pdf",
            ExpirationDate = expiration,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Reminders.Add(new Reminder
        {
            Id = reminderId,
            OrganizationId = orgId,
            DaysBefore = daysBefore,
            NotifyInternalUser = notifyInternal,
            NotifyVendor = notifyVendor,
            IsActive = reminderIsActive,
        });
        await db.SaveChangesAsync();
        return new SeedResult(orgId, reminderId, docId, vendorId, userId);
    }

    private async Task<int> LogCountAsync(Guid reminderId, Guid docId)
    {
        await using var db = CreateSystemDb();
        return await db.ReminderLogs.CountAsync(l => l.ReminderId == reminderId && l.DocumentId == docId);
    }

    // ----- AC1: local-08:00 window -----------------------------------------------------------

    [Fact]
    public async Task At_local_08_a_due_reminder_sends_to_the_internal_user()
    {
        var seed = await SeedReminderAsync(NyEightAm);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle()
            .Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Theory]
    [InlineData(12)] // 07:00 NY
    [InlineData(14)] // 09:00 NY
    [InlineData(0)]  // 19:00 NY (prior day)
    public async Task Outside_local_08_window_nothing_is_sent_and_no_log_row_is_written(int utcHour)
    {
        var when = new DateTimeOffset(2026, 1, 15, utcHour, 0, 0, TimeSpan.Zero);
        var seed = await SeedReminderAsync(when);

        await BuildWorker(when).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    // ----- AC2: dedupe (ReminderId, DocumentId, SendDate) ------------------------------------

    [Fact]
    public async Task Same_day_re_tick_does_not_send_again()
    {
        var seed = await SeedReminderAsync(NyEightAm);
        var worker = BuildWorker(NyEightAm);

        await worker.ProcessHourlyTickAsync(CancellationToken.None);
        await worker.ProcessHourlyTickAsync(CancellationToken.None); // second tick same instant

        Email.Sends.Should().ContainSingle();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Pre_existing_log_row_blocks_a_send_even_on_the_first_tick()
    {
        var seed = await SeedReminderAsync(NyEightAm);

        // Simulate a prior successful send recorded today by some earlier tick / instance.
        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "owner@example.com",
                SentAt = NyEightAm.UtcDateTime,
                SendDate = DateOnly.FromDateTime(NyEightAm.UtcDateTime),
                ResendMessageId = "resend_pre_existing",
                Status = "sent",
            });
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1); // unchanged
    }

    // ----- AC3: recipient selection ----------------------------------------------------------

    [Fact]
    public async Task Notify_internal_and_vendor_both_recipients_receive_one_email_each()
    {
        var seed = await SeedReminderAsync(NyEightAm, notifyInternal: true, notifyVendor: true);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Select(s => s.ToEmail).Should().BeEquivalentTo(["owner@example.com", "vendor@example.com"]);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(2);
    }

    [Fact]
    public async Task Notify_internal_only_does_not_email_the_vendor()
    {
        var seed = await SeedReminderAsync(NyEightAm, notifyInternal: true, notifyVendor: false);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Select(s => s.ToEmail).Should().Equal(["owner@example.com"]);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Notify_vendor_only_does_not_email_internal_users()
    {
        var seed = await SeedReminderAsync(NyEightAm, notifyInternal: false, notifyVendor: true);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Select(s => s.ToEmail).Should().Equal(["vendor@example.com"]);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Notify_vendor_but_vendor_has_no_contact_email_falls_through_to_internal_only()
    {
        var seed = await SeedReminderAsync(
            NyEightAm, notifyInternal: true, notifyVendor: true, vendorEmail: null);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Select(s => s.ToEmail).Should().Equal(["owner@example.com"]);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Notify_vendor_only_but_vendor_has_no_email_results_in_no_send()
    {
        var seed = await SeedReminderAsync(
            NyEightAm, notifyInternal: false, notifyVendor: true, vendorEmail: null);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    // ----- AC4: per-timezone send window -----------------------------------------------------

    [Fact]
    public async Task Two_orgs_in_different_timezones_each_fire_at_their_own_local_08()
    {
        var ny = await SeedReminderAsync(NyEightAm, timeZone: NyTz, internalEmail: "ny@example.com");
        // The Tokyo expiration window must be computed at the Tokyo tick instant, not NY's.
        var tokyo = await SeedReminderAsync(TokyoEightAm, timeZone: TokyoTz, internalEmail: "tokyo@example.com");

        // Tick at NY 08:00. Tokyo is at 22:00 local — outside its window.
        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        Email.Sends.Select(s => s.ToEmail).Should().Equal(["ny@example.com"]);

        // Tick at Tokyo 08:00 (same calendar UTC day or the prior one — irrelevant). NY is at
        // ~18:00 local — outside its window. Only Tokyo's reminder fires.
        await BuildWorker(TokyoEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        Email.Sends.Select(s => s.ToEmail).Should().BeEquivalentTo(["ny@example.com", "tokyo@example.com"]);

        (await LogCountAsync(ny.ReminderId, ny.DocumentId)).Should().Be(1);
        (await LogCountAsync(tokyo.ReminderId, tokyo.DocumentId)).Should().Be(1);
    }

    // ----- Other meaningful branches ---------------------------------------------------------

    [Fact]
    public async Task Inactive_reminder_does_not_fire()
    {
        var seed = await SeedReminderAsync(NyEightAm, reminderIsActive: false);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    [Fact]
    public async Task Document_outside_the_target_expiration_window_does_not_fire()
    {
        // Seed a doc that expires 60 days out, but the reminder fires at DaysBefore = 30.
        var farExpiration = ExpirationForOrgWindow(NyTz, NyEightAm, daysBefore: 60);
        var seed = await SeedReminderAsync(NyEightAm, daysBefore: 30, overrideExpirationUtc: farExpiration);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    [Fact]
    public async Task Soft_deleted_document_is_skipped()
    {
        var seed = await SeedReminderAsync(NyEightAm);

        await using (var db = CreateSystemDb())
        {
            var doc = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == seed.DocumentId);
            doc.DeletedAt = NyEightAm.UtcDateTime.AddHours(-1);
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    [Fact]
    public async Task Soft_deleted_organization_is_skipped()
    {
        var seed = await SeedReminderAsync(NyEightAm);

        await using (var db = CreateSystemDb())
        {
            var org = await db.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == seed.OrgId);
            org.DeletedAt = NyEightAm.UtcDateTime.AddHours(-1);
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    [Fact]
    public async Task Email_service_disabled_short_circuits_with_no_send_and_no_log()
    {
        var seed = await SeedReminderAsync(NyEightAm);
        Email.IsEnabled = false;
        try
        {
            await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

            Email.Sends.Should().BeEmpty();
            (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
        }
        finally
        {
            Email.IsEnabled = true; // restore for any subsequent test in the same fixture
        }
    }

    [Fact]
    public async Task Email_body_includes_document_org_and_days_before()
    {
        await SeedReminderAsync(NyEightAm, daysBefore: 30, notifyVendor: true, vendorEmail: "vendor@example.com");

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        var send = Email.Sends.Should().HaveCount(2).And.Subject.First();
        send.Subject.Should().Contain("policy.pdf").And.Contain("30 days");
        send.HtmlBody.Should().Contain("policy.pdf").And.Contain("30 days from today");
    }
}
