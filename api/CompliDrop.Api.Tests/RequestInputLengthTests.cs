using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CompliDrop.Api.Tests.TestHelpers.UploadFixtures;

namespace CompliDrop.Api.Tests;

/// <summary>
/// End-to-end coverage for the request-input length guards (#389,
/// <c>docs/adr/0046-request-input-length-guards.md</c>). Every case here used to be a generic <b>500</b>:
/// Npgsql does not truncate, so an over-length string written to a bounded <c>varchar(n)</c> failed the
/// whole <c>SaveChanges</c> with Postgres <c>22001</c> and the unhandled <c>DbUpdateException</c> came
/// back as a server error on input that deserved a 400 — on the public routes, input a third party chose.
/// <para/>
/// The suite is organized by the ticket's own priority order: PUBLIC / anonymous first (portal upload,
/// register, waitlist), then the dashboard blob-orphan, then the authenticated set. Each guarded field is
/// asserted at BOTH boundaries — exactly at the limit succeeds, one character over is refused — because a
/// guard written with the wrong comparison passes a one-sided test.
/// <para/>
/// It also carries the behaviour that replaced <c>CanonicalDocumentTypeTests</c>' retired
/// set-equality pin: #389 deleted <c>DocumentEndpoints</c>' duplicate <c>AllowedDocumentTypes</c>
/// literal (the collapse ADR 0045 § "Option E" deferred here), so the vocabulary contract those
/// endpoints owe is pinned over HTTP instead of by comparing two C# sets.
/// </summary>
public sealed class RequestInputLengthTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static string Chars(int count) => new('x', count);

    private static async Task<(string? Code, string? Message)> ErrorOf(HttpResponseMessage resp)
    {
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        return (error.GetProperty("code").GetString(), error.GetProperty("message").GetString());
    }

    /// <summary>Asserts a 400 carrying the shared over-length code and a message an SMB user can act on.</summary>
    private static async Task ShouldBeFriendly400(HttpResponseMessage resp, string label, int limit)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an over-length value is the caller's mistake, not a server error — a 22001 500 is the bug #389 fixes");
        var (code, message) = await ErrorOf(resp);
        code.Should().Be(InputLength.TooLongCode);
        message.Should().Be(InputLength.TooLongMessage(label, limit));
        // CLAUDE.md § Frontend error-message policy: the frontend renders error.message verbatim, so it
        // must never carry HTTP jargon or a raw column-width dump.
        message.Should().NotContainAny("22001", "varchar", "DbUpdate", "Npgsql");
    }

    private FakeBlobStorageService Blobs =>
        (FakeBlobStorageService)Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();

    // ───────────────────────── PUBLIC: the vendor portal upload ─────────────────────────

    [Theory]
    [InlineData(InputLengths.DocumentOriginalFileName)]      // exactly the column width — stored whole
    [InlineData(InputLengths.DocumentOriginalFileName + 1)]  // one over — stored clamped, NOT refused
    [InlineData(4_000)]                                      // far over: ASP.NET admits ~16KB of header
    public async Task Portal_upload_clamps_an_over_length_filename_instead_of_failing(int nameLength)
    {
        // OriginalFileName is varchar(500) and the portal stored it RAW: SanitizeFileName's 120-char
        // clamp only ever applied to the blob NAME. The 22001 landed inside the permit transaction,
        // after the blob was uploaded — so the vendor lost the upload and got a 500 they could not
        // retry out of. CLAMPED rather than rejected: a vendor must never be refused their certificate
        // over what their phone named the file, and a truncated name is still recognizable.
        var link = await SeedLinkAsync();
        var fileName = Chars(nameLength - ".pdf".Length) + ".pdf";

        var resp = await CreateClient().PostAsync(
            $"/api/portal/{link.Token}/upload", UploadForm(PdfBytes(), fileName, "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        var stored = await db.Documents.SingleAsync(d => d.OrganizationId == link.OrgId);
        stored.OriginalFileName.Should().Be(
            fileName.Length <= InputLengths.DocumentOriginalFileName
                ? fileName
                : fileName[..InputLengths.DocumentOriginalFileName]);
    }

    [Theory]
    [InlineData("coi", "coi")]                     // canonical, untouched
    [InlineData("COI", "coi")]                     // mis-cased: folded, not stored verbatim
    [InlineData("banana", "other")]                // unknown word
    [InlineData("", "other")]                      // blank — the pre-#389 special case, now subsumed
    public async Task Portal_upload_coerces_documentType_into_the_vocabulary(string submitted, string expected)
    {
        // The public form field was stored VERBATIM. Two failures in one: >100 chars 22001'd the insert,
        // and — silently, which is worse — a non-canonical value wrote a type no compliance rule can
        // ordinally match, so the document graded against nothing while still reading as coverage
        // (ADR 0045). Coerced, not rejected: a stray form value must not cost the vendor the upload.
        var link = await SeedLinkAsync();

        var resp = await CreateClient().PostAsync(
            $"/api/portal/{link.Token}/upload",
            UploadForm(PdfBytes(), "coi.pdf", "application/pdf",
                new Dictionary<string, string> { ["documentType"] = submitted }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.OrganizationId == link.OrgId)).DocumentType.Should().Be(expected);
    }

    [Fact]
    public async Task Portal_upload_coerces_an_over_length_documentType_to_the_fallback()
    {
        // The #373 oversize-documentType 500 the ticket asks us to dedupe into this one: 5,000 chars into
        // a varchar(100). It names no type we know, so it resolves like any other unknown word.
        var link = await SeedLinkAsync();

        var resp = await CreateClient().PostAsync(
            $"/api/portal/{link.Token}/upload",
            UploadForm(PdfBytes(), "coi.pdf", "application/pdf",
                new Dictionary<string, string> { ["documentType"] = Chars(5_000) }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.OrganizationId == link.OrgId))
            .DocumentType.Should().Be(CanonicalDocumentTypes.Fallback);
    }

    // ───────────────────────── PUBLIC: register ─────────────────────────

    public static TheoryData<string, int, string> RegisterFields => new()
    {
        // field, column width, the label the 400 message names
        { "companyName", InputLengths.OrganizationName, "Company name" },
        { "fullName", InputLengths.UserFullName, "Full name" },
        { "industry", InputLengths.OrganizationIndustry, "Industry" },
        // CompanySize at 20 is the NARROWEST column in the app fed by request input — the sharpest edge.
        { "companySize", InputLengths.OrganizationCompanySize, "Company size" },
    };

    [Theory]
    [MemberData(nameof(RegisterFields))]
    public async Task Register_refuses_an_over_length_field_with_a_friendly_400(string field, int limit, string label)
    {
        var resp = await CreateClient().PostAsJsonAsync("/api/auth/register", RegisterBody(field, Chars(limit + 1)));

        await ShouldBeFriendly400(resp, label, limit);
        await using var db = CreateSystemDb();
        (await db.Users.CountAsync()).Should().Be(0, "a refused registration must not half-create an account");
    }

    [Theory]
    [MemberData(nameof(RegisterFields))]
    public async Task Register_accepts_a_field_exactly_at_the_column_width(string field, int limit, string label)
    {
        _ = label;
        var resp = await CreateClient().PostAsJsonAsync("/api/auth/register", RegisterBody(field, Chars(limit)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "exactly-at-the-limit is a legal value — the guard says 'or fewer', and rejecting it would " +
            "be an off-by-one that only a boundary test catches");
    }

    [Fact]
    public async Task Register_measures_the_trimmed_value_the_row_actually_receives()
    {
        // The row is written from CompanyName.Trim(), so the guard must measure the trimmed value.
        // Checking the raw property instead would refuse a name that fits once trimmed — a guard
        // rejecting input the write would have accepted.
        var resp = await CreateClient().PostAsJsonAsync(
            "/api/auth/register", RegisterBody("companyName", "   " + Chars(InputLengths.OrganizationName) + "   "));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Organizations.SingleAsync(o => o.Name.Length == InputLengths.OrganizationName))
            .Name.Should().NotStartWith(" ");
    }

    private static Dictionary<string, object?> RegisterBody(string overriddenField, string value)
    {
        var body = new Dictionary<string, object?>
        {
            ["email"] = $"user-{Guid.NewGuid():N}@example.com",
            ["password"] = "Password1234",
            ["fullName"] = "Test User",
            ["companyName"] = "Test Co",
            ["industry"] = (string?)null,
            ["companySize"] = (string?)null,
            ["timeZone"] = "America/New_York",
        };
        body[overriddenField] = value;
        return body;
    }

    // ───────────────────────── PUBLIC: waitlist ─────────────────────────

    public static TheoryData<string, int, string> WaitlistFields => new()
    {
        { "email", InputLengths.WaitlistEmail, "Email" },
        { "companyName", InputLengths.WaitlistCompanyName, "Company name" },
        { "industry", InputLengths.WaitlistIndustry, "Industry" },
    };

    [Theory]
    [MemberData(nameof(WaitlistFields))]
    public async Task Waitlist_refuses_an_over_length_field_with_a_friendly_400(string field, int limit, string label)
    {
        var value = field == "email" ? EmailOfLength(limit + 1) : Chars(limit + 1);

        var resp = await CreateClient().PostAsJsonAsync("/api/waitlist", WaitlistBody(field, value));

        await ShouldBeFriendly400(resp, label, limit);
        await using var db = CreateSystemDb();
        (await db.WaitlistEntries.CountAsync()).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(WaitlistFields))]
    public async Task Waitlist_accepts_a_field_exactly_at_the_column_width(string field, int limit, string label)
    {
        _ = label;
        var value = field == "email" ? EmailOfLength(limit) : Chars(limit);

        var resp = await CreateClient().PostAsJsonAsync("/api/waitlist", WaitlistBody(field, value));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.WaitlistEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Waitlist_clamps_source_rather_than_refusing_the_signup()
    {
        // Source is the one waitlist column the VISITOR does not type — it is an attribution tag the
        // page sets and never reads back. Refusing a signup over the caller's own bug would punish the
        // wrong person, so this side of the reject/clamp split goes through ColumnClamp.To (ADR 0046).
        var resp = await CreateClient().PostAsJsonAsync(
            "/api/waitlist", WaitlistBody("source", Chars(InputLengths.WaitlistSource + 500)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.WaitlistEntries.SingleAsync()).Source
            .Should().HaveLength(InputLengths.WaitlistSource);
    }

    [Fact]
    public async Task Waitlist_repeat_signup_returns_the_friendly_message()
    {
        // The sequential arm — the exists-check fast path. Pinned alongside the concurrent arm below so
        // both roads to "you're already on the list" are asserted to answer identically.
        var client = CreateClient();
        var email = $"repeat-{Guid.NewGuid():N}@example.com";

        (await client.PostAsJsonAsync("/api/waitlist", WaitlistBody("email", email))).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var second = await client.PostAsJsonAsync("/api/waitlist", WaitlistBody("email", email));

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("message").GetString().Should().Be("You're on the list!");
    }

    [Fact]
    public async Task Concurrent_duplicate_waitlist_signups_all_return_the_friendly_200()
    {
        // The TOCTOU the ticket reports: the exists-check and the insert are two statements, so
        // concurrent submits of the same address all see "not on the list" and every loser hits the
        // (Email) unique index. That was a 500 on a public marketing form for a visitor who did nothing
        // wrong — and the honest answer is the one the repeat signup already gets, because by the time
        // the loser fails, the address IS on the list.
        //
        // Fixed by CATCHING the violation, not by adding an index: the unique index already exists, and
        // creating one over a table that may already hold duplicates would fail the startup
        // auto-migration and take production down (ADR 0016).
        //
        // Five racers rather than two so the losing arm is exercised rather than hoped for. Either arm
        // yields the same observable contract, which is the point; a regression that drops the catch
        // surfaces as a 500 here.
        var email = $"race-{Guid.NewGuid():N}@example.com";
        var client = CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync("/api/waitlist", WaitlistBody("email", email))));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK,
            "a duplicate is not an error for the visitor — a 500 here is the unhandled 23505");
        await using var db = CreateSystemDb();
        (await db.WaitlistEntries.CountAsync(e => e.Email == email)).Should().Be(1);
    }

    /// <summary>A syntactically plausible address of exactly <paramref name="length"/> characters.</summary>
    private static string EmailOfLength(int length) => Chars(length - "@example.com".Length) + "@example.com";

    private static Dictionary<string, object?> WaitlistBody(string overriddenField, string value)
    {
        var body = new Dictionary<string, object?>
        {
            ["email"] = $"signup-{Guid.NewGuid():N}@example.com",
            ["companyName"] = (string?)null,
            ["industry"] = (string?)null,
            ["source"] = (string?)null,
        };
        body[overriddenField] = value;
        return body;
    }

    // ───────────────────────── The dashboard upload's orphaned blob ─────────────────────────

    [Fact]
    public async Task A_failed_document_insert_leaves_no_orphaned_blob()
    {
        // The blob is uploaded BEFORE the insert, and only the IsKeyConflict catch used to delete it —
        // so ANY other SaveChanges failure (the 22001 this ticket removes at the source, a dropped
        // connection, a constraint nobody anticipated) left a paid-for blob in storage with no row
        // pointing at it and nothing that would ever find it. Cleanup is now unconditional
        // (documentPersisted + finally), mirroring the portal twin.
        //
        // The failure is driven through the one column on that insert whose value the SERVER derives —
        // BlobStorageUrl, varchar(500) — precisely because every request-controlled column is now
        // guarded. It is a genuine Postgres 22001 out of the same SaveChanges, and it is NOT an
        // idempotency-key conflict, which is exactly the class that used to orphan.
        var auth = await RegisterAndLoginAsync();
        Blobs.UrlOverride = "memory://" + Chars(600);

        var resp = await auth.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));

        // 500 specifically, not merely "not 2xx": it proves the request got PAST every 400-returning
        // guard and into the SaveChanges, so a green BlobCount below cannot come from an upload that
        // never happened.
        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await using var db = CreateSystemDb();
        (await db.Documents.CountAsync(d => d.OrganizationId == auth.OrgId)).Should().Be(0);
        Blobs.BlobCount.Should().Be(0, "the blob whose Document never committed must not survive the request");
    }

    // ───────────────────────── The dashboard upload's own input ─────────────────────────

    [Fact]
    public async Task Dashboard_upload_clamps_an_over_length_filename()
    {
        var auth = await RegisterAndLoginAsync();
        var fileName = Chars(InputLengths.DocumentOriginalFileName + 200) + ".pdf";

        var resp = await auth.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), fileName, "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.OrganizationId == auth.OrgId))
            .OriginalFileName.Should().HaveLength(InputLengths.DocumentOriginalFileName);
    }

    [Theory]
    [InlineData("coi", "coi")]
    [InlineData("COI", "coi")]
    [InlineData("banana", "other")]
    [InlineData("", "other")]
    public async Task Dashboard_upload_coerces_documentType_into_the_vocabulary(string submitted, string expected)
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync(
            "/api/documents/upload",
            UploadForm(PdfBytes(), "coi.pdf", "application/pdf",
                new Dictionary<string, string> { ["documentType"] = submitted }));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.OrganizationId == auth.OrgId)).DocumentType.Should().Be(expected);
    }

    [Fact]
    public async Task Dashboard_upload_coerces_an_over_length_documentType_to_the_fallback()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync(
            "/api/documents/upload",
            UploadForm(PdfBytes(), "coi.pdf", "application/pdf",
                new Dictionary<string, string> { ["documentType"] = Chars(5_000) }));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.OrganizationId == auth.OrgId))
            .DocumentType.Should().Be(CanonicalDocumentTypes.Fallback);
    }

    // ───── The PATCH allow-list: what CanonicalDocumentTypeTests' retired set-equality pin guaranteed ─────

    [Fact]
    public async Task Patch_accepts_every_vocabulary_member_and_rejects_anything_else()
    {
        // #389 deleted DocumentEndpoints' duplicate AllowedDocumentTypes literal, so this behaviour is
        // no longer expressible as "two C# sets are equal" — it is asserted over HTTP instead. PATCH
        // REJECTS an unknown type where the two upload paths coerce it: a human deliberately re-typing
        // a document is choosing which rules grade it, and answering "other" silently would change what
        // it is checked against (the deliberate asymmetry of ADR 0045 §5).
        var auth = await RegisterAndLoginAsync();
        var docId = await UploadDocumentAsync(auth.Client);

        foreach (var type in CanonicalDocumentTypes.All)
        {
            var ok = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = type });
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"'{type}' is in the vocabulary");
            await using var db = CreateSystemDb();
            (await db.Documents.SingleAsync(d => d.Id == docId)).DocumentType.Should().Be(type);
        }

        // Mis-cased input is FOLDED, not rejected — that changes the spelling, not the meaning.
        (await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = "COI" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var rejected = await auth.Client.PatchAsJsonAsync(
            $"/api/documents/{docId}", new { documentType = "banana" });
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(rejected)).Code.Should().Be("document.invalid_type");

        // The over-length case: refused as an unknown type rather than 22001-ing the varchar(100).
        (await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = Chars(5_000) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ───────────────────────── Authenticated: the idempotency key ─────────────────────────

    [Fact]
    public async Task A_long_idempotency_key_is_clamped_and_still_replays_its_own_record()
    {
        // IdempotencyRecord.Key is varchar(200) and the raw header went to BOTH the lookup and the
        // stored record, so a long key 22001'd the co-committed insert — taking the Document down with
        // it (ADR 0029) and orphaning its blob. Clamping is only correct if it happens at the SINGLE
        // point the header is read: clamp the storage but not the lookup (or vice versa) and a repeat
        // of a long key misses its own record and uploads a SECOND document — idempotency silently
        // broken rather than loudly failed. That is what this test exists to catch.
        var auth = await RegisterAndLoginAsync();
        var key = Chars(300);

        var first = await PostWithIdempotency(auth.Client, key);
        var second = await PostWithIdempotency(auth.Client, key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        (await UploadedId(first)).Should().Be(await UploadedId(second), "the second call replays the first");

        await using var db = CreateSystemDb();
        (await db.Documents.CountAsync(d => d.OrganizationId == auth.OrgId)).Should().Be(1);
        (await db.IdempotencyRecords.SingleAsync(r => r.OrganizationId == auth.OrgId)).Key
            .Should().HaveLength(InputLengths.ClientIdempotencyKey);
    }

    [Fact]
    public async Task A_long_idempotency_key_does_not_500_the_sample_seed()
    {
        var auth = await RegisterAndLoginAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sample");
        req.Headers.Add("Idempotency-Key", Chars(300));

        (await auth.Client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ───────────────────────── Authenticated: vendors ─────────────────────────

    public static TheoryData<string, int, string> VendorFields => new()
    {
        { "name", InputLengths.VendorName, "Vendor name" },
        { "contactPhone", InputLengths.VendorContactPhone, "Phone" },
        { "category", InputLengths.VendorCategory, "Category" },
    };

    [Theory]
    [MemberData(nameof(VendorFields))]
    public async Task Creating_a_vendor_refuses_an_over_length_field(string field, int limit, string label)
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsJsonAsync("/api/vendors/", VendorBody(field, Chars(limit + 1)));

        await ShouldBeFriendly400(resp, label, limit);
    }

    [Theory]
    [MemberData(nameof(VendorFields))]
    public async Task Updating_a_vendor_refuses_an_over_length_field(string field, int limit, string label)
    {
        // Both paths, deliberately — the both-paths rule #369 established for the contact email. The
        // EDIT form is where these values actually get changed, and a guard on create alone leaves the
        // 500 sitting on the path that matters.
        var auth = await RegisterAndLoginAsync();
        var created = await auth.Client.PostAsJsonAsync("/api/vendors/", VendorBody("category", "Caterer"));
        var vendorId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();

        var resp = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", VendorBody(field, Chars(limit + 1)));

        await ShouldBeFriendly400(resp, label, limit);
    }

    [Theory]
    [MemberData(nameof(VendorFields))]
    public async Task Creating_a_vendor_accepts_a_field_exactly_at_the_column_width(string field, int limit, string label)
    {
        _ = label;
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsJsonAsync("/api/vendors/", VendorBody(field, Chars(limit)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Dictionary<string, object?> VendorBody(string overriddenField, string value)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Acme Catering",
            ["contactEmail"] = (string?)null,
            ["contactPhone"] = (string?)null,
            ["category"] = (string?)null,
            ["complianceTemplateId"] = (Guid?)null,
        };
        body[overriddenField] = value;
        return body;
    }

    // ───────────────────────── Authenticated: checklists and their rules ─────────────────────────

    [Theory]
    [InlineData("name", InputLengths.TemplateName, "Checklist name")]
    [InlineData("description", InputLengths.TemplateDescription, "Description")]
    public async Task Creating_a_checklist_refuses_an_over_length_field(string field, int limit, string label)
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsJsonAsync("/api/compliance/templates", TemplateBody(field, Chars(limit + 1)));

        await ShouldBeFriendly400(resp, label, limit);
    }

    [Theory]
    [InlineData("name", InputLengths.TemplateName, "Checklist name")]
    [InlineData("description", InputLengths.TemplateDescription, "Description")]
    public async Task Updating_a_checklist_refuses_an_over_length_field(string field, int limit, string label)
    {
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var resp = await auth.Client.PutAsJsonAsync(
            $"/api/compliance/templates/{templateId}", TemplateBody(field, Chars(limit + 1)));

        await ShouldBeFriendly400(resp, label, limit);
    }

    [Fact]
    public async Task Updating_a_checklist_with_a_null_name_is_a_400_not_a_500()
    {
        // Discovered on the line the length guard lands on: UpdateTemplate called req.Name.Trim() with
        // no blank check, so a body carrying "name": null bound null through the non-nullable record
        // property and NRE'd — an unhandled 500 where the create path has always answered a 400.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var resp = await auth.Client.PutAsJsonAsync(
            $"/api/compliance/templates/{templateId}", new { name = (string?)null, description = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Code.Should().Be("validation.name");
    }

    [Theory]
    [InlineData("fieldName", InputLengths.RuleFieldName, "Field name")]
    [InlineData("operator", InputLengths.RuleOperator, "Operator")]
    [InlineData("expectedValue", InputLengths.RuleExpectedValue, "Expected value")]
    [InlineData("errorMessage", InputLengths.RuleErrorMessage, "Requirement description")]
    public async Task Upserting_a_rule_refuses_an_over_length_field(string field, int limit, string label)
    {
        // ErrorMessage is the owner's own plain-English requirement text, printed on the auditor-facing
        // export — a truncated requirement is a CHANGED requirement, which is why every one of these
        // rejects rather than clamps.
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var resp = await auth.Client.PostAsJsonAsync(
            $"/api/compliance/templates/{templateId}/rules", RuleBody(field, Chars(limit + 1)));

        await ShouldBeFriendly400(resp, label, limit);
    }

    [Fact]
    public async Task Upserting_a_rule_accepts_fields_exactly_at_their_column_widths()
    {
        var auth = await RegisterAndLoginAsync();
        var templateId = await CreateTemplateAsync(auth.Client);

        var body = RuleBody("fieldName", Chars(InputLengths.RuleFieldName));
        body["expectedValue"] = Chars(InputLengths.RuleExpectedValue);
        body["errorMessage"] = Chars(InputLengths.RuleErrorMessage);

        var resp = await auth.Client.PostAsJsonAsync($"/api/compliance/templates/{templateId}/rules", body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Dictionary<string, object?> TemplateBody(string overriddenField, string value)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Caterers",
            ["description"] = (string?)null,
        };
        body[overriddenField] = value;
        return body;
    }

    private static Dictionary<string, object?> RuleBody(string overriddenField, string value)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = (Guid?)null,
            ["documentType"] = "coi",
            ["fieldName"] = "expiration_date",
            ["operator"] = "required",
            ["expectedValue"] = (string?)null,
            ["errorMessage"] = "The certificate must carry an expiration date.",
            ["sortOrder"] = 0,
        };
        body[overriddenField] = value;
        return body;
    }

    private async Task<Guid> CreateTemplateAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/compliance/templates", TemplateBody("name", "Caterers"));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();
    }

    // ───────────────────────── Authenticated: reminders and manual field edits ─────────────────────────

    [Fact]
    public async Task Updating_a_reminder_refuses_an_over_length_subject()
    {
        var auth = await RegisterAndLoginAsync();
        await using var db = CreateSystemDb();
        var reminderId = await db.Reminders.Where(r => r.OrganizationId == auth.OrgId)
            .Select(r => r.Id).FirstAsync();

        var resp = await auth.Client.PutAsJsonAsync($"/api/reminders/{reminderId}", new
        {
            notifyInternalUser = true,
            notifyVendor = false,
            isActive = true,
            emailSubjectTemplate = Chars(InputLengths.ReminderEmailSubjectTemplate + 1),
        });

        await ShouldBeFriendly400(resp, "Email subject", InputLengths.ReminderEmailSubjectTemplate);
    }

    [Theory]
    [InlineData("fieldName", InputLengths.DocumentFieldName, "Field name")]
    [InlineData("fieldValue", InputLengths.DocumentFieldValue, "Field value")]
    public async Task Editing_a_field_refuses_an_over_length_value(string field, int limit, string label)
    {
        // The 22001 here took the recomputed VERDICT down with the edit, because ADR 0030 commits both
        // in ONE unit of work — so the user's correction AND its verdict vanished behind a 500.
        // REJECTED rather than clamped, unlike the extraction worker writing the same two columns
        // (ADR 0045 §4): the worker is salvaging a model response nobody typed; this is the user's own
        // correction, and a silent half of it on a compliance record is the worse failure.
        var auth = await RegisterAndLoginAsync();
        var docId = await UploadDocumentAsync(auth.Client);

        var update = new Dictionary<string, object?>
        {
            ["fieldName"] = "policy_number",
            ["fieldValue"] = "POL-1",
        };
        update[field] = Chars(limit + 1);

        var resp = await auth.Client.PutAsJsonAsync(
            $"/api/documents/{docId}/fields", new { fields = new[] { update } });

        await ShouldBeFriendly400(resp, label, limit);
        await using var db = CreateSystemDb();
        (await db.DocumentFields.CountAsync(f => f.DocumentId == docId)).Should().Be(0,
            "a refused edit must not half-apply");
    }

    [Fact]
    public async Task Editing_a_field_accepts_a_value_exactly_at_the_column_width()
    {
        var auth = await RegisterAndLoginAsync();
        var docId = await UploadDocumentAsync(auth.Client);

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[]
            {
                new { fieldName = "policy_number", fieldValue = Chars(InputLengths.DocumentFieldValue) },
            },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.DocumentFields.SingleAsync(f => f.DocumentId == docId)).FieldValue
            .Should().HaveLength(InputLengths.DocumentFieldValue);
    }

    // ───────────────────────── shared upload helpers ─────────────────────────

    private static Task<HttpResponseMessage> PostWithIdempotency(HttpClient client, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/documents/upload")
        {
            Content = UploadForm(PdfBytes(), "coi.pdf", "application/pdf"),
        };
        req.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(req);
    }

    private static async Task<Guid> UploadedId(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();

    private static async Task<Guid> UploadDocumentAsync(HttpClient client)
    {
        var resp = await client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));
        resp.EnsureSuccessStatusCode();
        return await UploadedId(resp);
    }
}
