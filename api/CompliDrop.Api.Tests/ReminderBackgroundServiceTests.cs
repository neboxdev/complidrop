using System.Data;
using System.Data.Common;
using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Migrations;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for <see cref="ReminderBackgroundService"/>: drives
/// <c>ProcessHourlyTickAsync</c> against the Testcontainers Postgres harness with a
/// <see cref="FixedTimeProvider"/> and a <see cref="FakeEmailService"/>, so the local-08:00
/// window, the per-recipient dedupe key (ADR 0002), the org-local <c>SendDate</c> semantic
/// (ADR 0007), and the per-(orgId, sendDate) advisory lock coordinating multi-instance ticks
/// (ADR 0008) can be asserted deterministically.
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

    /// <summary>As <see cref="BuildWorker"/>, but with a capturing logger so a test can assert on
    /// emitted warnings (the #184 dead-letter flag).</summary>
    private ReminderBackgroundService BuildWorker(DateTimeOffset nowUtc, ListLogger<ReminderBackgroundService> logger) =>
        new(
            Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(nowUtc),
            logger);

    /// <summary>
    /// Returns the UTC instant at the *start* of the org-local target day so the worker's
    /// expiration window query matches. Mirrors the prod-code calculation (which uses
    /// <c>TimeZoneInfo.ConvertTimeToUtc</c> against the org's zone), so seeded docs land inside
    /// the window on any host's local timezone.
    /// </summary>
    private static DateTime ExpirationForOrgWindow(string tz, DateTimeOffset nowUtc, int daysBefore)
    {
        var info = TimeZoneInfo.FindSystemTimeZoneById(tz);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, info);
        var targetDate = DateOnly.FromDateTime(local).AddDays(daysBefore);
        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(targetDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified),
            info);
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
        bool internalEmailVerified = false,
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
                EmailVerifiedAt = internalEmailVerified ? now : null,
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

    // ----- #184: unverified internal recipient dead-letter flag ------------------------------

    [Fact]
    public async Task Unverified_internal_recipient_still_receives_the_reminder_but_is_flagged()
    {
        // Soft-gate: an unverified (possibly typo'd) internal email must NOT block the org's
        // reminders — the send still happens — but the worker logs a warning so a forming
        // dead-letter is visible instead of silent (#184).
        var seed = await SeedReminderAsync(NyEightAm, internalEmail: "owner@example.com", internalEmailVerified: false);
        var logger = new ListLogger<ReminderBackgroundService>();

        await BuildWorker(NyEightAm, logger).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("unverified")
            && e.Message.Contains("owner@example.com"));
    }

    [Fact]
    public async Task Verified_internal_recipient_is_not_flagged()
    {
        // The negative: a verified internal email sends with no dead-letter warning, so the flag
        // can't become noise that operators learn to ignore.
        await SeedReminderAsync(NyEightAm, internalEmail: "owner@example.com", internalEmailVerified: true);
        var logger = new ListLogger<ReminderBackgroundService>();

        await BuildWorker(NyEightAm, logger).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle();
        logger.Entries.Should().NotContain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("unverified"));
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
        // SendDate is the org-local calendar day at SentAt (per ADR 0007). For NyEightAm
        // (13:00 UTC Jan 15 = 08:00 NY-local Jan 15) the value is Jan 15 in either convention,
        // but writing it as the local date keeps this seed consistent with the live semantic.
        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "owner@example.com",
                SentAt = NyEightAm.UtcDateTime,
                SendDate = new DateOnly(2026, 1, 15), // NY-local day
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

        // Both orgs' SendDate is org-local Jan 15 per ADR 0007 — even though the two ticks fired
        // at different UTC instants on different UTC calendar days (Tokyo at 23:00 UTC Jan 14,
        // NY at 13:00 UTC Jan 15). Asserts the symmetric write site: a regression that fixed only
        // one zone (or reverted only one to `nowUtc`) would fail here as well as in the dedicated
        // SendDate tests, so the failure mode is unambiguous.
        await using var assertDb = CreateSystemDb();
        var nyLog = await assertDb.ReminderLogs.SingleAsync(l => l.ReminderId == ny.ReminderId);
        var tokyoLog = await assertDb.ReminderLogs.SingleAsync(l => l.ReminderId == tokyo.ReminderId);
        nyLog.SendDate.Should().Be(new DateOnly(2026, 1, 15));
        tokyoLog.SendDate.Should().Be(new DateOnly(2026, 1, 15));
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
        // No finally restore — FakeEmailService.Reset() (run between tests by
        // IntegrationTestFixture.ResetAsync) puts IsEnabled back to true automatically.

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    [Fact]
    public async Task Email_subject_and_body_include_filename_org_name_and_days_before()
    {
        var seed = await SeedReminderAsync(NyEightAm, daysBefore: 30, notifyVendor: true, vendorEmail: "vendor@example.com");
        var orgName = $"Org-{seed.OrgId:N}";

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        // Assert across every send rather than picking Sends[0], so the test doesn't silently
        // re-target itself if the worker's recipient loop order ever changes. The org name
        // assertion ties the body to the specific org the reminder fired for.
        Email.Sends.Should().HaveCount(2);
        foreach (var send in Email.Sends)
        {
            send.Subject.Should().Contain("policy.pdf").And.Contain("30 days");
            send.HtmlBody.Should().Contain("policy.pdf").And.Contain("30 days from today").And.Contain(orgName);
        }
    }

    // ----- Regression tests from review --------------------------------------------------------

    [Fact]
    public async Task Multi_recipient_send_does_not_re_send_on_second_tick_in_same_day()
    {
        // The marquee invariant this ticket's schema widening enables: both recipients are
        // notified on tick 1, both rows persist, and tick 2 finds both in the dedupe set.
        var seed = await SeedReminderAsync(NyEightAm, notifyInternal: true, notifyVendor: true);
        var worker = BuildWorker(NyEightAm);

        await worker.ProcessHourlyTickAsync(CancellationToken.None);
        await worker.ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().HaveCount(2);
        Email.Sends.Select(s => s.ToEmail).Should().BeEquivalentTo(["owner@example.com", "vendor@example.com"]);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(2);
    }

    [Fact]
    public async Task Pre_existing_log_for_one_recipient_does_not_block_the_other()
    {
        // Per-recipient dedupe: a prior log for the internal user must not suppress the vendor.
        var seed = await SeedReminderAsync(NyEightAm, notifyInternal: true, notifyVendor: true);
        const string preExistingId = "resend_pre_owner";

        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "owner@example.com",
                SentAt = NyEightAm.UtcDateTime,
                SendDate = new DateOnly(2026, 1, 15), // NY-local day per ADR 0007
                ResendMessageId = preExistingId,
                Status = "sent",
            });
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Select(s => s.ToEmail).Should().Equal(["vendor@example.com"]);

        // Verify the newly-written row directly rather than just the count, so a refactor that
        // (say) overwrote the pre-existing row instead of inserting a new one would fail loudly.
        await using var db2 = CreateSystemDb();
        var newLog = await db2.ReminderLogs
            .Where(l => l.ReminderId == seed.ReminderId
                        && l.DocumentId == seed.DocumentId
                        && l.ResendMessageId != preExistingId)
            .SingleAsync();
        newLog.RecipientEmail.Should().Be("vendor@example.com");
        newLog.Status.Should().Be("sent");
        newLog.ResendMessageId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Case_variant_duplicate_recipients_collapse_to_a_single_send_keeping_internal()
    {
        // Internal user emails are stored lowercased; vendor ContactEmail is stored as-typed.
        // A vendor "Owner@example.com" must not produce a second mail to the same human, and
        // the kept variant is the internal one (added to the recipient list first).
        var seed = await SeedReminderAsync(
            NyEightAm,
            notifyInternal: true,
            notifyVendor: true,
            internalEmail: "owner@example.com",
            vendorEmail: "OWNER@example.com");

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    // ----- SendDate = org-local calendar day (#24) -------------------------------------------
    // ReminderLog.SendDate stores the org's local calendar day, not the UTC date at send time.
    // The regression is real only for orgs whose 08:00-local crosses a UTC midnight (Tokyo: 23:00
    // UTC the prior day); NY is included for symmetry so a future "let's just use nowUtc" refactor
    // breaks both tests at once instead of looking like a Tokyo-only quirk. ADR 0002 documents the
    // decision; SentAt (the timestamptz instant) is unchanged.

    [Fact]
    public async Task Tokyo_org_records_send_date_as_local_calendar_day_not_utc()
    {
        // 08:00 Tokyo Jan 15 = 23:00 UTC Jan 14. The old behavior stored Jan 14 (UTC date at the
        // moment of send); the new behavior stores Jan 15 (the org's local calendar day, which is
        // what every "reminders sent on Jan 15" query naturally means for a Tokyo org).
        var seed = await SeedReminderAsync(TokyoEightAm, timeZone: TokyoTz, internalEmail: "tokyo@example.com");

        await BuildWorker(TokyoEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        await using var db = CreateSystemDb();
        var log = await db.ReminderLogs.SingleAsync(l =>
            l.ReminderId == seed.ReminderId && l.DocumentId == seed.DocumentId);
        log.SendDate.Should().Be(new DateOnly(2026, 1, 15));
        // SentAt remains the precise UTC instant — DateOnly column flipping doesn't change this.
        log.SentAt.Should().Be(TokyoEightAm.UtcDateTime);
    }

    [Fact]
    public async Task NewYork_org_records_send_date_as_local_calendar_day()
    {
        // 08:00 NY Jan 15 = 13:00 UTC Jan 15 — same calendar day in both zones, so the value is
        // identical before and after #24. The test exists so the Tokyo assertion above doesn't
        // stand alone: a regression that reverted both write sites to nowUtc would silently pass
        // for NY but fail for Tokyo, and we want the failure to read as "SendDate semantic", not
        // "Tokyo-specific bug".
        var seed = await SeedReminderAsync(NyEightAm, timeZone: NyTz, internalEmail: "ny@example.com");

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        await using var db = CreateSystemDb();
        var log = await db.ReminderLogs.SingleAsync(l =>
            l.ReminderId == seed.ReminderId && l.DocumentId == seed.DocumentId);
        log.SendDate.Should().Be(new DateOnly(2026, 1, 15));
        log.SentAt.Should().Be(NyEightAm.UtcDateTime);
    }

    [Fact]
    public async Task Backfill_sql_rewrites_legacy_utc_send_date_to_org_local_for_tokyo_row()
    {
        // The runtime tests cover the new write path. This test covers the one-shot backfill SQL
        // shipped in migration `BackfillReminderLogSendDateToOrgLocal` — seed a row in the
        // pre-#24 shape (SendDate = UTC date), execute the same SQL the migration runs, and
        // assert the row was shifted to the org-local day. WHERE guard means it's idempotent;
        // running it a second time is a no-op.
        var seed = await SeedReminderAsync(TokyoEightAm, timeZone: TokyoTz, internalEmail: "tokyo@example.com");
        var nyOnSameTokyoDay = await SeedReminderAsync(NyEightAm, timeZone: NyTz, internalEmail: "ny@example.com");

        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "tokyo@example.com",
                SentAt = TokyoEightAm.UtcDateTime,        // 23:00 UTC Jan 14
                SendDate = new DateOnly(2026, 1, 14),     // legacy: UTC date
                ResendMessageId = "resend_legacy_tokyo",
                Status = "sent",
            });
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = nyOnSameTokyoDay.ReminderId,
                DocumentId = nyOnSameTokyoDay.DocumentId,
                RecipientEmail = "ny@example.com",
                SentAt = NyEightAm.UtcDateTime,           // 13:00 UTC Jan 15
                SendDate = new DateOnly(2026, 1, 15),     // already matches local — backfill must skip
                ResendMessageId = "resend_legacy_ny",
                Status = "sent",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateSystemDb())
        {
            // Sourced from the migration class's `const` so test and prod execute the same
            // statement byte-for-byte. A typo or guard removed from `UpSql` would surface here
            // automatically — no copy-paste drift.
            await db.Database.ExecuteSqlRawAsync(BackfillReminderLogSendDateToOrgLocal.UpSql);
        }

        await using (var db = CreateSystemDb())
        {
            var tokyoLog = await db.ReminderLogs.SingleAsync(l => l.ReminderId == seed.ReminderId);
            tokyoLog.SendDate.Should().Be(new DateOnly(2026, 1, 15)); // shifted from Jan 14 → Jan 15
            var nyLog = await db.ReminderLogs.SingleAsync(l => l.ReminderId == nyOnSameTokyoDay.ReminderId);
            nyLog.SendDate.Should().Be(new DateOnly(2026, 1, 15));   // unchanged (already matched)
        }
    }

    [Fact]
    public async Task Backfill_sql_skips_rows_whose_org_has_a_time_zone_postgres_cannot_resolve()
    {
        // The migration's `pg_timezone_names` guard means a single bad TimeZone value can't
        // abort the entire deploy. Today no writer produces such a value (NormalizeTimeZone in
        // AuthEndpoints validates), but the column has no DB-level CHECK constraint so a future
        // admin tool / seed script could. This test inserts a row directly (bypassing the worker
        // and SeedReminderAsync's CLR TimeZoneInfo lookup) and asserts the row's SendDate is
        // unchanged after the backfill — silently skipped, not failed.
        var orgId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        var now = TokyoEightAm.UtcDateTime;
        var legacySendDate = new DateOnly(2026, 1, 14);

        await using (var db = CreateSystemDb())
        {
            db.Organizations.Add(new Organization
            {
                Id = orgId,
                Name = $"Org-{orgId:N}",
                TimeZone = "Mars/Olympus", // not in pg_timezone_names
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "V", CreatedAt = now, UpdatedAt = now });
            db.Documents.Add(new Document { Id = docId, OrganizationId = orgId, VendorId = vendorId, OriginalFileName = "p.pdf", BlobStorageUrl = "b", FileSizeBytes = 1, ContentType = "application/pdf", CreatedAt = now, UpdatedAt = now });
            db.Reminders.Add(new Reminder { Id = reminderId, OrganizationId = orgId, DaysBefore = 30, NotifyInternalUser = true, IsActive = true });
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = logId,
                ReminderId = reminderId,
                DocumentId = docId,
                RecipientEmail = "mars@example.com",
                SentAt = now,
                SendDate = legacySendDate, // would be Jan 15 if backfill ran for this row
                ResendMessageId = "resend_mars",
                Status = "sent",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateSystemDb())
        {
            // Must NOT throw despite the unrecognised TZ — the guard skips the row.
            await db.Database.ExecuteSqlRawAsync(BackfillReminderLogSendDateToOrgLocal.UpSql);
        }

        await using (var db = CreateSystemDb())
        {
            var log = await db.ReminderLogs.SingleAsync(l => l.Id == logId);
            log.SendDate.Should().Be(legacySendDate); // unchanged — skipped by the guard
        }
    }

    [Fact]
    public async Task Tokyo_pre_existing_log_with_local_send_date_blocks_resend()
    {
        // Dedupe check uses the new local-day SendDate on both sides (write and lookup). A
        // pre-existing row written with the new semantic (SendDate = Jan 15 Tokyo-local) must
        // block today's tick at 23:00 UTC Jan 14. This is the dedupe invariant for non-US zones
        // after the value change.
        var seed = await SeedReminderAsync(TokyoEightAm, timeZone: TokyoTz, internalEmail: "tokyo@example.com");

        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "tokyo@example.com",
                SentAt = TokyoEightAm.UtcDateTime,
                SendDate = new DateOnly(2026, 1, 15), // Tokyo-local day, not UTC (Jan 14).
                ResendMessageId = "resend_pre_existing_tokyo",
                Status = "sent",
            });
            await db.SaveChangesAsync();
        }

        await BuildWorker(TokyoEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1); // unchanged
    }

    // ----- Additional coverage from review (DST, multi-reminder, retry, subject template) ------

    [Fact]
    public async Task DST_spring_forward_day_still_fires_at_local_08()
    {
        // March 8, 2026 is the US DST spring-forward: at 02:00 EST clocks jump to 03:00 EDT.
        // 08:00 New_York that morning = 12:00 UTC (NY is UTC-4 EDT after the jump).
        var when = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero);
        var seed = await SeedReminderAsync(when);

        await BuildWorker(when).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task DST_fall_back_day_still_fires_at_local_08()
    {
        // November 1, 2026: at 02:00 EDT clocks fall back to 01:00 EST. The 01:00–02:00 wall hour
        // happens twice (ambiguous), but 08:00 happens exactly once at 13:00 UTC (NY now UTC-5).
        var when = new DateTimeOffset(2026, 11, 1, 13, 0, 0, TimeSpan.Zero);
        var seed = await SeedReminderAsync(when);

        await BuildWorker(when).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Two_reminders_with_different_days_before_each_fire_for_their_own_doc()
    {
        // Org has two reminders: 30-day and 7-day. Two docs, one expiring in 30 NY-local days,
        // the other in 7. Both reminders must fire in the same tick, each matching its own doc.
        var orgId = Guid.NewGuid();
        var reminder30Id = Guid.NewGuid();
        var reminder7Id = Guid.NewGuid();
        var doc30Id = Guid.NewGuid();
        var doc7Id = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = NyEightAm.UtcDateTime;

        await using (var db = CreateSystemDb())
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", TimeZone = NyTz, CreatedAt = now, UpdatedAt = now });
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "V", CreatedAt = now, UpdatedAt = now });
            db.Users.Add(new User { Id = userId, OrganizationId = orgId, Email = "owner@example.com", PasswordHash = "x", FullName = "Owner", Role = "admin", CreatedAt = now });
            db.Documents.AddRange(
                new Document { Id = doc30Id, OrganizationId = orgId, VendorId = vendorId, OriginalFileName = "30day.pdf", BlobStorageUrl = "b", FileSizeBytes = 1, ContentType = "application/pdf", ExpirationDate = ExpirationForOrgWindow(NyTz, NyEightAm, 30), CreatedAt = now, UpdatedAt = now },
                new Document { Id = doc7Id, OrganizationId = orgId, VendorId = vendorId, OriginalFileName = "7day.pdf", BlobStorageUrl = "b", FileSizeBytes = 1, ContentType = "application/pdf", ExpirationDate = ExpirationForOrgWindow(NyTz, NyEightAm, 7), CreatedAt = now, UpdatedAt = now });
            db.Reminders.AddRange(
                new Reminder { Id = reminder30Id, OrganizationId = orgId, DaysBefore = 30, NotifyInternalUser = true, IsActive = true },
                new Reminder { Id = reminder7Id, OrganizationId = orgId, DaysBefore = 7, NotifyInternalUser = true, IsActive = true });
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().HaveCount(2);
        (await LogCountAsync(reminder30Id, doc30Id)).Should().Be(1);
        (await LogCountAsync(reminder7Id, doc7Id)).Should().Be(1);
        // No cross-match: reminder30 should not have fired against doc7 (or vice versa).
        (await LogCountAsync(reminder30Id, doc7Id)).Should().Be(0);
        (await LogCountAsync(reminder7Id, doc30Id)).Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Notify_vendor_with_whitespace_only_email_falls_through_to_internal(string vendorEmail)
    {
        // Pairs with Notify_vendor_but_vendor_has_no_contact_email... (covers null) to exercise
        // every branch of string.IsNullOrWhiteSpace.
        var seed = await SeedReminderAsync(
            NyEightAm, notifyInternal: true, notifyVendor: true, vendorEmail: vendorEmail);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Select(s => s.ToEmail).Should().Equal(["owner@example.com"]);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task EmailSubjectTemplate_overrides_the_default_subject_line()
    {
        const string customSubject = "Please renew your policy ASAP";
        var seed = await SeedReminderAsync(NyEightAm);

        await using (var db = CreateSystemDb())
        {
            var reminder = await db.Reminders.SingleAsync(r => r.Id == seed.ReminderId);
            reminder.EmailSubjectTemplate = customSubject;
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.Subject.Should().Be(customSubject);
    }

    [Fact]
    public async Task Failed_send_writes_a_failed_log_row_and_blocks_intraday_retry()
    {
        // Codifies the current behavior: a Resend non-2xx (messageId == null) persists a log row
        // with Status='failed' that subsequent ticks the same day treat as already-sent. Intraday
        // retry is intentionally NOT attempted today; if/when we add retries, this test will need
        // to flip its assertions and is the canonical place to do so.
        var seed = await SeedReminderAsync(NyEightAm);
        Email.NextSendReturnsNull = true;

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        // First tick: send was attempted (queue has the entry with null id) and a failed row exists.
        Email.Sends.Should().ContainSingle().Which.MessageId.Should().BeNull();
        await using (var db = CreateSystemDb())
        {
            var log = await db.ReminderLogs.SingleAsync(l =>
                l.ReminderId == seed.ReminderId && l.DocumentId == seed.DocumentId);
            log.Status.Should().Be("failed");
            log.ResendMessageId.Should().BeNull();
        }

        // Second tick same day: dedupe set contains the failed row → no retry.
        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        Email.Sends.Should().ContainSingle(); // unchanged
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    // ----- AC: Multi-instance coordination via advisory lock (#25 / ADR 0008) ----------------
    // Two API replicas firing their hourly tick at the same UTC hour must not both call
    // email.SendAsync for the same (reminder, doc, recipient). The advisory lock keyed on
    // (orgId, sendDate) is the primary mechanism; the per-recipient unique index from ADR 0002
    // is defence-in-depth.

    /// <summary>
    /// Acquires the session-scoped Postgres advisory lock keyed by <paramref name="lockKey"/> on
    /// a separate Npgsql connection and returns a handle whose <c>DisposeAsync</c> calls
    /// <c>pg_advisory_unlock</c> explicitly before disposing the connection. Relying on Npgsql's
    /// <c>DISCARD ALL</c>-on-pool-return is not enough here: the reset isn't synchronously visible
    /// to the next command on a different pooled connection, so a Release test that fires the
    /// next tick immediately after the handle is disposed would race the lock's actual release.
    /// Uses <c>pg_try_advisory_lock</c> so a stale lock from a prior test fails loudly here
    /// instead of blocking for the connection-timeout window.
    /// <para/>
    /// Both the acquire and release commands set <c>DbType.String</c> explicitly to match the
    /// production parameter shape in <c>ReminderBackgroundService.AddTextParam</c>. The default
    /// <c>AddWithValue</c> inference is <c>text</c> in Npgsql today and works the same — but
    /// pinning the type guarantees the hash and the prod hash agree even if a future
    /// Npgsql/PG version changes inference for CLR <c>string</c>.
    /// </summary>
    private async Task<IAsyncDisposable> HoldAdvisoryLockAsync(string lockKey)
    {
        var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))";
            AddTextParam(cmd, "@key", lockKey);
            var acquired = (bool?)await cmd.ExecuteScalarAsync();
            acquired.Should().BeTrue("no other session should hold the lock at test arrangement time");
            return new AdvisoryLockHandle(conn, lockKey);
        }
        catch
        {
            // Don't leak the open connection (and the lock it would otherwise hold) if the
            // setup assertion or the SELECT itself throws before the caller receives the handle.
            await conn.DisposeAsync();
            throw;
        }
    }

    private static void AddTextParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = DbType.String;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private sealed class AdvisoryLockHandle(NpgsqlConnection conn, string lockKey) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@key, 0))";
                AddTextParam(cmd, "@key", lockKey);
                await cmd.ExecuteScalarAsync();
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Concurrent_ticks_against_the_same_org_send_each_recipient_exactly_one_email()
    {
        // Marquee invariant from #25 / ADR 0008. Two replicas firing the same UTC hour both
        // produce exactly two emails (internal + vendor) — not four. Pre-fix, both ticks would
        // pre-load an empty alreadySent set, both would call email.SendAsync for each recipient,
        // and only the DB unique index would protect log integrity (the emails would already be
        // at Resend twice). The advisory lock prevents the SendAsync from ever firing twice.
        var seed = await SeedReminderAsync(NyEightAm, notifyInternal: true, notifyVendor: true);
        var workerA = BuildWorker(NyEightAm);
        var workerB = BuildWorker(NyEightAm);

        // Task.WhenAll dispatches both immediately. Two outcomes are valid and both must satisfy
        // the assertion: (a) one wins the lock + sends, the other skips on pg_try → false;
        // (b) one fully completes before the other reaches the critical section, the second's
        // alreadySent set then contains both recipients and it sends nothing. Either way the
        // total send count is 2, the total log count is 2 — the assertions below pin the floor
        // and the ceiling.
        //
        // CAVEAT: this test alone CANNOT detect a regression that silently removes the advisory
        // lock — under cooperative scheduling path (b) it would pass on the dedupe HashSet
        // alone. `Held_advisory_lock_on_a_side_connection_causes_the_tick_to_skip_the_org`
        // below is the canonical lock-presence pin; this test pins the externally observable
        // invariant (two sends total, two log rows, no matter the interleaving).
        await Task.WhenAll(
            workerA.ProcessHourlyTickAsync(CancellationToken.None),
            workerB.ProcessHourlyTickAsync(CancellationToken.None));

        Email.Sends.Should().HaveCount(2);
        Email.Sends.Select(s => s.ToEmail).Should()
            .BeEquivalentTo(["owner@example.com", "vendor@example.com"]);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(2);
    }

    [Fact]
    public async Task Held_advisory_lock_on_a_side_connection_causes_the_tick_to_skip_the_org()
    {
        // Pin the lock-acquisition path explicitly. A regression that silently removed the lock
        // and relied on dedupe alone would still pass the concurrent test above under scheduler
        // serialisation but would fail this one — the worker would enter the critical section,
        // find no log rows, and send.
        var seed = await SeedReminderAsync(NyEightAm);
        var lockKey = ReminderBackgroundService.BuildOrgDayLockKey(seed.OrgId, new DateOnly(2026, 1, 15));

        await using var holder = await HoldAdvisoryLockAsync(lockKey);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty("the locked org must be skipped, not waited on");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    [Fact]
    public async Task Releasing_the_advisory_lock_lets_a_subsequent_tick_process_the_org()
    {
        // The acquire/release round-trip: a lock held during one tick must be releasable before
        // the next tick, with no residual state blocking the second run. Without this, a one-off
        // race that took the lock would permanently wedge the org for the rest of the local day.
        var seed = await SeedReminderAsync(NyEightAm);
        var lockKey = ReminderBackgroundService.BuildOrgDayLockKey(seed.OrgId, new DateOnly(2026, 1, 15));

        await using (var holder = await HoldAdvisoryLockAsync(lockKey))
        {
            await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        }

        Email.Sends.Should().BeEmpty("first tick: lock held → org skipped");

        // Lock released by the using-block dispose. Next tick must process normally.
        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Different_orgs_can_be_processed_in_the_same_tick_when_one_orgs_lock_is_held()
    {
        // Lock granularity is per-(org, sendDate), not global. An external holder of org A's
        // lock must not block the tick from processing org B in the same run — otherwise scaling
        // out the workers would serialise reminder processing globally and defeat the point.
        var orgA = await SeedReminderAsync(NyEightAm, internalEmail: "a@example.com");
        var orgB = await SeedReminderAsync(NyEightAm, internalEmail: "b@example.com");
        var lockKeyA = ReminderBackgroundService.BuildOrgDayLockKey(orgA.OrgId, new DateOnly(2026, 1, 15));

        await using var holder = await HoldAdvisoryLockAsync(lockKeyA);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Select(s => s.ToEmail).Should().Equal(["b@example.com"]);
        (await LogCountAsync(orgA.ReminderId, orgA.DocumentId)).Should().Be(0, "org A is locked, must be skipped");
        (await LogCountAsync(orgB.ReminderId, orgB.DocumentId)).Should().Be(1, "org B is unlocked, must process");
    }

    [Fact]
    public async Task Cancellation_during_tick_releases_the_advisory_lock_for_next_tick()
    {
        // Defensive pin on the lock-release-on-cancellation contract. The per-org `finally`
        // (release lock) and the outer pinned-connection `finally` (close connection — pool
        // DISCARD ALL is the ultimate safety net) both run on cancellation, and neither
        // ReleaseOrgLockAsync nor CloseConnectionAsync take the tick's CT. A regression that
        // wrapped the unlock in `if (!ct.IsCancellationRequested) ...` would leak the lock
        // for the rest of the local day; this test catches that by running a fresh tick and
        // asserting the org was actually processed (a leak would have skipped it).
        var seed = await SeedReminderAsync(NyEightAm);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await BuildWorker(NyEightAm).ProcessHourlyTickAsync(cts.Token);
        }
        catch (OperationCanceledException) { /* expected — cancellation propagates out */ }

        // If the cancelled tick had leaked the (orgId, sendDate) lock, this tick would skip
        // the org (logged at debug as "advisory lock held by another instance") and write no
        // ReminderLog row at all. The assertion below would then fail.
        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().BeGreaterOrEqualTo(1,
            "the fresh tick must have acquired the lock — a leaked lock would have blocked the send");
    }

    [Fact]
    public void BuildOrgDayLockKey_is_stable_across_calls_and_distinct_across_inputs()
    {
        // Pins the lock key's identity at the function level so the integration tests above
        // can rely on ReminderBackgroundService.BuildOrgDayLockKey returning the same string they
        // pass to pg_try_advisory_lock manually. A refactor that accidentally collapsed the
        // granularity (per-org only, per-day only, or constant global) would fail here loudly
        // — without this, the failure mode would surface only as "the concurrent test
        // intermittently fails because the manual lock no longer matches the worker's".
        var orgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var date = new DateOnly(2026, 1, 15);

        ReminderBackgroundService.BuildOrgDayLockKey(orgId, date)
            .Should().Be(ReminderBackgroundService.BuildOrgDayLockKey(orgId, date));

        var otherOrg = Guid.Parse("22222222-2222-2222-2222-222222222222");
        ReminderBackgroundService.BuildOrgDayLockKey(otherOrg, date)
            .Should().NotBe(ReminderBackgroundService.BuildOrgDayLockKey(orgId, date));
        ReminderBackgroundService.BuildOrgDayLockKey(orgId, date.AddDays(1))
            .Should().NotBe(ReminderBackgroundService.BuildOrgDayLockKey(orgId, date));
    }
}
