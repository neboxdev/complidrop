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
/// <see cref="FixedTimeProvider"/> and a <see cref="FakeEmailService"/>, so the local 08:00+
/// catch-up window and failed-send retry (ADR 0025), the per-recipient dedupe key (ADR 0002),
/// the org-local <c>SendDate</c> semantic (ADR 0007), and the per-(orgId, sendDate) advisory
/// lock coordinating multi-instance ticks (ADR 0008) can be asserted deterministically.
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
    /// Returns the stored shape of a document face date that lands in the worker's target window
    /// for the given tick: UTC midnight of (org-local date at the tick) + <paramref name="daysBefore"/>.
    /// Mirrors how the pipeline persists expirations (<c>CanonicalDocumentFields.ParseUtcDate</c>:
    /// face date at UTC midnight) and the worker's UTC-calendar-day bracket (#270), so seeded
    /// docs land inside the window on any host's local timezone.
    /// </summary>
    private static DateTime ExpirationForOrgWindow(string tz, DateTimeOffset nowUtc, int daysBefore)
    {
        var info = TimeZoneInfo.FindSystemTimeZoneById(tz);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, info);
        var targetDate = DateOnly.FromDateTime(local).AddDays(daysBefore);
        return DateTime.SpecifyKind(targetDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
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
        string? orgName = null,
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
            Name = orgName ?? $"Org-{orgId:N}",
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
    [InlineData(12)] // 07:00 NY — one hour before the window opens
    [InlineData(11)] // 06:00 NY
    [InlineData(5)]  // 00:00 NY — first hour of the local day
    public async Task Before_local_08_nothing_is_sent(int utcHour)
    {
        // The catch-up window (ADR 0025) opens at local 08:00 and stays open through the local
        // day — but it must still OPEN at 08:00, never earlier. Pre-08:00 ticks send nothing.
        var when = new DateTimeOffset(2026, 1, 15, utcHour, 0, 0, TimeSpan.Zero);
        var seed = await SeedReminderAsync(when);

        await BuildWorker(when).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
    }

    // ----- ADR 0025 half A: catch-up window ---------------------------------------------------

    [Fact]
    public async Task Missed_08_window_is_caught_up_by_a_later_tick_the_same_local_day()
    {
        // The marquee half-A regression (#270): no tick fired during the org's 08:00 hour
        // (deploy / crash / stuck instance). Pre-#270 the day's reminders were silently dropped
        // forever; now any later tick the same local day sends them.
        var seed = await SeedReminderAsync(NyEightAm);
        var elevenAmNy = NyEightAm.AddHours(3); // 16:00 UTC = 11:00 NY — the 08:00 tick never ran

        await BuildWorker(elevenAmNy).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Catch_up_tick_does_not_resend_what_08_already_sent()
    {
        // Idempotency of the widened window: the extra qualifying ticks re-send nothing — the
        // per-recipient dedupe (ADR 0002) is what makes ADR 0025's `>=` safe.
        var seed = await SeedReminderAsync(NyEightAm);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        await BuildWorker(NyEightAm.AddHours(3)).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
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
    public async Task Two_orgs_in_different_timezones_each_fire_on_their_own_local_day()
    {
        var ny = await SeedReminderAsync(NyEightAm, timeZone: NyTz, internalEmail: "ny@example.com");
        // The Tokyo expiration window must be computed at the Tokyo tick instant, not NY's.
        var tokyo = await SeedReminderAsync(TokyoEightAm, timeZone: TokyoTz, internalEmail: "tokyo@example.com");

        // Tick at Tokyo 08:00 Jan 15 (23:00 UTC Jan 14). NY is at 18:00 local Jan 14 — inside the
        // ADR 0025 catch-up window for its local Jan 14, but its doc targets the NY-local Jan 15
        // day, so nothing matches and only Tokyo's reminder fires.
        await BuildWorker(TokyoEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        Email.Sends.Select(s => s.ToEmail).Should().Equal(["tokyo@example.com"]);

        // Tick at NY 08:00 Jan 15 (13:00 UTC). Tokyo is at 22:00 local Jan 15 — still inside its
        // catch-up window for Jan 15, but its send is already logged for that local day, so the
        // dedupe holds and only NY's reminder fires.
        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);
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

    [Fact]
    public async Task Org_name_is_html_encoded_in_the_reminder_body_no_injection()
    {
        // #185 makes the org name user-editable; it is interpolated into the
        // reminder HTML body, which is delivered to vendors (outside the org's
        // trust boundary). A malicious name must be HTML-encoded, not rendered
        // as live markup.
        await SeedReminderAsync(NyEightAm, orgName: "<script>alert('xss')</script>");

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle();
        var body = Email.Sends[0].HtmlBody;
        body.Should().NotContain("<script>alert('xss')</script>");
        body.Should().Contain("&lt;script&gt;");
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

    // ----- #205: editable-time-zone double-send guard ----------------------------------------
    // #185 made the org time zone user-editable. Editing it around local 08:00 can re-open the
    // send window on a *different* org-local calendar day within ~24h, computing a different
    // SendDate that the (…, SendDate, …) unique index and the same-day dedupe set would miss.
    // The trailing-window guard (TzEditDedupeWindow, ADR 0015) keys on the prior send's UTC
    // SentAt — which a zone edit cannot move — to suppress the re-fire.

    [Fact]
    public async Task Editing_time_zone_around_local_08_does_not_double_send_the_same_reminder()
    {
        // The marquee #205 regression, kept as the end-to-end "zone edit cannot double-send" pin.
        // When it was written the doc qualified on two local calendar days (the org-local→UTC
        // windows overlapped) and only the 26h SentAt guard suppressed the re-fire. Since #270's
        // UTC-day bracket, tick 2's Jan 16 target is disjoint from the doc's Jan 15 UTC day, so
        // the invariant now holds structurally as well — a regression in EITHER layer (window
        // bracketing or the ADR 0015 guard, which the width theory below pins directly) shows up
        // here as a second send.
        var docExpiresUtc = new DateTime(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc);
        var seed = await SeedReminderAsync(
            NyEightAm, timeZone: NyTz, daysBefore: 0,
            internalEmail: "owner@example.com",
            overrideExpirationUtc: docExpiresUtc);

        // Tick 1: NY-local 08:00 Jan 15 (13:00 UTC). One send; SendDate = Jan 15.
        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");

        // Owner edits the org time zone to Asia/Tokyo (the #185 capability). Updating the row
        // directly is equivalent to the endpoint's effect — the worker reads TimeZone fresh each
        // tick.
        await using (var db = CreateSystemDb())
        {
            var org = await db.Organizations.SingleAsync(o => o.Id == seed.OrgId);
            org.TimeZone = TokyoTz;
            await db.SaveChangesAsync();
        }

        // Tick 2: 23:00 UTC Jan 15 = Tokyo-local 08:00 Jan 16 → the window re-opens with
        // SendDate = Jan 16 (≠ Jan 15). Post-#270 the Jan 16 UTC-day bracket no longer matches
        // the doc; and even if it did, the guard sees the prior SentAt (13:00 UTC Jan 15) inside
        // the trailing 26h window and suppresses it.
        var tokyoNextDayTick = new DateTimeOffset(2026, 1, 15, 23, 0, 0, TimeSpan.Zero);
        await BuildWorker(tokyoNextDayTick).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle("the zone edit must not re-send the same reminder");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);

        // The single retained row is the NY-local Jan 15 send; no Jan 16 row was written.
        await using var assertDb = CreateSystemDb();
        var log = await assertDb.ReminderLogs.SingleAsync(l => l.ReminderId == seed.ReminderId);
        log.SendDate.Should().Be(new DateOnly(2026, 1, 15));
    }

    [Theory]
    [InlineData(25.0, true)]   // just inside the 26h window → suppressed (even on an earlier SendDate)
    [InlineData(27.0, false)]  // just outside the window → not suppressed
    [InlineData(48.0, false)]  // a stale prior occurrence, well outside → not suppressed
    public async Task Tz_edit_guard_suppresses_a_prior_send_only_within_the_26h_window(
        double priorSendHoursAgo, bool expectSuppressed)
    {
        // Pins the guard WIDTH. The prior log is written on an EARLIER SendDate so the same-day arm
        // (l.SendDate == sendDate) cannot match — only the new trailing-window arm
        // (l.SentAt >= nowUtc - TzEditDedupeWindow) decides. A prior send is suppressed iff its
        // SentAt is inside the 26h window. A regression that shrank or grew TzEditDedupeWindow flips
        // one of these cases — the marquee test's ~10h gap survives any plausible shrink, so it can't
        // pin the width on its own. The 48h case also pins the lower bound: a stale send never wedges
        // a (reminder, doc, recipient) forever, it stays a *recent*-send check.
        var seed = await SeedReminderAsync(NyEightAm, internalEmail: "owner@example.com");

        var priorSentAt = NyEightAm.UtcDateTime.AddHours(-priorSendHoursAgo);
        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "owner@example.com",
                SentAt = priorSentAt,
                SendDate = DateOnly.FromDateTime(priorSentAt), // earlier day, ≠ today's Jan 15
                ResendMessageId = "resend_prior",
                Status = "sent",
            });
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        if (expectSuppressed)
        {
            Email.Sends.Should().BeEmpty("a prior send inside the 26h window suppresses today's send");
            (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1); // only the prior row
        }
        else
        {
            Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
            (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(2); // prior + today's
        }
    }

    [Fact]
    public async Task Tz_edit_guard_is_per_recipient_a_recent_send_to_one_does_not_suppress_another()
    {
        // The widened clause drops the SendDate constraint; the per-RecipientEmail HashSet is what
        // keeps it from over-suppressing. A recent send to the internal owner (on an earlier
        // SendDate, so matched only by the new SentAt arm) must NOT suppress the vendor's first send.
        var seed = await SeedReminderAsync(
            NyEightAm, notifyInternal: true, notifyVendor: true,
            internalEmail: "owner@example.com", vendorEmail: "vendor@example.com");

        // Prior send to owner only, 20h ago (inside the 26h window) on an earlier SendDate.
        var priorSentAt = NyEightAm.UtcDateTime.AddHours(-20);
        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "owner@example.com",
                SentAt = priorSentAt,
                SendDate = DateOnly.FromDateTime(priorSentAt), // Jan 14, ≠ today's Jan 15
                ResendMessageId = "resend_prior_owner",
                Status = "sent",
            });
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        // Owner is suppressed by the guard; vendor (no prior send) is sent.
        Email.Sends.Select(s => s.ToEmail).Should().Equal(["vendor@example.com"]);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(2); // prior owner + new vendor
    }

    [Fact]
    public async Task Tz_edit_guard_is_per_document_a_recent_send_for_one_doc_does_not_suppress_another()
    {
        // The lookup is pinned by DocumentId, so a recent send for doc A must not suppress doc B
        // under the same reminder. Structurally guaranteed by the unchanged DocumentId filter; this
        // pins it so a future mis-scoping regression in the widened clause is caught.
        var orgId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();
        var docAId = Guid.NewGuid();
        var docBId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = NyEightAm.UtcDateTime;
        var exp = ExpirationForOrgWindow(NyTz, NyEightAm, 30);

        await using (var db = CreateSystemDb())
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", TimeZone = NyTz, CreatedAt = now, UpdatedAt = now });
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "V", CreatedAt = now, UpdatedAt = now });
            db.Users.Add(new User { Id = userId, OrganizationId = orgId, Email = "owner@example.com", PasswordHash = "x", FullName = "Owner", Role = "admin", CreatedAt = now });
            db.Documents.AddRange(
                new Document { Id = docAId, OrganizationId = orgId, VendorId = vendorId, OriginalFileName = "a.pdf", BlobStorageUrl = "b", FileSizeBytes = 1, ContentType = "application/pdf", ExpirationDate = exp, CreatedAt = now, UpdatedAt = now },
                new Document { Id = docBId, OrganizationId = orgId, VendorId = vendorId, OriginalFileName = "b.pdf", BlobStorageUrl = "b", FileSizeBytes = 1, ContentType = "application/pdf", ExpirationDate = exp, CreatedAt = now, UpdatedAt = now });
            db.Reminders.Add(new Reminder { Id = reminderId, OrganizationId = orgId, DaysBefore = 30, NotifyInternalUser = true, IsActive = true });
            // Recent send for doc A only, 20h ago on an earlier SendDate.
            var priorSentAt = now.AddHours(-20);
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = reminderId,
                DocumentId = docAId,
                RecipientEmail = "owner@example.com",
                SentAt = priorSentAt,
                SendDate = DateOnly.FromDateTime(priorSentAt),
                ResendMessageId = "resend_prior_docA",
                Status = "sent",
            });
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        // Doc A's recipient is suppressed (recent send); doc B's is sent — the guard is per-document.
        Email.Sends.Select(s => s.ToEmail).Should().Equal(["owner@example.com"]); // exactly one, for doc B
        (await LogCountAsync(reminderId, docAId)).Should().Be(1); // prior only, no re-send
        (await LogCountAsync(reminderId, docBId)).Should().Be(1); // newly sent
    }

    [Fact]
    public async Task Editing_time_zone_suppresses_the_re_fire_for_both_internal_and_vendor_recipients()
    {
        // The multi-recipient end-to-end. The double-send is most costly for the vendor (outside the
        // org's trust boundary, real Resend cost). Tick 1 (NY) sends to owner + vendor; after the zone
        // edit, tick 2 (Tokyo, next local day) must suppress BOTH — structurally via the #270 UTC-day
        // bracket, and by the ADR 0015 guard behind it.
        var docExpiresUtc = new DateTime(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc);
        var seed = await SeedReminderAsync(
            NyEightAm, timeZone: NyTz, daysBefore: 0,
            notifyInternal: true, notifyVendor: true,
            internalEmail: "owner@example.com", vendorEmail: "vendor@example.com",
            overrideExpirationUtc: docExpiresUtc);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        Email.Sends.Select(s => s.ToEmail).Should().BeEquivalentTo(["owner@example.com", "vendor@example.com"]);

        await using (var db = CreateSystemDb())
        {
            var org = await db.Organizations.SingleAsync(o => o.Id == seed.OrgId);
            org.TimeZone = TokyoTz;
            await db.SaveChangesAsync();
        }

        var tokyoNextDayTick = new DateTimeOffset(2026, 1, 15, 23, 0, 0, TimeSpan.Zero);
        await BuildWorker(tokyoNextDayTick).ProcessHourlyTickAsync(CancellationToken.None);

        // No additional sends; exactly the two original rows remain, both on NY-local Jan 15.
        Email.Sends.Should().HaveCount(2);
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(2);
        await using var assertDb = CreateSystemDb();
        var sendDates = await assertDb.ReminderLogs
            .Where(l => l.ReminderId == seed.ReminderId)
            .Select(l => l.SendDate)
            .ToListAsync();
        sendDates.Should().AllBeEquivalentTo(new DateOnly(2026, 1, 15));
    }

    [Fact]
    public async Task Editing_time_zone_across_an_extreme_offset_jump_still_dedupes_a_two_day_send_date_shift()
    {
        // The asymmetric / robustness case the SentAt key (vs a SendDate-adjacency guard) exists
        // for: an extreme eastward jump — Pacific/Pago_Pago (UTC-11) → Pacific/Kiritimati
        // (UTC+14), a 25h increase — pushes the second qualifying tick TWO local calendar days
        // later (SendDate shifts Jan 15 → Jan 17) while the two ticks stay 23h apart in UTC.
        // When written, the doc landed in both org-local windows and only the 26h SentAt guard
        // (which a ±1-day SendDate-adjacency rule would miss) prevented the re-send. Post-#270
        // the Jan 17 UTC-day bracket is disjoint from the doc's Jan 15 face date, so the
        // invariant holds structurally as well — this stays as the end-to-end extreme-jump pin,
        // and the guard's width is pinned directly by the 25h/27h/48h theory above.
        const string pagoPago = "Pacific/Pago_Pago";   // UTC-11, no DST
        const string kiritimati = "Pacific/Kiritimati"; // UTC+14, no DST

        // Face date Jan 15 at UTC midnight + intra-day time — inside the Jan 15 UTC-day bracket
        // that the Pago_Pago tick (localDate Jan 15, DaysBefore 0) targets.
        var docExpiresUtc = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var pagoTick = new DateTimeOffset(2026, 1, 15, 19, 0, 0, TimeSpan.Zero); // Pago_Pago 08:00 Jan 15
        var seed = await SeedReminderAsync(
            pagoTick, timeZone: pagoPago, daysBefore: 0,
            internalEmail: "owner@example.com",
            overrideExpirationUtc: docExpiresUtc);

        await BuildWorker(pagoTick).ProcessHourlyTickAsync(CancellationToken.None);
        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        await using (var db = CreateSystemDb())
        {
            var firstLog = await db.ReminderLogs.SingleAsync(l => l.ReminderId == seed.ReminderId);
            firstLog.SendDate.Should().Be(new DateOnly(2026, 1, 15)); // Pago_Pago-local day
        }

        await using (var db = CreateSystemDb())
        {
            var org = await db.Organizations.SingleAsync(o => o.Id == seed.OrgId);
            org.TimeZone = kiritimati;
            await db.SaveChangesAsync();
        }

        var kiritimatiTick = new DateTimeOffset(2026, 1, 16, 18, 0, 0, TimeSpan.Zero); // Kiritimati 08:00 Jan 17
        await BuildWorker(kiritimatiTick).ProcessHourlyTickAsync(CancellationToken.None);

        // localDate Jan 17 (a 2-day shift); without the SentAt guard it would double-send. The
        // 23h-old prior send is inside the 26h window → suppressed.
        Email.Sends.Should().ContainSingle("a 2-day SendDate shift from an extreme zone edit must not re-send");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Editing_time_zone_within_the_same_local_day_still_dedupes_via_the_same_day_key()
    {
        // The widening must be purely ADDITIVE: a zone edit whose next qualifying tick lands on the
        // SAME local calendar day is still deduped by the unchanged same-day SendDate arm (ADR 0002 /
        // 0007), independent of the new guard. NY → Chicago: NY 08:00 Jan 15 (13:00 UTC) then Chicago
        // 08:00 Jan 15 (14:00 UTC) — both localDate Jan 15, same SendDate.
        var docExpiresUtc = new DateTime(2026, 1, 15, 18, 0, 0, DateTimeKind.Utc); // mid-Jan-15 in both zones
        var seed = await SeedReminderAsync(
            NyEightAm, timeZone: NyTz, daysBefore: 0,
            internalEmail: "owner@example.com",
            overrideExpirationUtc: docExpiresUtc);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);
        Email.Sends.Should().ContainSingle();

        await using (var db = CreateSystemDb())
        {
            var org = await db.Organizations.SingleAsync(o => o.Id == seed.OrgId);
            org.TimeZone = "America/Chicago";
            await db.SaveChangesAsync();
        }

        var chicagoTick = new DateTimeOffset(2026, 1, 15, 14, 0, 0, TimeSpan.Zero); // Chicago 08:00 Jan 15
        await BuildWorker(chicagoTick).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle("a same-local-day zone edit is deduped by the unchanged SendDate key");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
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

    // ----- ADR 0025 half B: failed sends retry in place ---------------------------------------
    // A Resend non-2xx (messageId == null) persists a Status='failed' row. Pre-#270 that row was
    // treated as already-sent by every subsequent tick — a 30s Resend outage at 08:00 silently
    // dropped the day's warnings forever. Now failed rows are excluded from dedupe and the retry
    // heals the SAME row in place (the unique index admits one row per tuple).

    [Fact]
    public async Task Failed_send_is_retried_on_a_later_tick_and_heals_the_same_log_row()
    {
        var seed = await SeedReminderAsync(NyEightAm);
        Email.NextSendReturnsNull = true;

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        // First tick: send was attempted (queue has the entry with null id) and a failed row exists.
        Email.Sends.Should().ContainSingle().Which.MessageId.Should().BeNull();
        Guid failedRowId;
        await using (var db = CreateSystemDb())
        {
            var log = await db.ReminderLogs.SingleAsync(l =>
                l.ReminderId == seed.ReminderId && l.DocumentId == seed.DocumentId);
            log.Status.Should().Be("failed");
            log.ResendMessageId.Should().BeNull();
            failedRowId = log.Id;
        }

        // Next hourly tick (09:00 NY): the failed row no longer dedupes; the retry succeeds and
        // heals the same row — no second row, no unique-index violation.
        var nineAmNy = NyEightAm.AddHours(1);
        await BuildWorker(nineAmNy).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().HaveCount(2);
        Email.Sends[1].MessageId.Should().NotBeNullOrEmpty();
        await using (var assertDb = CreateSystemDb())
        {
            var log = await assertDb.ReminderLogs.SingleAsync(l =>
                l.ReminderId == seed.ReminderId && l.DocumentId == seed.DocumentId);
            log.Id.Should().Be(failedRowId, "the retry must heal the existing row in place, not insert");
            log.Status.Should().Be("sent");
            log.ResendMessageId.Should().NotBeNullOrEmpty();
            log.SentAt.Should().Be(nineAmNy.UtcDateTime, "SentAt records the latest attempt instant");
            log.SendDate.Should().Be(new DateOnly(2026, 1, 15));
        }
    }

    [Fact]
    public async Task Failed_retry_that_fails_again_keeps_status_failed_and_updates_the_attempt_instant()
    {
        var seed = await SeedReminderAsync(NyEightAm);
        Email.NextSendReturnsNull = true;

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.NextSendReturnsNull = true; // one-shot flag — arm again for the retry attempt
        var nineAmNy = NyEightAm.AddHours(1);
        await BuildWorker(nineAmNy).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().HaveCount(2, "every qualifying tick re-attempts a failed send");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
        await using var db = CreateSystemDb();
        var log = await db.ReminderLogs.SingleAsync(l =>
            l.ReminderId == seed.ReminderId && l.DocumentId == seed.DocumentId);
        log.Status.Should().Be("failed");
        log.ResendMessageId.Should().BeNull();
        log.SentAt.Should().Be(nineAmNy.UtcDateTime, "the failed row is re-stamped with the latest attempt");
    }

    [Theory]
    [InlineData("bounced")]
    [InlineData("complained")]
    [InlineData("delivered")]
    public async Task Non_failed_statuses_block_retry(string status)
    {
        // Only "failed" (Resend never accepted the mail) retries. Webhook-advanced statuses
        // describe ACCEPTED mail — auto-resending a hard bounce or a complaint is
        // sender-reputation damage, the exact wrong response (ADR 0025).
        var seed = await SeedReminderAsync(NyEightAm);
        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "owner@example.com",
                SentAt = NyEightAm.UtcDateTime.AddHours(-1),
                SendDate = new DateOnly(2026, 1, 15),
                ResendMessageId = "resend_prior",
                Status = status,
            });
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty();
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Failed_row_within_the_26h_guard_window_does_not_suppress_a_send()
    {
        // The failed-row exclusion applies to BOTH dedupe arms (ADR 0025): a failed attempt
        // yesterday — visible only through the trailing 26h SentAt guard arm (ADR 0015) — must
        // not suppress today's send. The historical failed row keys a different SendDate tuple,
        // so today's send inserts a fresh row and leaves the failure record untouched.
        var seed = await SeedReminderAsync(NyEightAm);
        var priorSentAt = NyEightAm.UtcDateTime.AddHours(-20); // inside the 26h window, Jan 14
        await using (var db = CreateSystemDb())
        {
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(),
                ReminderId = seed.ReminderId,
                DocumentId = seed.DocumentId,
                RecipientEmail = "owner@example.com",
                SentAt = priorSentAt,
                SendDate = DateOnly.FromDateTime(priorSentAt), // Jan 14, ≠ today's Jan 15
                ResendMessageId = null,
                Status = "failed",
            });
            await db.SaveChangesAsync();
        }

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(2);
        await using var assertDb = CreateSystemDb();
        var rows = await assertDb.ReminderLogs
            .Where(l => l.ReminderId == seed.ReminderId)
            .OrderBy(l => l.SentAt)
            .ToListAsync();
        rows[0].Status.Should().Be("failed", "yesterday's failure record stays untouched");
        rows[0].SendDate.Should().Be(new DateOnly(2026, 1, 14));
        rows[1].Status.Should().Be("sent");
        rows[1].SendDate.Should().Be(new DateOnly(2026, 1, 15));
    }

    [Fact]
    public async Task Send_throw_leaves_no_row_and_a_later_tick_retries()
    {
        // When SendAsync THROWS (transport fault / HttpClient timeout) the worker writes no row
        // at all. Pre-#270 the single daily qualifying tick had already passed — the day was
        // dropped; now the next catch-up tick retries naturally (no row → no dedupe entry).
        var seed = await SeedReminderAsync(NyEightAm);
        Email.NextSendThrows = new HttpRequestException("resend unreachable");

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty("a throw records no attempt");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);

        await BuildWorker(NyEightAm.AddHours(1)).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task Only_the_failed_recipient_is_retried_not_the_already_served_one()
    {
        // Partial failure inside one (reminder, doc): the internal send fails (Resend non-2xx),
        // the vendor send succeeds. The retry tick re-attempts ONLY the failed recipient — the
        // served one stays deduped per ADR 0002's per-recipient key.
        var seed = await SeedReminderAsync(NyEightAm, notifyInternal: true, notifyVendor: true);
        Email.NextSendReturnsNull = true; // one-shot: fails the first send (owner); vendor succeeds

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().HaveCount(2);

        await BuildWorker(NyEightAm.AddHours(1)).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().HaveCount(3, "exactly one retry — the vendor stays deduped");
        Email.Sends[2].ToEmail.Should().Be("owner@example.com");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(2);
        await using var db = CreateSystemDb();
        var rows = await db.ReminderLogs.Where(l => l.ReminderId == seed.ReminderId).ToListAsync();
        rows.Should().OnlyContain(r => r.Status == "sent");
    }

    // ----- #270 half 3: expiry matches on the UTC calendar day of the face date ---------------
    // ExpirationDate stores the document's face date at UTC midnight (CanonicalDocumentFields).
    // Pre-#270 the worker bracketed the org-LOCAL day converted to UTC, so for every UTC-negative
    // org the UTC-midnight face date fell into the previous local day's bracket — the "14 days
    // before" email fired 15 days out while the body claimed 14.

    [Fact]
    public async Task US_timezone_org_fires_on_the_exact_target_day_not_a_day_early()
    {
        // NY-local Jan 15 + 14 days = face date Jan 29, stored as Jan 29 00:00 UTC. Pre-#270 the
        // NY window for target Jan 29 was [Jan 29 05:00, Jan 30 05:00) UTC — missing this doc on
        // its true day (it had already fired a day early instead).
        var faceDate = new DateTime(2026, 1, 29, 0, 0, 0, DateTimeKind.Utc);
        var seed = await SeedReminderAsync(NyEightAm, daysBefore: 14, overrideExpirationUtc: faceDate);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().ContainSingle("the reminder fires exactly DaysBefore days before the face date");
        Email.Sends[0].HtmlBody.Should().Contain("14 days from today", "the body copy matches reality");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(1);
    }

    [Fact]
    public async Task US_timezone_org_does_not_fire_a_day_early()
    {
        // The complement: a face date 15 days out must NOT match today's 14-day target. Pre-#270
        // the NY bracket for target Jan 29 captured the Jan 30 UTC-midnight face date — this is
        // the day-early send the ticket reported.
        var faceDate = new DateTime(2026, 1, 30, 0, 0, 0, DateTimeKind.Utc);
        var seed = await SeedReminderAsync(NyEightAm, daysBefore: 14, overrideExpirationUtc: faceDate);

        await BuildWorker(NyEightAm).ProcessHourlyTickAsync(CancellationToken.None);

        Email.Sends.Should().BeEmpty("a 15-days-out document must not match the 14-day reminder");
        (await LogCountAsync(seed.ReminderId, seed.DocumentId)).Should().Be(0);
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
