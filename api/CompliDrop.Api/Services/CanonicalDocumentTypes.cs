using System.Text.Json.Nodes;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// The canonical <see cref="Document.DocumentType"/> vocabulary — ONE source of truth for every place
/// that has to answer "is this a document type we understand?" (#373, ADR 0045
/// <c>docs/adr/0045-canonical-document-type-vocabulary.md</c>). Companion to
/// <see cref="CanonicalDocumentFields"/>, which does the same job for extracted FIELD names.
/// <para/>
/// The vocabulary is load-bearing, not cosmetic: <c>Document.DocumentType</c> is compared with ORDINAL
/// equality on two compliance-critical paths, so a value outside this set (or merely mis-cased) is not a
/// cosmetic wart but a silent behavior change:
/// <list type="bullet">
///   <item><description><c>ComplianceCheckService.ComputeOutcome</c>'s <c>applicableRules</c> filter
///     (<c>r.DocumentType == doc.DocumentType</c>) — a <c>"COI"</c> matches zero <c>"coi"</c> rules, so
///     the checklist yields zero applicable rules and the document is NEVER GRADED against
///     anything.</description></item>
///   <item><description><see cref="DocumentSupersession"/>, which groups on
///     <c>(VendorId, DocumentType)</c> — a <c>"COI"</c> renewal never supersedes the <c>"coi"</c> cert it
///     replaces, so the old expired copy keeps inflating the Expired liability and keeps drawing
///     reminders (ADR 0033).</description></item>
/// </list>
/// <para/>
/// Never-graded is not a fail-safe silence. It USED to be an affirmative-coverage OVERCLAIM whenever
/// the document expired within 30 days: a document no rule ever touched read "Expiring soon" on the
/// list, rolled its vendor up to "Covered", and printed "Expiring soon" into the auditor-facing vendor
/// package — over an empty "What we checked" panel.
/// <see href="https://github.com/neboxdev/complidrop/issues/443">#443</see> /
/// <see href="../../../docs/adr/0047-never-graded-document-asserts-no-affirmative-verdict.md">ADR 0047</see>
/// CLOSED that: such a document now reads <c>Pending</c> on every read surface, so the residue is
/// VISIBLE rather than silent. The SUPERSESSION half of a mis-cased type survives that fix untouched —
/// a <c>"COI"</c> renewal still never supersedes the <c>"coi"</c> cert it replaces — and a demoted
/// document still asserts nothing until a human re-types it, so this class still matters: it stops NEW
/// documents from joining that population.
/// <para/>
/// Callers: <c>ExtractionWorker.PersistSuccess</c> (coerces the model's answer before it overwrites the
/// stored type), the two INGRESS upload paths — <c>DocumentEndpoints.UploadDocument</c> and the PUBLIC
/// <c>VendorPortalEndpoints.UploadViaPortal</c>, which also coerce (#389) — <c>UpdateDocument</c> and
/// <c>ComplianceEndpoints.UpsertRule</c> (the OTHER operand of that ordinal comparison — both REJECT an
/// unknown type with a 400 rather than coercing, because a human deliberately re-typing a document or
/// writing a RULE is choosing what gets graded), and both extraction clients' structured-output schemas,
/// which pin <c>documentType</c> to this exact list via <see cref="SchemaEnum"/> so the Gemini and
/// Anthropic contracts cannot drift apart.
/// <para/>
/// Mirrors pinned equal to <see cref="All"/> by <c>CanonicalDocumentTypeTests</c>: both provider schemas,
/// the extraction prompt's DOCUMENT TYPES block, and <see cref="DisplayLabels"/>' label map (which
/// renders the type on the audit export). <c>DocumentEndpoints</c>' second literal is GONE — #389
/// collapsed it (ADR 0046 §7, the collapse ADR 0045 § "Option E" deferred), so those endpoints call this
/// class directly and their contract is asserted over HTTP in <c>RequestInputLengthTests</c> instead. One
/// mirror a .NET test cannot reach stays unpinned and is named in <c>.claude/reviewers.md</c> + ADR 0045:
/// <c>frontend/src/lib/document-types.ts</c>.
/// <para/>
/// NOT a mirror: <c>RuleEngine/RuleSetLoader</c>'s private <c>DocumentTypes</c> set
/// (<c>coi | license | certification | other</c>) is a deliberate RD-c SUBSET — the obligation schema
/// accepts fewer types than the product stores — and must NOT be pinned equal to <see cref="All"/>.
/// Adding a seventh type here is therefore an explicit DECISION for that loader (does the obligation
/// schema accept it?), not an automatic edit.
/// <para/>
/// KNOWN GAP (ADR 0045): coercion happens only on the next EXTRACTION, and nothing re-extracts an
/// already-processed row — so a pre-deploy row carrying a non-canonical type keeps grading against zero
/// rules until a human re-types it or triggers a re-extraction. Deliberately not laundered by a data
/// migration; see the ADR for why. Since #443 / ADR 0047 that residue at least reads <c>Pending</c>
/// everywhere instead of rolling up to "Covered" — the population still needs re-typing, it just no
/// longer overclaims while it waits.
/// <para/>
/// Exposed as <c>internal</c> for direct unit testing via <c>InternalsVisibleTo CompliDrop.Api.Tests</c>,
/// matching <see cref="CanonicalDocumentFields"/> / <see cref="VerdictBearingFields"/>.
/// </summary>
internal static class CanonicalDocumentTypes
{
    /// <summary>
    /// The type an unrecognized value resolves to. "Unknown" is not a new state: <c>other</c> is already
    /// what the extraction prompt tells the model to emit when the type is unclear, what the DB column
    /// defaults to, and what the UI labels an unclassified document.
    /// </summary>
    public const string Fallback = "other";

    /// <summary>
    /// The vocabulary, in the order the provider schemas serialize it. Mirrors the DOCUMENT TYPES block
    /// of <see cref="Extraction.ExtractionPrompts.SystemPrompt"/> and
    /// <c>frontend/src/lib/document-types.ts</c>. Every entry is lower-case ASCII, and the longest is
    /// well under the <c>varchar(100)</c> column — so anything <see cref="Normalize"/> returns is
    /// length-safe BY CONSTRUCTION rather than by a clamp (pinned by a test against the EF model).
    /// </summary>
    public static readonly string[] All = ["coi", "license", "permit", "certification", "contract", "other"];

    /// <summary>
    /// Case-insensitive membership index. <c>TryGetValue</c> hands back the STORED literal, so
    /// <see cref="Normalize"/> returns an element of <see cref="All"/> itself rather than a
    /// lower-cased copy of caller input — no locale-dependent casing, no look-alike Unicode slipping
    /// through as a "canonical" value.
    /// </summary>
    private static readonly HashSet<string> Lookup = new(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="documentType"/> names a type in the vocabulary (trimmed,
    /// case-insensitive). The membership question a REQUEST path asks before rejecting user input with a
    /// 400 — deliberately distinct from <see cref="Normalize"/>, which coerces instead of rejecting: a
    /// human editing a type can be told to fix it, a background worker parsing a model response cannot.
    /// </summary>
    public static bool IsAllowed(string? documentType) => TryCanonicalize(documentType, out _);

    /// <summary>
    /// Coerces any untrusted value to the vocabulary: a recognized type (any casing, surrounding
    /// whitespace) returns its canonical spelling; anything else — unknown word, empty, whitespace, a
    /// 10 KB blob — returns <see cref="Fallback"/>.
    /// </summary>
    public static string Normalize(string? documentType) =>
        TryCanonicalize(documentType, out var canonical) ? canonical : Fallback;

    /// <summary>
    /// The extraction-pipeline form of <see cref="Normalize"/>: canonicalizes what the model returned,
    /// but treats a BLANK/absent answer as "the provider told us nothing" rather than as a positive
    /// classification of <see cref="Fallback"/>, falling back to the type already stored (itself
    /// normalized).
    /// <para/>
    /// "Absent" reaches this method only because both clients' <c>MapResult</c> map a missing or
    /// JSON-null <c>documentType</c> to <c>null</c> instead of to the literal <c>"other"</c> — the shape a
    /// provider ACTUALLY produces when it violates <c>required</c> is an omitted property, not an empty
    /// string, so coercing it there would forge a positive answer this branch could never see.
    /// <para/>
    /// Blank is off-spec — <c>documentType</c> is <c>required</c> in both providers' structured-output
    /// schemas — so it carries no information, and overwriting with <c>other</c> on the strength of it
    /// would DISCARD information: the stored type is typically the uploader's own pick from the document
    /// type dropdown (also passed to the model as its type hint), and demoting a deliberate
    /// <c>license</c> to <c>other</c> drops every license rule from the checklist and strands the
    /// document at <c>Pending</c> — the exact silent-never-graded outcome #373 exists to close.
    /// Normalizing the fallback too means a non-canonical STORED value (e.g. an <c>"COI"</c> written by
    /// an API client before the ingress paths validated) is still laundered rather than preserved.
    /// </summary>
    public static string NormalizeExtracted(string? extracted, string? current) =>
        string.IsNullOrWhiteSpace(extracted) ? Normalize(current) : Normalize(extracted);

    /// <summary>
    /// A JSON-schema <c>enum</c> array of the vocabulary, for the providers' structured-output contracts
    /// (Gemini <c>responseSchema</c>, Anthropic tool <c>input_schema</c>). Returns a FRESH array on every
    /// call because a <see cref="JsonNode"/> may only be attached to one parent — a cached instance would
    /// throw the moment the second client tried to adopt it.
    /// <para/>
    /// Both clients call this rather than spelling out their own literal list: the pre-#373 shape had the
    /// enum on Gemini only, so the alternate provider was free to emit <c>"Certificate of Insurance"</c>
    /// against a prompt that asks for <c>"coi"</c>.
    /// </summary>
    public static JsonArray SchemaEnum()
    {
        var values = new JsonArray();
        foreach (var type in All) values.Add(type);
        return values;
    }

    private static bool TryCanonicalize(string? value, out string canonical)
    {
        if (!string.IsNullOrWhiteSpace(value) && Lookup.TryGetValue(value.Trim(), out var found))
        {
            canonical = found;
            return true;
        }
        canonical = Fallback;
        return false;
    }
}
