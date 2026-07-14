using System.Security.Cryptography;
using System.Text;
using CompliDrop.Api.Services.Extraction;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Tripwire coupling <see cref="ExtractionPrompts.Version"/> to the prompt content (#272
/// review). Every document records ExtractionPromptVersion as its audit-trail provenance;
/// before this test, bumping the version on a prompt edit was manual discipline, and a
/// forgotten bump would stamp materially different extractions with the same version —
/// making documents extracted under different prompts indistinguishable.
/// </summary>
public sealed class ExtractionPromptVersionTests
{
    // ON ANY PROMPT EDIT: update BOTH constants together —
    //   1. bump ExtractionPrompts.Version (new date + slug),
    //   2. re-pin this hash (the test failure message prints the new value).
    // Updating the hash without bumping the version defeats the audit trail this
    // tripwire exists to protect.
    private const string PinnedVersion = "v2-2026-07-13-gl-each-occurrence";
    private const string PinnedPromptSha256 = "311C6D71FE3DC8179B06ECF50768894127E0D845A72C9DE7660F847079F0AC1A";

    [Fact]
    public void Prompt_content_and_version_are_pinned_together()
    {
        // Line endings are normalized before hashing: C# raw string literals preserve the
        // SOURCE file's endings, which differ between a Windows checkout (CRLF) and CI
        // (LF) — the hash must be platform-stable.
        var normalized = ExtractionPrompts.SystemPrompt.ReplaceLineEndings("\n");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

        ExtractionPrompts.Version.Should().Be(PinnedVersion,
            "a prompt edit must consciously bump the version (see the comment on the pinned constants)");
        hash.Should().Be(PinnedPromptSha256,
            $"the prompt content changed — bump ExtractionPrompts.Version AND re-pin this hash to {hash}");
    }

    [Fact]
    public void Prompt_pins_general_liability_to_the_each_occurrence_cell()
    {
        // #397: general_liability_limit must be read from the ACORD 25 "EACH OCCURRENCE" cell, never the
        // General Aggregate — a $2M aggregate over a $500k/occ policy is the review's #1 fail-open, so the
        // prompt has to say so explicitly. Pin the instruction (a dropped bullet regresses the extraction).
        ExtractionPrompts.SystemPrompt.Should().Contain("EACH OCCURRENCE",
            "the prompt must pin general_liability_limit to the ACORD per-occurrence cell (#397)");
        ExtractionPrompts.SystemPrompt.Should().Contain("GENERAL AGGREGATE",
            "the prompt must name the aggregate as the value NOT to read (#397)");

        // The version bump on any prompt edit is enforced durably by Prompt_content_and_version_are_pinned_together
        // (Version.Should().Be(PinnedVersion) + the content hash) — a single current-slug pin that catches EVERY
        // forgotten bump, not just one historical literal. This test's job is the EACH OCCURRENCE / GENERAL
        // AGGREGATE content pin above; a non-durable NotBe("<one old slug>") added nothing and is dropped (#416).
    }
}
