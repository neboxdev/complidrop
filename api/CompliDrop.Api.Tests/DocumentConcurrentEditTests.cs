using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Endpoints;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Regression tests for #366 (ADR 0030 Amendment 1): the two PARTIAL document writers —
/// <c>DocumentEndpoints.UpdateFields</c> and <c>UpdateDocument</c> — must never commit a row whose
/// canonical inputs contradict each other or whose verdict was graded against inputs the row no longer
/// holds.
/// <para/>
/// ADR 0030 already made each writer commit its inputs and the verdict they imply in ONE transaction.
/// That is not enough for these two, because each rebuilds the WHOLE <c>ExtractionFields</c> JSON mirror
/// from its own read snapshot while EF writes back only the columns IT modified. Two overlapping edits
/// could therefore leave the typed <c>GeneralLiabilityLimit</c> holding writer A's value, the JSON
/// mirror holding the pre-A value, and <c>ComplianceStatus</c> matching neither — or leave the row on
/// the NEW vendor with a verdict graded against the OLD vendor's checklist.
/// <para/>
/// Every interleave here is CONSTRUCTED, not raced: the competing writer is driven to completion from
/// inside the request-under-test's <c>SaveChanges</c> hook (see
/// <see cref="ConcurrentDocumentWriteInterceptor"/>), so "the other writer committed between my snapshot
/// and my UPDATE" happens on every run rather than on a lucky one.
/// </summary>
public sealed class DocumentConcurrentEditTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private ConcurrentDocumentWriteInterceptor Interleave =>
        Fixture.Factory.Services.GetRequiredService<ConcurrentDocumentWriteInterceptor>();

    private CommitFaultInterceptor CommitFault =>
        Fixture.Factory.Services.GetRequiredService<CommitFaultInterceptor>();

    public override async Task DisposeAsync()
    {
        // Belt and braces with the fixture reset: a callback surviving a failed assertion would fire
        // another test's competing write — or fail its commit.
        Interleave.Reset();
        CommitFault.Reset();
        await base.DisposeAsync();
    }

    // A checklist carrying a single "general_liability_limit >= minLimit" rule for COIs, plus a vendor on it.
    private async Task<(Guid VendorId, Guid TemplateId)> SeedVendorWithGlRuleAsync(
        Guid orgId, string minLimit, string vendorName = "V")
    {
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate
        {
            Id = templateId, OrganizationId = orgId, Name = $"T-{templateId:N}", CreatedAt = now
        });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = vendorName, ComplianceTemplateId = templateId,
            CreatedAt = now, UpdatedAt = now
        });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = Guid.NewGuid(), ComplianceTemplateId = templateId, DocumentType = "coi",
            FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = minLimit, SortOrder = 0
        });
        await db.SaveChangesAsync();
        return (vendorId, templateId);
    }

    // A COI carrying BOTH canonical inputs this suite moves: the typed/JSON general_liability_limit (a
    // verdict input) and certificate_holder (not a verdict input under these checklists — which is the
    // point: editing it must still not be able to corrupt the limit).
    private async Task<Guid> SeedCoiAsync(Guid orgId, Guid? vendorId, decimal gl, string holder)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = "coi.pdf",
            BlobStorageUrl = "blob://d",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Completed,
            ComplianceStatus = ComplianceStatus.Pending,
            GeneralLiabilityLimit = gl,
            ExtractionFields = JsonDocument.Parse(
                $$"""{"general_liability_limit":"{{gl:0}}","certificate_holder":"{{holder}}"}"""),
            // Far future: the verdict is purely rule-driven, never date-driven.
            ExpirationDate = now.AddYears(1),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.DocumentFields.Add(new DocumentField
        {
            Id = Guid.NewGuid(), DocumentId = docId, FieldName = "general_liability_limit",
            FieldValue = $"{gl:0}", FieldType = "text", Confidence = 0.95
        });
        db.DocumentFields.Add(new DocumentField
        {
            Id = Guid.NewGuid(), DocumentId = docId, FieldName = "certificate_holder",
            FieldValue = holder, FieldType = "text", Confidence = 0.95
        });
        await db.SaveChangesAsync();
        return docId;
    }

    private sealed record TerminalState(Document Doc, string? MirrorGl, string? MirrorHolder, string? FieldRowGl);

    /// <summary>
    /// Reads the row back with everything the verdict was computed from — including the vendor chain the
    /// row ACTUALLY ends up pointing at — and asserts the single invariant this ticket is about: the
    /// persisted verdict is exactly what the persisted inputs grade to, and the two copies of every
    /// canonical input agree. A torn pair fails this whichever way it tore.
    /// </summary>
    private async Task<TerminalState> ReadTerminalStateAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        var doc = await db.Documents
            .Include(d => d.Fields)
            .Include(d => d.Vendor)
                .ThenInclude(v => v!.ComplianceTemplate)
                    .ThenInclude(t => t!.Rules)
            .FirstAsync(d => d.Id == docId);

        var root = doc.ExtractionFields!.RootElement;
        var mirrorGl = root.TryGetProperty("general_liability_limit", out var g) ? g.GetString() : null;
        var mirrorHolder = root.TryGetProperty("certificate_holder", out var h) ? h.GetString() : null;

        // The typed column and the JSON mirror are two copies of ONE canonical input. #366 is exactly the
        // state where they disagree, so assert them equal before anything else.
        decimal.Parse(mirrorGl!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(doc.GeneralLiabilityLimit,
                "the typed column and the JSON mirror are two copies of the same canonical input and must never disagree");

        // And the stored verdict must be the one those inputs imply against the vendor the row actually
        // holds — recomputed here from the persisted row, so a verdict graded against the OTHER vendor's
        // checklist (failure scenario B) fails even though the value would look plausible on its own.
        var recomputed = ComplianceCheckService.ComputeOutcome(doc, DateTime.UtcNow).Status;
        doc.ComplianceStatus.Should().Be(recomputed,
            "the persisted verdict must be what the persisted inputs grade to against the persisted vendor");

        return new TerminalState(
            doc, mirrorGl, mirrorHolder,
            doc.Fields.FirstOrDefault(f => f.FieldName == "general_liability_limit")?.FieldValue);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> EditFieldAsync(HttpClient client, Guid docId, string name, string value) =>
        SendAsync(client, HttpMethod.Put, $"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = name, fieldValue = value } }
        });

    [Fact]
    public async Task Two_overlapping_field_edits_leave_one_consistent_inputs_and_verdict_tuple()
    {
        // FAILURE SCENARIO A. Two edits to DIFFERENT fields overlap. The one that commits LAST rebuilt the
        // whole ExtractionFields JSON from a snapshot taken BEFORE the other committed, but EF writes back
        // only its own typed columns — so pre-#366 the terminal row held the winner's typed
        // GeneralLiabilityLimit (3M), a JSON mirror still saying 1M, and a verdict computed from 1M
        // (NonCompliant). That is a Compliant-vs-deficient split of the two canonical copies, and the
        // sweep never re-runs rule evaluation, so it persisted until someone re-graded by hand.
        var auth = await RegisterAndLoginAsync();
        var (vendorId, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000");
        var docId = await SeedCoiAsync(auth.OrgId, vendorId, gl: 1_000_000m, holder: "Old Hall");
        var second = await LoginAsync(auth.Email);

        var competingWrites = 0;
        Interleave.OnSavingChanges = async () =>
        {
            // Fire ONCE: the retry must be allowed to succeed, and one conflict is what this scenario is.
            if (Interlocked.Increment(ref competingWrites) > 1) return;
            // The competing edit raises the GL limit above the 2M floor — a verdict-MOVING correction, so
            // dropping it does not merely lose a value, it re-certifies the document from the old one.
            var resp = await EditFieldAsync(second.Client, docId, "general_liability_limit", "3000000");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        // The request under test edits a DIFFERENT field, so nothing about it is "about" the GL limit —
        // it only rebuilds the JSON blob that happens to carry it.
        var response = await EditFieldAsync(auth.Client, docId, "certificate_holder", "New Hall");
        Interleave.Reset();

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the conflicted attempt is reloaded and recomputed, not surfaced to the user");
        competingWrites.Should().Be(2, "the retry re-entered the save hook, proving the write really was re-run");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.GeneralLiabilityLimit.Should().Be(3_000_000m, "the competing correction must not be rolled back");
        terminal.MirrorGl.Should().Be("3000000", "the JSON mirror was rebuilt from the RELOADED row, not the stale snapshot");
        terminal.FieldRowGl.Should().Be("3000000");
        terminal.MirrorHolder.Should().Be("New Hall", "and this request's own edit still landed");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "3M clears the 2M floor — the pre-#366 terminal row said NonCompliant beside a compliant limit");

        // The re-runnability invariant, in the one place it is observable from OUTSIDE the row: the
        // audit payload must come from the attempt that COMMITTED. UpdateFields captures `before`
        // inside the retryable callback precisely so the retried attempt describes the state it
        // actually wrote — the row as it stood AFTER the competing 1M -> 3M correction landed.
        //
        // Nothing else here can see the difference. Capture `before` once (outside the callback, or
        // `before ??=` so only the first attempt sets it) and every assertion above stays green while
        // this document's history claims THIS user's certificate_holder edit found a 1M limit and left
        // it at 3M — the other user's correction, attributed to the wrong edit, in the artifact the
        // product sells as the audit trail.
        await using var auditDb = CreateSystemDb();
        var edits = await auditDb.AuditLogs
            .Where(a => a.EntityId == docId && a.Action == "document.fields_edited")
            .ToListAsync();
        // Two: the competing edit is a real request through the same host, so it audits itself too.
        edits.Should().HaveCount(2);
        var underTest = edits.Single(a => AuditFields(a.AfterJson).Any(f => f.FieldValue == "New Hall"));

        AuditFields(underTest.BeforeJson)
            .Single(f => f.FieldName == "general_liability_limit").FieldValue
            .Should().Be("3000000",
                "the committing attempt re-read the row after the competing correction, so its before-state is 3M — "
                + "1000000 here would be the snapshot the abandoned first attempt lost with");
        AuditFields(underTest.BeforeJson)
            .Single(f => f.FieldName == "certificate_holder").FieldValue
            .Should().Be("Old Hall", "and this request's own field is unchanged in its before-state");
    }

    /// <summary>One field's name/value as a <c>document.fields_edited</c> payload records it.</summary>
    private sealed record AuditFieldRow(string FieldName, string? FieldValue);

    private static IReadOnlyList<AuditFieldRow> AuditFields(string? json) =>
        JsonSerializer.Deserialize<List<AuditFieldRow>>(
            json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public async Task A_field_edit_racing_a_vendor_reassignment_is_graded_against_the_vendor_the_row_keeps()
    {
        // FAILURE SCENARIO B, the sharper one. UpdateDocument moves the document to a vendor whose
        // checklist it PASSES while UpdateFields is mid-flight. UpdateFields never writes VendorId, so
        // pre-#366 the row kept the new vendor while ComplianceStatus was the verdict of the OLD vendor's
        // checklist — a wrong persisted compliance verdict, which is what this product sells.
        var auth = await RegisterAndLoginAsync();
        var (strictVendor, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000", vendorName: "Strict");
        var (lenientVendor, lenientTemplate) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "500000", vendorName: "Lenient");
        var docId = await SeedCoiAsync(auth.OrgId, strictVendor, gl: 1_000_000m, holder: "Old Hall");
        var second = await LoginAsync(auth.Email);

        var competingWrites = 0;
        Interleave.OnSavingChanges = async () =>
        {
            if (Interlocked.Increment(ref competingWrites) > 1) return;
            var resp = await SendAsync(second.Client, HttpMethod.Patch, $"/api/documents/{docId}",
                new { vendorId = lenientVendor });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        var response = await EditFieldAsync(auth.Client, docId, "certificate_holder", "New Hall");
        Interleave.Reset();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.VendorId.Should().Be(lenientVendor, "the reassignment committed and this request never writes VendorId");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "1M clears the LENIENT vendor's 500k floor — pre-#366 this row kept the lenient vendor with the strict vendor's NonCompliant verdict");

        // And the explainer rows agree: they were written against the lenient vendor's checklist, so the
        // "what we checked" panel cannot show the previous vendor's requirements beside the new vendor.
        await using var db = CreateSystemDb();
        var checkedTemplates = await db.ComplianceChecks
            .Where(c => c.DocumentId == docId)
            .Select(c => c.ComplianceRule.ComplianceTemplateId)
            .Distinct()
            .ToListAsync();
        checkedTemplates.Should().Equal([lenientTemplate],
            "the committed verdict must be graded against the checklist of the vendor the row actually ends up with");
    }

    [Fact]
    public async Task A_vendor_reassignment_racing_a_field_edit_is_graded_against_the_inputs_the_row_keeps()
    {
        // Failure scenario B with the two writers swapped, so UpdateDocument's OWN retry is what is under
        // test. The field edit drops the limit under BOTH floors while the reassignment is mid-flight;
        // pre-#366 the row kept the lowered limit with a verdict computed from the pre-edit one.
        var auth = await RegisterAndLoginAsync();
        var (strictVendor, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000", vendorName: "Strict");
        var (lenientVendor, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "500000", vendorName: "Lenient");
        var docId = await SeedCoiAsync(auth.OrgId, strictVendor, gl: 1_000_000m, holder: "Old Hall");
        var second = await LoginAsync(auth.Email);

        var competingWrites = 0;
        Interleave.OnSavingChanges = async () =>
        {
            if (Interlocked.Increment(ref competingWrites) > 1) return;
            var resp = await EditFieldAsync(second.Client, docId, "general_liability_limit", "100000");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        var response = await SendAsync(auth.Client, HttpMethod.Patch, $"/api/documents/{docId}",
            new { vendorId = lenientVendor });
        Interleave.Reset();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.VendorId.Should().Be(lenientVendor);
        terminal.Doc.GeneralLiabilityLimit.Should().Be(100_000m, "the competing field edit must not be rolled back");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "100k fails even the lenient 500k floor — pre-#366 this row read Compliant beside a deficient limit");
    }

    [Fact]
    public async Task A_write_that_keeps_losing_commits_nothing_and_says_so()
    {
        // Retry exhaustion. A competing write commits inside EVERY attempt, so no attempt can ever win.
        // The bounded loop must then answer 409 having committed NOTHING — never a half-applied edit, and
        // never a confident verdict computed from inputs the row no longer holds. The terminal row is the
        // last competing writer's own consistent tuple, untouched by this request.
        var auth = await RegisterAndLoginAsync();
        var (vendorId, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000");
        var docId = await SeedCoiAsync(auth.OrgId, vendorId, gl: 1_000_000m, holder: "Old Hall");
        var second = await LoginAsync(auth.Email);

        var competingWrites = 0;
        Interleave.OnSavingChanges = async () =>
        {
            // A DIFFERENT value each time, so the last one is identifiable and every attempt genuinely
            // finds a newer row version than the one it read.
            var attempt = Interlocked.Increment(ref competingWrites);
            var resp = await EditFieldAsync(second.Client, docId, "general_liability_limit", $"{3_000_000 + attempt}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        var response = await EditFieldAsync(auth.Client, docId, "certificate_holder", "New Hall");
        Interleave.Reset();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be(DocumentWriteConcurrency.ConflictCode);
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        competingWrites.Should().Be(DocumentConcurrency.MaxAttempts,
            "the write is retried a BOUNDED number of times — an unbounded loop would spin here forever");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.MirrorHolder.Should().Be("Old Hall", "nothing of the abandoned edit may be committed");
        terminal.Doc.GeneralLiabilityLimit.Should().Be(3_000_000m + DocumentConcurrency.MaxAttempts,
            "the row is the last competing writer's own consistent tuple");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant);

        // And no audit row claims an edit that never happened — in EITHER shape. The explicit
        // "document.fields_edited" row is written after the commit, so the abandoned request never
        // reaches it; the interceptor's "document.updated" row is staged INSIDE the failing SaveChanges
        // on the same context, so it has to roll back with the attempt. Exactly one of each per
        // competing write, and nothing from the three abandoned attempts.
        await using var db = CreateSystemDb();
        var rows = await db.AuditLogs
            .Where(a => a.EntityId == docId)
            .GroupBy(a => a.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync();
        rows.Should().BeEquivalentTo(new[]
        {
            new { Action = "document.fields_edited", Count = DocumentConcurrency.MaxAttempts },
            new { Action = "document.updated", Count = DocumentConcurrency.MaxAttempts },
        }, "only the competing writes are audited — an abandoned attempt's staged rows roll back with it");
    }

    [Fact]
    public async Task An_unrelated_document_writer_still_wins_last_without_conflicting()
    {
        // The floor: #366 is scoped to the two PARTIAL writers that co-write a verdict. Every other
        // document write path keeps READ COMMITTED last-writer-wins — which is what keeps this clear of
        // ADR 0030 § Option A, whose entity-wide token would have made ALL of them optimistic (including
        // the extraction worker, where an exception out of SaveChanges costs a re-paid OCR + LLM run).
        // MarkVerified is such a path: a partial writer that touches no verdict, so it cannot tear one.
        var auth = await RegisterAndLoginAsync();
        var (vendorId, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000");
        var docId = await SeedCoiAsync(auth.OrgId, vendorId, gl: 1_000_000m, holder: "Old Hall");
        var second = await LoginAsync(auth.Email);

        var competingWrites = 0;
        Interleave.OnSavingChanges = async () =>
        {
            if (Interlocked.Increment(ref competingWrites) > 1) return;
            var resp = await EditFieldAsync(second.Client, docId, "general_liability_limit", "3000000");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        var response = await SendAsync(auth.Client, HttpMethod.Put, $"/api/documents/{docId}/verify");
        Interleave.Reset();

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an unrelated writer must not start answering 409 — it never became optimistic");
        competingWrites.Should().Be(1, "and it must not retry: there is nothing here to recompute");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.IsManuallyVerified.Should().BeTrue("this request's own column landed");
        terminal.Doc.GeneralLiabilityLimit.Should().Be(3_000_000m,
            "and the competing edit's columns survived alongside it — unchanged last-writer-wins semantics");
    }

    /// <summary>
    /// The competing write in the shape <c>ExtractionWorker.PersistSuccess</c> commits it, minus the OCR
    /// and the LLM: ONE unguarded, plain <c>READ COMMITTED</c> unit of work that rewrites the WHOLE tuple
    /// — every key of the <c>ExtractionFields</c> mirror, the typed columns it implies, the
    /// <c>DocumentField</c> rows (removed and re-inserted, never updated in place) and the verdict.
    /// </summary>
    private async Task WriteWholeTupleLikeTheExtractionWorkerAsync(Guid docId, string gl, string holder)
    {
        await using var db = CreateSystemDb();
        var doc = await db.Documents
            .Include(d => d.Fields)
            .Include(d => d.Vendor)
                .ThenInclude(v => v!.ComplianceTemplate)
                    .ThenInclude(t => t!.Rules)
            .FirstAsync(d => d.Id == docId);

        doc.ExtractionFields = JsonDocument.Parse(
            $$"""{"general_liability_limit":"{{gl}}","certificate_holder":"{{holder}}"}""");
        CanonicalDocumentFields.ApplyToTypedColumn(doc, "general_liability_limit", gl);

        // ToList first: marking these Deleted fixes up doc.Fields, which is the collection being walked.
        db.DocumentFields.RemoveRange(doc.Fields.ToList());
        foreach (var (name, value) in new[] { ("general_liability_limit", gl), ("certificate_holder", holder) })
            db.DocumentFields.Add(new DocumentField
            {
                Id = Guid.NewGuid(), DocumentId = docId, FieldName = name, FieldValue = value,
                FieldType = "text", Confidence = 0.95
            });

        // Combined unit of work (ADR 0030): the verdict rides the inputs' own SaveChanges, so this
        // competitor commits a consistent tuple of its own — it is a faithful competitor, not a torn one.
        var outcome = ComplianceCheckService.ComputeOutcome(doc, DateTime.UtcNow);
        if (outcome.ClearExistingChecks)
            db.ComplianceChecks.RemoveRange(
                await db.ComplianceChecks.Where(c => c.DocumentId == docId).ToListAsync());
        if (outcome.NewChecks.Count > 0) db.ComplianceChecks.AddRange(outcome.NewChecks);
        doc.ComplianceStatus = outcome.Status;
        doc.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_field_edit_conflicts_with_an_UNGUARDED_whole_tuple_write_and_still_retries()
    {
        // The SECOND realistic population DocumentConcurrency names: "an edit landing on top of a
        // re-extraction". Every other interleave in this suite drives a GUARDED writer as the
        // competitor, so none of them shows the 40001 firing against one that never opted in.
        // ExtractionWorker.PersistSuccess is that competitor — it keeps READ COMMITTED
        // last-writer-wins BY DECISION (ADR 0030 Amendment 1: an entity-level token there costs a
        // re-paid OCR + LLM run) and it writes the whole tuple in one go, for every input it EXTRACTS.
        // The double below is faithful to THAT: it writes the inputs it changes plus the verdict they
        // imply, in one SaveChanges. It does not model the worker's own stale-basis grading (anything
        // EF sees unmodified on its minutes-old snapshot used to be graded from the pre-run value and is
        // never written back — #460, since closed by ADR 0030 Amendment 2 and covered by
        // ExtractionWorkerStaleBasisTests), which is a different interleave and out of scope for this
        // suite. The double is deliberately NOT updated to match the real worker's grading basis: what it
        // exists to be is an UNGUARDED whole-tuple competitor, and how that competitor picks its inputs
        // changes nothing about the 40001 this test is watching for.
        //
        // Its DocumentField handling is a hazard of its own. It DELETES every row and re-inserts, so
        // the row the guarded writer loaded and is about to UPDATE by Id no longer exists. Under READ
        // COMMITTED that UPDATE matches zero rows and EF raises DbUpdateConcurrencyException — a 500
        // over an ordinary re-extraction. Under REPEATABLE READ Postgres refuses it as a
        // serialization failure instead, which is a signal the guard knows what to do with.
        var auth = await RegisterAndLoginAsync();
        var (vendorId, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000");
        var docId = await SeedCoiAsync(auth.OrgId, vendorId, gl: 1_000_000m, holder: "Old Hall");

        var competingWrites = 0;
        Interleave.OnSavingChanges = async () =>
        {
            if (Interlocked.Increment(ref competingWrites) > 1) return;
            await WriteWholeTupleLikeTheExtractionWorkerAsync(docId, gl: "3000000", holder: "Old Hall");
        };

        var response = await EditFieldAsync(auth.Client, docId, "certificate_holder", "New Hall");
        Interleave.Reset();

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a re-extraction committing inside the window is a conflict to re-run, not a 500 and not a lost edit");
        competingWrites.Should().Be(2, "the retry re-entered the save hook, proving the write really was re-run");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.GeneralLiabilityLimit.Should().Be(3_000_000m, "the re-extraction's values must not be rolled back");
        terminal.MirrorGl.Should().Be("3000000", "the mirror was rebuilt from the RELOADED row, not the stale snapshot");
        terminal.FieldRowGl.Should().Be("3000000");
        terminal.MirrorHolder.Should().Be("New Hall", "and this request's own edit still landed");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "3M clears the 2M floor, and the verdict is the RETRIED attempt's — computed from what the row now holds");

        // The retried attempt edited the row the competitor INSERTED: it neither resurrected the one it
        // had loaded nor added a third row beside the new one.
        terminal.Doc.Fields.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_attempt_that_loses_at_COMMIT_leaves_no_audit_row_behind_it()
    {
        // "Did this request commit a field edit?" must be a fact about the attempt that ANSWERED, and
        // the endpoint has to make it one itself. Today REPEATABLE READ reports first-updater-wins at
        // the UPDATE statement, so a conflicted attempt never reaches the line that records it — but
        // that is a property of the isolation level, not of the endpoint. SERIALIZABLE reports at
        // COMMIT (SSI), and so would any future writer taking an explicit row lock.
        //
        // So: fail attempt 1 at its COMMIT, and make the document vanish before attempt 2 reads. The
        // request then answers 404 having committed nothing — and must audit nothing. With the
        // per-attempt reset removed, attempt 1's rolled-back write is still sitting in the outer
        // variable when the 404 returns, and the customer's audit export gains a "fields edited" row
        // for an edit that never happened, against a document that no longer exists.
        var auth = await RegisterAndLoginAsync();
        var (vendorId, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000");
        var docId = await SeedCoiAsync(auth.OrgId, vendorId, gl: 1_000_000m, holder: "Old Hall");

        var rollbacks = 0;
        CommitFault.OnCommitting = ordinal => Task.FromResult(ordinal == 1);
        CommitFault.OnRolledBack = async () =>
        {
            // AFTER the rollback, never inside the commit hook: the faulted attempt still holds the
            // document row's UPDATE lock until it rolls back, so a delete from another connection
            // would block on a lock only this callback's own return could release.
            if (Interlocked.Increment(ref rollbacks) > 1) return;
            await using var db = CreateSystemDb();
            var doc = await db.Documents.FirstAsync(d => d.Id == docId);
            db.Documents.Remove(doc); // interceptor translates to the soft delete AppDbContext filters out
            await db.SaveChangesAsync();
        };

        var response = await EditFieldAsync(auth.Client, docId, "certificate_holder", "New Hall");
        CommitFault.Reset();

        rollbacks.Should().Be(1, "attempt 1 must have been rolled back — otherwise nothing below is under test");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the retry re-reads, and the document is gone");

        await using var db = CreateSystemDb();
        var edits = await db.AuditLogs
            .CountAsync(a => a.EntityId == docId && a.Action == "document.fields_edited");
        edits.Should().Be(0, "no attempt committed a field edit, so none may be audited as one");

        var doc = await db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == docId);
        doc.ExtractionFields!.RootElement.GetProperty("certificate_holder").GetString()
            .Should().Be("Old Hall", "and the rolled-back attempt left nothing of itself in the row either");
    }

    [Fact]
    public async Task The_attempt_that_loses_at_the_LAST_commit_leaves_no_audit_row_behind_it()
    {
        // The sibling above proves the fact is cleared BETWEEN attempts. This proves it is also cleared
        // when there is no next attempt to clear it — the case a per-attempt-ENTRY reset structurally
        // cannot reach. RunAsync wraps CommitAsync in the SAME conflict catch as the write, so the
        // FINAL attempt can run all the way through "this attempt wrote a field edit" and still be
        // abandoned at its commit; the loop then falls straight out to the 409.
        //
        // What that used to produce is the sharpest shape of the bug this suite exists for: a response
        // that says "nothing was committed" beside a `document.fields_edited` audit row carrying the
        // rolled-back attempt's values — and IAuditLogger writes on its own SystemDbContext connection,
        // so the rollback cannot reach that row. The customer's audit export then records an edit that
        // never happened, against a document still holding the old value.
        var auth = await RegisterAndLoginAsync();
        var (vendorId, _) = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000");
        var docId = await SeedCoiAsync(auth.OrgId, vendorId, gl: 1_000_000m, holder: "Old Hall");

        // EVERY commit fails, so every attempt — the last one included — reaches the line that records
        // the edit and then loses. No competing writer and no delete hook: the only thing under test is
        // what the request RECORDS about a write it did not commit.
        var commits = 0;
        CommitFault.OnCommitting = ordinal =>
        {
            Interlocked.Exchange(ref commits, ordinal);
            return Task.FromResult(true);
        };

        var response = await EditFieldAsync(auth.Client, docId, "certificate_holder", "New Hall");
        CommitFault.Reset();

        commits.Should().Be(DocumentConcurrency.MaxAttempts,
            "every attempt must have reached its COMMIT — otherwise the terminal one is not under test");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be(DocumentWriteConcurrency.ConflictCode);

        await using var db = CreateSystemDb();
        var rows = await db.AuditLogs.Where(a => a.EntityId == docId).ToListAsync();
        rows.Should().BeEmpty(
            "the 409 states that nothing was committed, so no audit row may claim otherwise — in either shape");

        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionFields!.RootElement.GetProperty("certificate_holder").GetString()
            .Should().Be("Old Hall", "and no abandoned attempt left anything of itself in the row");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Pending,
            "no attempt's recomputed verdict was committed either");
    }
}
