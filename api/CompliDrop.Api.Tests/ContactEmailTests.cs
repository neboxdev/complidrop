using System.Text.Json;
using System.Text.RegularExpressions;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Unit tests for the vendor contact-email predicate (<see cref="ContactEmail"/>), #369.
/// <para>
/// A plain class with no <c>IntegrationTestFixture</c>, mirroring <c>DisplayLabelsTests</c>: these
/// are pure assertions over a static helper, and the cross-language agreement they pin should not
/// require Docker and a per-case database reset to run. The HTTP-level theories that exercise the
/// same corpus through <c>/api/vendors</c> stay in <c>VendorEndpointsTests</c> and read the
/// <c>MemberData</c> providers below by reference.
/// </para>
/// <para>
/// The accept/reject corpus is NOT inlined here — it is loaded from the SHARED fixture
/// <c>SharedFixtures/contact-email-cases.json</c>, the same file
/// <c>frontend/src/lib/contact-email.test.ts</c> reads. The first review pass found that
/// hand-maintained parallel lists were already unequal at introduction, AND that the two
/// <c>\s</c>-based regexes genuinely disagreed on real input (.NET's <c>\s</c> includes U+0085 and
/// excludes U+FEFF; JS's is the reverse). One list makes "the two implementations agree" a
/// mechanical property instead of a comment nobody re-checks.
/// </para>
/// </summary>
public class ContactEmailTests
{
    private sealed record ContactEmailCases(
        int MaxLength,
        int[][] BlankRanges,
        CorpusMessages Messages,
        string[] Valid,
        string[] Malformed,
        PaddedCase[] PaddedValid,
        string[] Blank);

    private sealed record PaddedCase(string Raw, string Normalized);

    private sealed record CorpusMessages(string Invalid, string HiddenCharacter, string TooLong);

    private static readonly ContactEmailCases Cases = LoadContactEmailCases();

    private static ContactEmailCases LoadContactEmailCases()
    {
        // Copied next to the test assembly by the csproj <None Update="SharedFixtures\...">.
        var path = Path.Combine(AppContext.BaseDirectory, "SharedFixtures", "contact-email-cases.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Shared contact-email corpus not found at {path}. It is the single source both this " +
                "suite and frontend/src/lib/contact-email.test.ts read (#369) — do not inline the " +
                "cases here instead.", path);

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<ContactEmailCases>(File.ReadAllText(path), opts)
               ?? throw new InvalidOperationException($"Could not parse {path}");
    }

    public static TheoryData<string> MalformedEmails()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Malformed) data.Add(c);
        return data;
    }

    public static TheoryData<string> ValidEmails()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Valid) data.Add(c);
        return data;
    }

    public static TheoryData<string, string> PaddedValidEmails()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in Cases.PaddedValid) data.Add(c.Raw, c.Normalized);
        return data;
    }

    public static TheoryData<string> BlankEmails()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Blank) data.Add(c);
        return data;
    }

    /// <summary>Renders invisible code points so a failure message names the character.</summary>
    public static string Show(string s) =>
        string.Concat(s.Select(ch => ch is >= ' ' and <= '~' ? ch.ToString() : $"\\u{(int)ch:X4}"));

    [Fact]
    public void The_length_cap_matches_the_shared_corpus()
    {
        // The varchar(256) column width was the ONE rule of the mirror pair the corpus did not
        // declare: each side asserted the cap against its OWN constant, so ContactEmail.MaxLength
        // and CONTACT_EMAIL_MAX_LENGTH could drift apart with both suites green — and the client
        // would then leave Save enabled on an address the server 400s, which is exactly the
        // form-vs-API drift #369 exists to remove. Declared once, asserted on both sides.
        ContactEmail.MaxLength.Should().Be(Cases.MaxLength);
    }

    [Fact]
    public void The_shared_corpus_loaded_and_is_non_trivial()
    {
        // Guards every theory below: a silently-missing or emptied fixture would make them all
        // vacuously pass with zero cases.
        Cases.Valid.Length.Should().BeGreaterThan(3);
        Cases.Malformed.Length.Should().BeGreaterThan(10);
        Cases.PaddedValid.Length.Should().BeGreaterThan(3);
        Cases.Blank.Length.Should().BeGreaterThan(3);
    }

    [Theory]
    [MemberData(nameof(MalformedEmails))]
    public void The_predicate_rejects_every_malformed_case_in_the_shared_corpus(string bad)
    {
        // Unit-level mirror of the frontend's it.each over the SAME list — this is the assertion
        // that actually pins cross-language agreement, including the code points the two engines'
        // \s classes disagree about (U+0085, U+FEFF) and the C0 controls Postgres cannot store.
        ContactEmail.IsWellFormed(bad).Should().BeFalse($"{Show(bad)} must be rejected");
    }

    [Theory]
    [MemberData(nameof(ValidEmails))]
    public void The_predicate_accepts_every_valid_case_in_the_shared_corpus(string good)
    {
        // Includes sample-vendor@example.com (#238 seeds it — rejecting it would break the
        // one-click demo) and a non-ASCII address (the predicate must not become ASCII-only).
        ContactEmail.IsWellFormed(good).Should().BeTrue($"{Show(good)} must be accepted");
    }

    [Theory]
    [MemberData(nameof(PaddedValidEmails))]
    public void Normalization_strips_the_same_edges_the_frontend_strips(string raw, string normalized)
    {
        // The BOM/NEL rows are the ones .NET Trim() and JS .trim() disagree on. Both sides strip
        // via the shared explicit character set instead, so these must agree exactly.
        ContactEmail.Normalize(raw).Should().Be(normalized, $"{Show(raw)} normalizes to its bare address");
        ContactEmail.IsWellFormed(raw).Should().BeTrue($"{Show(raw)} is valid once stripped");
    }

    [Theory]
    [MemberData(nameof(BlankEmails))]
    public void Blank_normalizes_to_null_and_stays_valid(string blank)
    {
        // Load-bearing: a vendor with no contact email is a supported state.
        ContactEmail.Normalize(blank).Should().BeNull($"{Show(blank)} is absent, not empty string");
        ContactEmail.IsWellFormed(blank).Should().BeTrue($"{Show(blank)} must not become a 400");
    }

    // ---- the two representations of the blank class must never disagree ------------------------

    [Fact]
    public void The_blank_predicate_matches_the_shared_corpus_ranges()
    {
        // The corpus's case lists only SAMPLE the blank class, so cross-language agreement used to
        // be sampled too: a range added to ONE mirror passed both suites whenever the added code
        // points happened not to appear in those lists. `blankRanges` declares the SET, and both
        // sides walk the BMP against it — so equivalence is total, and adding a range means editing
        // the corpus AND both predicates.
        var inCorpus = new bool[0x10000];
        foreach (var range in Cases.BlankRanges)
        {
            range.Length.Should().Be(2, "each corpus range is a [lo, hi] pair");
            for (var cp = range[0]; cp <= range[1]; cp++) inCorpus[cp] = true;
        }

        var disagreements = new List<string>();
        for (var i = 0; i <= 0xFFFF; i++)
        {
            if (ContactEmail.IsBlank((char)i) != inCorpus[i])
                disagreements.Add($"U+{i:X4} (predicate={ContactEmail.IsBlank((char)i)}, corpus={inCorpus[i]})");
            if (disagreements.Count >= 10) break;
        }

        disagreements.Should().BeEmpty("the corpus owns the blank set; this side must implement exactly it");
    }

    [Fact]
    public void The_rejection_copy_matches_the_shared_corpus()
    {
        // The inline message the user reads while typing and the 400 body they get on submit are a
        // second, quieter mirror pair. Declared once in the corpus so they cannot diverge.
        ContactEmail.InvalidMessage.Should().Be(Cases.Messages.Invalid);
        ContactEmail.HiddenCharacterMessage.Should().Be(Cases.Messages.HiddenCharacter);
        ContactEmail.TooLongMessage.Should().Be(Cases.Messages.TooLong);
    }

    [Fact]
    public void An_invisible_contaminant_gets_its_own_message_not_the_generic_one()
    {
        // "Enter a valid contact email address" is unactionable when the field LOOKS correct: a
        // pasted zero-width character renders as nothing, so the user re-reads a correct-looking
        // address with no idea what is wrong. The explicit blank class is what makes this
        // reachable — neither engine's \s covers ZWSP — so the copy had to arrive with it.
        ContactEmail.DescribeProblem("ops\u200Bacme@acme.com").Should().Be(ContactEmail.HiddenCharacterMessage);
        ContactEmail.DescribeProblem("ops\u00A0acme@acme.com").Should().Be(ContactEmail.HiddenCharacterMessage);

        // A plain typo keeps the generic copy — the hidden-character wording would be a lie.
        ContactEmail.DescribeProblem("jane@acme,com").Should().Be(ContactEmail.InvalidMessage);

        // The display-name form is the sharp case: it contains SPACES, which ARE in the blank class,
        // but a space is plainly visible. Calling it a hidden character would send the user hunting
        // for something invisible in a string whose problem is right there in front of them.
        ContactEmail.DescribeProblem("Jane Smith <jane@acme.com>").Should().Be(ContactEmail.InvalidMessage);
        ContactEmail.DescribeProblem("jane doe@acme.com").Should().Be(ContactEmail.InvalidMessage);

        // Over-length is its own case: the address may be perfectly well-formed.
        ContactEmail.DescribeProblem(new string('a', 250) + "@acme.com").Should().Be(ContactEmail.TooLongMessage);

        // Acceptable values describe no problem at all.
        ContactEmail.DescribeProblem("ops@acme.com").Should().BeNull();
        ContactEmail.DescribeProblem("   ").Should().BeNull();
        ContactEmail.DescribeProblem(null).Should().BeNull();
    }

    [Fact]
    public void The_blank_predicate_and_the_character_class_agree()
    {
        // ContactEmail carries the blank set TWICE by necessity: as a regex character class (used
        // by WellFormed) and as the IsBlank predicate (used by the linear edge strip). A range
        // added to one and not the other would split the two halves of the same rule — an address
        // could be rejected as malformed while its padding was left unstripped, or vice versa.
        //
        // Asserted by construction against ContactEmail.Blank itself (internal via
        // InternalsVisibleTo) rather than a copied literal: a copy would drift along with the bug.
        var fromClass = new Regex("^[" + ContactEmail.Blank + "]$");

        var disagreements = new List<string>();
        for (var i = 0; i <= 0xFFFF; i++)
        {
            var c = (char)i;
            var byClass = fromClass.IsMatch(c.ToString());
            var byPredicate = ContactEmail.IsBlank(c);
            if (byClass != byPredicate)
                disagreements.Add($"U+{i:X4} (class={byClass}, predicate={byPredicate})");
        }

        disagreements.Should().BeEmpty(
            "the regex character class and the linear predicate are two spellings of ONE set");
    }

    // ---- normalization must stay linear in the input length ------------------------------------

    [Fact]
    public void Normalization_of_a_blank_heavy_value_completes_promptly()
    {
        // Regression guard for the confirmed super-linear blowup in the edge strip this replaced.
        //
        // The old implementation was `Regex.Replace` over `^[Blank]+|[Blank]+\z`. The second
        // alternative is unanchored at its start, so when it cannot match the engine retries at
        // every offset, and at each offset the greedy `[Blank]+` consumes the whole run before
        // `\z` fails — O(n^2), with no matchTimeout configured anywhere in the API.
        //
        // The hostile SHAPE is not the obvious one, and getting it wrong makes this test vacuous.
        // Leading/trailing padding is fast even at 10 MB (1.3 ms measured): `^[Blank]+` matches at
        // offset 0, consumes the run in ONE match, and Replace resumes past it. The pathological
        // input is blanks in the MIDDLE with a non-blank at BOTH ends, so neither alternative can
        // ever match. Measured on the real [GeneratedRegex] path:
        //
        //     n=100,000 -> 225 ms | n=200,000 -> 1,002 ms | n=400,000 -> 4,272 ms
        //
        // ~4x per doubling — clean quadratic. Extrapolated to a 10 MB body, which Kestrel accepts
        // (MaxRequestBodySize, Program.cs) and any authenticated org user can post to
        // PUT /api/vendors/{id}, that is ~45 minutes of one pegged CPU on a shared Railway
        // instance. The 256-char cap that would have bounded it ran AFTER normalization.
        //
        // 500k against a 1.5 s budget: ~6-7 s under the old regex (a decisive gap even on a fast
        // machine), while the linear scan does it in well under a millisecond.
        var hostile = "x" + new string(' ', 500_000) + "x";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var normalized = ContactEmail.Normalize(hostile);
        sw.Stop();

        normalized.Should().Be(hostile, "nothing is stripped — the blanks are interior, not edges");
        sw.ElapsedMilliseconds.Should().BeLessThan(1_500,
            "the edge strip must be linear in the input length, not quadratic");
    }

    [Fact]
    public void A_large_edge_padded_value_normalizes_to_its_bare_address()
    {
        // The companion shape: padding at both edges, which the strip must actually remove. Fast
        // under both implementations (see above) — this pins CORRECTNESS at size, not timing.
        var padded = new string(' ', 100_000) + "ops@acme.com" + new string(' ', 100_000);

        ContactEmail.Normalize(padded).Should().Be("ops@acme.com");
    }

    [Fact]
    public void An_oversized_value_is_rejected_and_yields_no_value_to_persist()
    {
        // Named for what it ASSERTS, not for the cap-before-regex ordering in TryNormalize: that
        // ordering is real and deliberate (it keeps an oversized value away from the backtracking
        // engine) but nothing here can observe it — swapping the two blocks leaves this green. The
        // ordering rationale lives on TryNormalize itself rather than in a test name that promises
        // a guarantee it does not pin.
        //
        // What this DOES pin: the 400-not-500 guard (ContactEmail is varchar(256) and Npgsql does
        // not truncate) and that a rejected value yields nothing for the caller to persist.
        var tooLong = new string('a', 250) + "@acme.com"; // 259 > 256, and otherwise well-formed

        ContactEmail.TryNormalize(tooLong, out var normalized).Should().BeFalse();
        normalized.Should().BeNull("a rejected value must not be offered to the caller to persist");
    }

    // ---- TryNormalize is the shape the endpoints use -------------------------------------------

    [Theory]
    [MemberData(nameof(PaddedValidEmails))]
    public void TryNormalize_emits_exactly_the_value_that_was_validated(string raw, string normalized)
    {
        // The endpoints write what TryNormalize emits, so "what was checked" and "what is stored"
        // are the same string by construction — the two-call IsWellFormed-then-Normalize shape
        // could drift if either ever stopped agreeing about the input.
        ContactEmail.TryNormalize(raw, out var emitted).Should().BeTrue($"{Show(raw)} is valid once stripped");
        emitted.Should().Be(normalized);
    }

    [Theory]
    [MemberData(nameof(MalformedEmails))]
    public void TryNormalize_emits_null_for_a_malformed_value(string bad)
    {
        ContactEmail.TryNormalize(bad, out var emitted).Should().BeFalse($"{Show(bad)} must be rejected");
        emitted.Should().BeNull("a caller that ignored the bool must not persist a bad address");
    }

    [Theory]
    [MemberData(nameof(BlankEmails))]
    public void TryNormalize_accepts_blank_and_emits_null(string blank)
    {
        ContactEmail.TryNormalize(blank, out var emitted).Should().BeTrue($"{Show(blank)} is a supported state");
        emitted.Should().BeNull();
    }

    [Fact]
    public void TryNormalize_accepts_a_null_address()
    {
        ContactEmail.TryNormalize(null, out var emitted).Should().BeTrue();
        emitted.Should().BeNull();
    }
}
