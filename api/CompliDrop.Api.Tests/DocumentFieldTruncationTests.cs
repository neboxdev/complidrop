using System.Text.Json;
using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests for <c>DocumentFieldTruncation.ValueWasClipped</c> (#444, ADR 0049) — the read-time
/// derivation that tells the detail page a field's value on screen is only the START of what the
/// document holds. No DB: the predicate reads two things the caller already has in memory
/// (<c>Document.ExtractionFields</c> and the <c>DocumentField</c> row).
///
/// The invariant under test is narrower than "the two copies differ". It is "this row is EXACTLY the
/// clamp of the full value" — so a legitimate divergence (a JSON null, a value nothing clipped, a row
/// that is simply a different string) must read FALSE, or the page prints a "shown shortened" warning
/// over a value that is whole.
/// </summary>
public sealed class DocumentFieldTruncationTests
{
    private const int Width = InputLengths.DocumentFieldValue;

    private static Document DocWith(string fieldName, JsonNodeShape shape) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = Guid.NewGuid(),
        OriginalFileName = "coi.pdf",
        BlobStorageUrl = "memory://x",
        ContentType = "application/pdf",
        DocumentType = "coi",
        ExtractionFields = JsonDocument.Parse(
            $"{{{JsonSerializer.Serialize(fieldName)}:{shape.Json}}}")
    };

    /// <summary>The jsonb value for the field, as the writers actually store it.</summary>
    private sealed record JsonNodeShape(string Json)
    {
        public static JsonNodeShape Text(string value) => new(JsonSerializer.Serialize(value));
        public static JsonNodeShape Null() => new("null");
    }

    private static DocumentField Row(string fieldName, string? value) => new()
    {
        Id = Guid.NewGuid(),
        DocumentId = Guid.NewGuid(),
        FieldName = fieldName,
        FieldValue = value,
        Confidence = 0.95
    };

    [Fact]
    public void A_row_holding_the_clamp_of_a_longer_value_reads_clipped()
    {
        // The headline shape: the worker wrote the FULL value into ExtractionFields (jsonb, no width)
        // and Clamp's output into the varchar(2000) row. The detail page binds its input to the row, so
        // without this flag the user is shown a clipped description_of_operations as the whole value.
        var full = new string('d', Width + 500);
        var doc = DocWith("description_of_operations", JsonNodeShape.Text(full));

        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("description_of_operations", full[..Width]))
            .Should().BeTrue();
    }

    [Fact]
    public void A_value_that_fits_the_column_reads_whole()
    {
        // The discriminating other half. Every healthy field takes this path, so a predicate that
        // over-fires here would warn on every document.
        var full = new string('d', Width);
        var doc = DocWith("description_of_operations", JsonNodeShape.Text(full));

        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("description_of_operations", full))
            .Should().BeFalse("a value at exactly the column width is stored verbatim, not clipped");
    }

    [Fact]
    public void A_row_and_a_json_value_that_AGREE_read_whole_even_when_they_used_to_differ()
    {
        // The self-clearing property that makes the read-time derivation viable without a persisted
        // column: UpdateFields writes the submitted text into BOTH copies (and REJECTS an over-length
        // one, ADR 0046), so after a save the record genuinely holds what the page shows. If this
        // returned true the warning would be permanent and would then be FALSE — the record no longer
        // carries anything longer.
        const string edited = "Named insured: Acme Catering. Additional insured: Garden Hall.";
        var doc = DocWith("description_of_operations", JsonNodeShape.Text(edited));

        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("description_of_operations", edited))
            .Should().BeFalse();
    }

    [Fact]
    public void A_row_that_is_not_the_clamp_of_the_longer_value_reads_whole()
    {
        // "They differ" is NOT the test. The worker writes one DocumentField row per extracted field
        // but only the LAST value per name into the JSON mirror, so a duplicate-name response leaves an
        // earlier row that differs from the mirror without being a clip of it. Warning there would point
        // the user at a value the record does not hold.
        var doc = DocWith("description_of_operations", JsonNodeShape.Text(new string('d', Width + 500)));

        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("description_of_operations", new string('x', Width)))
            .Should().BeFalse();
    }

    [Fact]
    public void A_clamp_that_backed_off_a_surrogate_pair_still_reads_clipped()
    {
        // ColumnClamp.To cuts at maxLength - 1 when the character there is a high surrogate, so a
        // clipped value can be 1999 characters long. A length-only test ("is the row exactly 2000?")
        // would silently stop warning on any description whose cut lands on an emoji — which is exactly
        // the astral content an OCR'd description box carries.
        var full = new string('d', Width - 1) + "\U0001F600" + "tail";
        var clamped = ColumnClamp.To(full, Width);
        clamped.Should().HaveLength(Width - 1, "the back-off is what makes this case discriminating");
        var doc = DocWith("description_of_operations", JsonNodeShape.Text(full));

        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("description_of_operations", clamped))
            .Should().BeTrue();
    }

    [Fact]
    public void A_json_null_reads_whole()
    {
        // A JSON null is an ABSENCE on both sides (ADR 0040 / DocumentFieldReadability.RawFieldValue).
        // The old GetRawText() arm returned the literal 4-character string "null"; reading it as a value
        // here would compare an absence against a row and could only mislead.
        var doc = DocWith("description_of_operations", JsonNodeShape.Null());

        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("description_of_operations", null))
            .Should().BeFalse();
        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("description_of_operations", "something"))
            .Should().BeFalse();
    }

    [Fact]
    public void A_field_the_json_mirror_does_not_carry_reads_whole()
    {
        // Manual-entry rows (the FP-064 zero-field path) and a document that never extracted have no
        // JSON mirror entry at all — nothing to be a clip OF.
        var doc = DocWith("description_of_operations", JsonNodeShape.Text(new string('d', Width + 500)));

        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("expiration_date", new string('d', Width)))
            .Should().BeFalse();
    }

    [Fact]
    public void A_document_with_no_extraction_fields_reads_whole()
    {
        var doc = DocWith("description_of_operations", JsonNodeShape.Text("x"));
        doc.ExtractionFields = null;

        DocumentFieldTruncation
            .ValueWasClipped(doc, Row("description_of_operations", new string('d', Width)))
            .Should().BeFalse();
    }

    [Fact]
    public void The_derivation_reads_the_SAME_width_the_extraction_worker_clamps_to()
    {
        // The predicate reproduces the clamp rather than guessing at it, so both sides must be the one
        // constant. If a future change widened the column and moved only the worker, the derivation
        // would keep flagging every field at the old width — a permanent false warning on healthy
        // documents. ExtractionWorker.FieldValueMaxLength is itself an alias of InputLengths (ADR 0046),
        // and this pins that it stays one number.
        ExtractionWorker.FieldValueMaxLength.Should().Be(InputLengths.DocumentFieldValue);
    }
}
