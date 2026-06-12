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
    private const string PinnedVersion = "v2-2026-06-12-eo-addl-insured";
    private const string PinnedPromptSha256 = "3ED6C1A5E646F522F8A1761B32BB7B56F448745096ADD43AF18E2B4442C48F46";

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
}
