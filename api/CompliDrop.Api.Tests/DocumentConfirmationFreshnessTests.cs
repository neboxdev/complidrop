using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Regression tests for <see href="https://github.com/neboxdev/complidrop/issues/464">#464</see>
/// (ADR 0052 Amendment 3): <c>Document.IsManuallyVerified</c> is a PRESENT-TENSE claim about the field
/// values the row currently holds, so the one event that replaces them all —
/// <c>ExtractionWorker.PersistSuccess</c> — must withdraw it.
/// <para/>
/// The detail page renders the flag as a green shield reading <i>"A person confirmed these fields."</i>
/// Until #464 nothing ever cleared it: <c>DocumentEndpoints.ResolveManualReview</c> held the only
/// assignment in the codebase, and neither <c>Reextract</c>'s re-arm nor <c>PersistSuccess</c> touched it.
/// So confirm → "Read again" → a clean machine re-read left that sentence standing over values no person
/// had ever seen. Per <c>.claude/reviewers.md</c> § Project severity anchors, copy that overclaims
/// compliance certainty is major — and ADR 0042 Amendment 2 recorded the stickiness as accepted only on
/// the COVERAGE axis, which ADR 0052 has since closed by retiring the flag from <c>ComputeCoverage</c>.
/// This suite closes the display half.
/// <para/>
/// Three properties, and the middle one is the whole reason the fix is not a one-line assignment:
/// <list type="number">
/// <item><see cref="A_clean_re_read_withdraws_the_confirmation_whose_values_it_replaced"/> — the ticket's
/// own sequence, end to end over the wire, so what is asserted is the payload the page renders from.</item>
/// <item><see cref="A_confirmation_that_lands_MID_read_is_still_withdrawn_by_the_read_that_replaced_it"/>
/// — the FORCED write. <c>ProcessDocumentAsync</c> holds a minutes-old snapshot and EF emits only the
/// properties that differ from it, so a plain <c>= false</c> writes nothing in exactly the case that
/// matters: a confirmation committed on another connection while the worker was out at the LLM. Same
/// mechanism as <c>SetTrust</c> / <c>ForceVerdictWrite</c> (ADR 0052 §2), and this test is what goes red
/// if the force is removed while the assignment stays.</item>
/// <item><see cref="A_read_that_replaced_no_values_leaves_the_confirmation_alone"/> — the SCOPE. A failed
/// attempt and a queue re-arm write no field values, so the confirmed values are still the ones on the
/// row and clearing there would erase a claim that is still true. The absence mirrors trust's absence
/// from the queue path (ADR 0052 §2).</item>
/// </list>
/// The source-scan twin — one setter, one clearer, the clear forced, and no <c>SetProperty</c> form
/// anywhere — is <c>Adr0052EnforcementTests.The_confirmation_flag_has_ONE_setter_and_ONE_clearer</c>.
/// </summary>
public sealed class DocumentConfirmationFreshnessTests(IntegrationTestFixture fixture)
    : IntegrationTestBase(fixture)
{
    private FakeExtractionClient Extraction =>
        Fixture.Factory.Services.GetRequiredService<FakeExtractionClient>();

    private ExtractionWorker BuildWorker() => new(
        Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new ExtractionSettings()),
        NullLogger<ExtractionWorker>.Instance);

    /// <summary>
    /// A queued document in the caller's OWN org with its blob actually stored, so the worker can claim
    /// and process it for real and the same authenticated client can drive the endpoints against it.
    /// </summary>
    private async Task<Guid> SeedQueuedDocAsync(Guid orgId)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        var blobPath = $"blob/{docId:N}.pdf";
        await using (var db = CreateSystemDb())
        {
            db.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = orgId,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "blob://d",
                BlobStoragePath = blobPath,
                FileSizeBytes = 1024,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await Fixture.Factory.Services.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        return docId;
    }

    /// <summary>
    /// A successful extraction returning exactly <paramref name="fields"/>, every one comfortably clear of
    /// the manual-review gate and parseable — so the document settles <c>Completed</c> and nothing but the
    /// column under test can move.
    /// </summary>
    private static ExtractionResult CleanRead(params (string Name, string Value)[] fields) => new(
        DocumentType: "coi",
        DocumentSubType: null,
        Fields: [.. fields.Select(f => new ExtractedField(f.Name, f.Value, "string", 0.95))],
        NeedsReprocessing: false,
        Usage: new ExtractionUsage(InputTokens: 1000, OutputTokens: 200, EstimatedCostUsd: 0.01m));

    private async Task ClaimAndProcessAsync(Guid docId)
    {
        var worker = BuildWorker();
        var claimed = await worker.ClaimNextAsync(CancellationToken.None);
        claimed.Should().Be(docId, "precondition: the seeded document is the one the worker picks up");
        await worker.ProcessDocumentAsync(claimed.Value, CancellationToken.None);
    }

    private async Task<Document> GetDocAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        return await db.Documents.AsNoTracking().SingleAsync(d => d.Id == docId);
    }

    /// <summary>The detail payload the page renders the "Verified" cell from.</summary>
    private static async Task<JsonElement> ReadDetailAsync(HttpClient client, Guid docId)
    {
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        return body.GetProperty("data");
    }

    private static string? FieldValue(JsonElement detail, string name)
    {
        foreach (var field in detail.GetProperty("fields").EnumerateArray())
            if (field.GetProperty("fieldName").GetString() == name)
                return field.GetProperty("fieldValue").GetString();
        return null;
    }

    [Fact]
    public async Task A_clean_re_read_withdraws_the_confirmation_whose_values_it_replaced()
    {
        // The ticket's sequence, driven through the real endpoints and asserted on the real payload:
        // read → confirm → "Read again" → a clean machine re-read that replaces every value.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedQueuedDocAsync(auth.OrgId);

        Extraction.Result = CleanRead(("policy_number", "POL-FIRST"));
        await ClaimAndProcessAsync(docId);

        var confirm = await auth.Client.PutAsync($"/api/documents/{docId}/verify", content: null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmed = await ReadDetailAsync(auth.Client, docId);
        confirmed.GetProperty("isManuallyVerified").GetBoolean().Should().BeTrue(
            "precondition: the page is showing the green shield over the values this person just read");
        FieldValue(confirmed, "policy_number").Should().Be("POL-FIRST",
            "precondition: and these are the values they confirmed");

        var reextract = await auth.Client.PostAsync($"/api/documents/{docId}/reextract", content: null);
        reextract.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(auth.Client, docId)).GetProperty("isManuallyVerified").GetBoolean()
            .Should().BeTrue(
                "the re-arm itself replaces nothing — the row still holds the confirmed values, so the "
                + "claim is still true while the read is merely queued (ADR 0052 §2's scoping, mirrored)");

        Extraction.Result = CleanRead(("policy_number", "POL-SECOND"));
        await ClaimAndProcessAsync(docId);

        var afterReRead = await ReadDetailAsync(auth.Client, docId);
        FieldValue(afterReRead, "policy_number").Should().Be("POL-SECOND",
            "precondition: the machine re-read really did replace the value the human confirmed");
        afterReRead.GetProperty("isManuallyVerified").GetBoolean().Should().BeFalse(
            "the detail page renders this as \"A person confirmed these fields.\" beside a green shield — "
            + "and no person has seen THESE fields. Pre-#464 the flag was write-once, so the sentence "
            + "survived every re-read and asserted human verification over machine output");
        afterReRead.GetProperty("extractionStatus").GetString().Should().Be("Completed",
            "anti-no-op: the withdrawal must come from a read that SUCCEEDED, not from one that fell over "
            + "before it replaced anything");
    }

    [Fact]
    public async Task A_confirmation_that_lands_MID_read_is_still_withdrawn_by_the_read_that_replaced_it()
    {
        // The FORCE, and the only shape that can see it. ProcessDocumentAsync loads the document before
        // OCR + the LLM call and holds that tracked snapshot for the minutes the read takes; EF emits only
        // the properties whose value DIFFERS from it. The snapshot says false (nobody had confirmed at
        // claim time), so a plain `doc.IsManuallyVerified = false` produces no SET clause at all — while
        // the row has moved: PUT /verify committed true on another connection, over the values this
        // persist is about to throw away. Without SetTrust's IsModified shape the withdrawal silently
        // no-ops and the page goes right back to claiming a human confirmed machine output.
        //
        // The same interleave, one column over, is what
        // ExtractionWorkerTests.PersistSuccess_forces_its_trust_decision_over_a_write_that_landed_mid_extraction
        // pins for trust — and it is not the shape ADR 0052 Amendment 2 refuses for MarkVerified's status:
        // nothing here is a stale value being re-asserted. "No human has confirmed the fields this commit
        // leaves" is this read's own conclusion and the fresher fact.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedQueuedDocAsync(auth.OrgId);
        Extraction.Result = CleanRead(("policy_number", "POL-MACHINE"));

        var confirmedMidRead = false;
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsync($"/api/documents/{docId}/verify", content: null);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            confirmedMidRead = await GetIsManuallyVerifiedAsync(docId);
        };

        await ClaimAndProcessAsync(docId);

        confirmedMidRead.Should().BeTrue(
            "ANTI-NO-OP: the interleaved confirmation must really have committed true, or the assertion "
            + "below passes against a row nothing ever set");
        var doc = await GetDocAsync(docId);
        doc.IsManuallyVerified.Should().BeFalse(
            "the read that just landed replaced every field value, so the confirmation of the PRE-run "
            + "values cannot survive it — and it must reach the row even though it matches a snapshot the "
            + "row has moved on from (#464, the SetTrust force one column over)");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "anti-no-op: this was a clean read, so the withdrawal is not a side effect of a review flag");
    }

    private async Task<bool> GetIsManuallyVerifiedAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        return await db.Documents.AsNoTracking().Where(d => d.Id == docId)
            .Select(d => d.IsManuallyVerified).SingleAsync();
    }

    [Theory]
    // The two ways an attempt ends WITHOUT replacing a field value. Both must leave the claim standing:
    [InlineData(true)]  // MarkFailed — the non-retryable arm, terminal in one attempt
    [InlineData(false)] // RecordFailedAttempt — a counted failure that requeues
    public async Task A_read_that_replaced_no_values_leaves_the_confirmation_alone(bool nonRetryable)
    {
        // The scope of the withdrawal is the EVENT that replaces the values, not "any extraction touched
        // this row". A failed attempt writes no fields, no mirror and no typed column, so the values a
        // human confirmed are still exactly the ones on the row — clearing there would delete a claim that
        // is still true, and on the terminal arm nothing would ever put it back (ResolveManualReview is
        // the only setter, and the detail page's manual-entry affordance for a Failed document is how a
        // human gets there). Same reasoning that keeps trust out of the queue path (ADR 0052 §2).
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedQueuedDocAsync(auth.OrgId);

        Extraction.Result = CleanRead(("policy_number", "POL-CONFIRMED"));
        await ClaimAndProcessAsync(docId);
        (await auth.Client.PutAsync($"/api/documents/{docId}/verify", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await auth.Client.PostAsync($"/api/documents/{docId}/reextract", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Extraction.ThrowNonRetryable = nonRetryable;
        Extraction.ThrowOnExtract = !nonRetryable;
        await ClaimAndProcessAsync(docId);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(
            nonRetryable ? ExtractionStatus.Failed : ExtractionStatus.Pending,
            "precondition: the attempt really did end without a persist");
        doc.IsManuallyVerified.Should().BeTrue(
            "a read that replaced nothing leaves the confirmed values in place, so the claim about them is "
            + "still true — the withdrawal belongs to PersistSuccess alone (#464)");

        var detail = await ReadDetailAsync(auth.Client, docId);
        FieldValue(detail, "policy_number").Should().Be("POL-CONFIRMED",
            "and the values on the page are still the ones the person actually confirmed");
    }
}
