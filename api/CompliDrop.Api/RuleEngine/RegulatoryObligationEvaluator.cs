using CompliDrop.Api.Services;

namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// The regulatory obligation engine (SCHEMA §6). Pure: <c>(EntityProfile, documents, evaluationDate,
/// RuleSet)</c> → <see cref="ObligationReport"/>. No DB, no LLM, no clock reads — the evaluation date is
/// injected. This is the UPSTREAM layer that answers "which credentials does this ENTITY need by law?",
/// separate from <see cref="ComplianceCheckService"/> (which grades one uploaded document against a per-org
/// CONTRACTUAL checklist). It mirrors that service's purity + date-injection idiom but shares no state.
///
/// Pipeline:
///   1. Coverage (SCHEMA §3): a known non-covered state, or a SET but unmodeled entity type, ⇒ <c>NotCovered</c>,
///      never empty-compliant. A NULL entity type is NOT an all-clear — its type-scoped rules read needs-profile-info.
///   2. Candidate selection: federal rules + the entity's-state rules (additive), pre-filtered by entity type,
///      each resolved to the version effective at <paramref name="evaluationDate"/> (validFrom/validTo).
///   3. Kleene applicability per candidate.
///   4. satisfiesFederal suppression: an APPLICABLE (Kleene-True) state rule suppresses the federal rules it implements.
///   5. Per-obligation status from matching document presence/expiry + cadence; Unknown ⇒ needs-profile-info,
///      a matched doc with no determinable currency on a renewing obligation ⇒ needs-document-info (never a false satisfied).
/// </summary>
public static class RegulatoryObligationEvaluator
{
    private const string FederalJurisdiction = "us-fed";
    private const int ExpiringWindowDays = ComplianceStatusDeriver.ExpiringSoonWindowDays;

    public static ObligationReport Evaluate(
        EntityProfile profile,
        IReadOnlyList<IDocumentLike> documents,
        DateOnly evaluationDate,
        RuleSet ruleSet,
        CompletenessNotice? completenessNotice = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(ruleSet);
        var notice = completenessNotice ?? CompletenessNotice.Default();

        // --- 1. Coverage (SCHEMA §3) --------------------------------------------------------------
        var coveredStates = ruleSet.Rules
            .Select(r => r.Jurisdiction)
            .Where(j => !IsFederal(j))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? stateSlug = profile.TryGet(FactNames.State, out var stateFact) && stateFact.Kind == FactKind.String
            ? NormalizeStateSlug(stateFact.AsString)
            : null;

        if (stateSlug is not null && coveredStates.Count > 0 && !coveredStates.Contains(stateSlug))
            return ObligationReport.NotCovered(
                $"CompliDrop does not yet cover \"{stateFact.AsString}\". Regulatory obligations are currently available for Texas (US-TX) only.",
                notice);

        string? entityType = profile.TryGet(FactNames.EntityType, out var entityTypeFact) && entityTypeFact.Kind == FactKind.String
            ? entityTypeFact.AsString
            : null;

        // Entity-type coverage (A-3/C-1). The modeled set is the union of the rule set's own entityTypes —
        // for the production data this equals EntityTypes.KnownModeled (pinned by a test). A SET but UNMODELED
        // type ⇒ NotCovered (mirrors the unknown-state path), never a bare all-clear; a NULL type is handled
        // per-rule below (type-scoped rules ⇒ needs-profile-info), never bypassed into a wrong verdict.
        var modeledEntityTypes = ruleSet.Rules
            .SelectMany(r => r.EntityTypes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (entityType is not null && modeledEntityTypes.Count > 0 && !modeledEntityTypes.Contains(entityType))
            return ObligationReport.NotCovered(
                $"No rule set is modeled for entity type \"{entityTypeFact.AsString}\".",
                notice);

        // --- 2. Candidate selection + version resolution ------------------------------------------
        var candidates = new List<Candidate>();
        foreach (var rule in ruleSet.Rules)
        {
            // Jurisdiction: federal always considered; a state rule only when it matches the entity's state.
            // An unknown state selects no state rules (we never apply a state's law to an entity whose state
            // we don't know) — surfaced below via OutstandingProfileFacts, not silently swallowed.
            if (!IsFederal(rule.Jurisdiction) && (stateSlug is null || !Eq(rule.Jurisdiction, stateSlug)))
                continue;

            // Entity type: a structural pre-filter. Omit a type-scoped rule ONLY when we KNOW the type and
            // it isn't in the list (a SET but unmodeled type already returned NotCovered above).
            var typeScoped = rule.EntityTypes.Count > 0;
            if (typeScoped && entityType is not null
                && !rule.EntityTypes.Contains(entityType, StringComparer.OrdinalIgnoreCase))
                continue;

            var version = ResolveEffectiveVersion(rule, evaluationDate);
            if (version is null) continue; // no version in effect at this date

            ApplicabilityResult applicability;
            if (typeScoped && entityType is null)
            {
                // Type-scoped but the entity type is UNKNOWN. Never bypass the type filter into a Kleene-True
                // (which would emit a wrong Missing/Satisfied — e.g. tell a caterer it needs a DPS guard
                // license, C-1). Force NeedsProfileInfo(entityType); as Unknown it also can't suppress a
                // federal floor. Rules with NO type scope (EntityTypes empty) still evaluate normally.
                applicability = new ApplicabilityResult(Kleene.Unknown, new HashSet<string> { FactNames.EntityType });
            }
            else
            {
                applicability = ApplicabilityEvaluator.Evaluate(version.Applicability, profile);
            }
            candidates.Add(new Candidate(rule, version, applicability));
        }

        // --- 3/4. satisfiesFederal suppression (SCHEMA §3) ----------------------------------------
        // Only an APPLICABLE (Kleene-True) state rule suppresses a federal floor. Suppressing on Unknown
        // could hide a federal obligation the state rule turns out not to impose — never assert from ignorance.
        var suppressedFederalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
            if (!IsFederal(candidate.Rule.Jurisdiction) && candidate.Applicability.Value == Kleene.True)
                foreach (var fedRef in candidate.Version.SatisfiesFederal)
                    suppressedFederalIds.Add(fedRef);

        // --- 5. Build per-obligation results ------------------------------------------------------
        var results = new List<ObligationResult>();
        foreach (var candidate in candidates)
        {
            if (IsFederal(candidate.Rule.Jurisdiction) && suppressedFederalIds.Contains(candidate.Rule.Id))
                continue; // implemented by an applicable state credential; not a separate obligation

            results.Add(BuildResult(candidate, documents, evaluationDate));
        }

        // Deterministic order: ACTIONABLE statuses first (A-4) so a same-obligationRef NotApplicable /
        // Satisfied sibling can never shadow an actionable result in a naive First(ref==) lookup; then by
        // obligationRef, then name. Output stays stable regardless of file load order.
        results.Sort((a, b) =>
        {
            var byRank = ActionabilityRank(a.Status).CompareTo(ActionabilityRank(b.Status));
            if (byRank != 0) return byRank;
            var byRef = string.CompareOrdinal(a.ObligationRef, b.ObligationRef);
            return byRef != 0 ? byRef : string.CompareOrdinal(a.Name, b.Name);
        });

        // Selection facts we couldn't resolve become outstanding profile questions, so an unknown state or
        // entity type is visible to the caller rather than quietly narrowing the result.
        var extraOutstanding = new List<string>();
        if (stateSlug is null && coveredStates.Count > 0) extraOutstanding.Add(FactNames.State);
        if (entityType is null && ruleSet.Rules.Any(r => r.EntityTypes.Count > 0)) extraOutstanding.Add(FactNames.EntityType);

        // Local obligations (CC-7): union the "noted, not encoded" pointers of every rule-set that applied to
        // this entity (its candidate rules' source files) into the completeness notice.
        var localPointers = candidates
            .SelectMany(c => c.Rule.LocalObligations)
            .Concat(notice.LocalObligationPointers)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (localPointers.Count > 0)
            notice = notice with { LocalObligationPointers = localPointers };

        return ObligationReport.ForObligations(results, notice, extraOutstanding);
    }

    /// <summary>The version whose [validFrom, validTo] window (validTo INCLUSIVE, null = open) contains the date; latest validFrom wins on overlap.</summary>
    internal static RuleVersion? ResolveEffectiveVersion(Rule rule, DateOnly evaluationDate)
    {
        RuleVersion? best = null;
        foreach (var version in rule.Versions)
        {
            if (version.ValidFrom > evaluationDate) continue;
            if (version.ValidTo is { } to && evaluationDate > to) continue;
            if (best is null || version.ValidFrom > best.ValidFrom)
                best = version;
        }
        return best;
    }

    private static ObligationResult BuildResult(Candidate candidate, IReadOnlyList<IDocumentLike> documents, DateOnly evaluationDate)
    {
        var (rule, version, applicability) = (candidate.Rule, candidate.Version, candidate.Applicability);
        var obligation = version.Obligation;

        var baseResult = new ObligationResult
        {
            RuleId = rule.Id,
            ObligationRef = rule.ObligationRef,
            Name = obligation.Name,
            Status = ObligationStatus.NotApplicable, // overwritten below
            Citation = version.Citation,
            Rationale = version.Rationale,
            UserAction = version.UserAction,
        };

        switch (applicability.Value)
        {
            case Kleene.False:
                return baseResult with { Status = ObligationStatus.NotApplicable };

            case Kleene.Unknown:
                return baseResult with
                {
                    Status = ObligationStatus.NeedsProfileInfo,
                    MissingFacts = applicability.MissingFacts.OrderBy(f => f, StringComparer.Ordinal).ToList(),
                };

            case Kleene.True:
                return ComputeSatisfaction(baseResult, obligation, version.Cadence, documents, evaluationDate);

            default:
                throw new ArgumentOutOfRangeException(nameof(candidate));
        }
    }

    /// <summary>
    /// For an APPLICABLE obligation, derives status from the matching tracked document(s) + cadence. No
    /// match ⇒ <c>Missing</c>. A matched document's printed expiry takes precedence (mirroring the
    /// contractual grader's date rules and reusing its 30-day window); a document without a printed expiry
    /// falls back to the cadence's computed due date. Applicability-Unknown never reaches here, so a present
    /// document can never launder an unresolved interstate/intrastate branch into a "satisfied" (legal-req #1).
    /// </summary>
    private static ObligationResult ComputeSatisfaction(
        ObligationResult baseResult,
        Obligation obligation,
        Cadence? cadence,
        IReadOnlyList<IDocumentLike> documents,
        DateOnly evaluationDate)
    {
        var match = documents
            .Where(d => Matches(d, obligation))
            // Prefer the document that extends coverage furthest — the latest printed expiry, then any.
            .OrderByDescending(d => d.ExpirationDate ?? DateOnly.MinValue)
            .FirstOrDefault();

        if (match is null)
        {
            // A conditional filing (e.g. the amusement injury report) is owed only on a triggering event the
            // profile can't assert, so with no document on record it is NOT "Missing" — it is NotApplicable
            // until the trigger occurs (CC-1). Its rationale/userAction state "file only if triggered".
            if (cadence is { Kind: CadenceKind.ConditionalFiling })
                return baseResult with { Status = ObligationStatus.NotApplicable };

            // Missing: no document/attestation on record. Still surface the next cadence deadline where the
            // cadence alone determines it (e.g. a fixed-annual filing), so the caller can schedule a reminder.
            var due = cadence is null ? null : CadenceCalculator.ComputeNextDueDate(cadence, null, null, evaluationDate);
            return baseResult with { Status = ObligationStatus.Missing, NextDueDate = due };
        }

        DateOnly? nextDue;
        ObligationStatus status;

        if (match.ExpirationDate is { } expiry)
        {
            nextDue = expiry;
            status = StatusFromExpiry(expiry, evaluationDate);
        }
        else if (cadence is not null)
        {
            nextDue = CadenceCalculator.ComputeNextDueDate(cadence, null, match.IssueDate, evaluationDate);
            if (nextDue is not null)
            {
                // A determinable due date from the cadence (e.g. an issueDate-anchored Part-107 recency whose
                // 24-month clock runs from the document's issue date): classify the timing normally.
                status = StatusFromTiming(CadenceCalculator.ClassifyTiming(nextDue, evaluationDate, cadence.GracePeriodDays, ExpiringWindowDays));
            }
            else
            {
                // No printed expiry AND the cadence yields no due date. A held ONE-TIME credential needs no
                // renewal (Satisfied); any renewing / fixed-annual / conditional-filing obligation can't be
                // confirmed current from a document with no readable expiry ⇒ NeedsDocumentInfo. NEVER a
                // false Satisfied from an undeterminable expiry on a renewing obligation (A-1).
                status = cadence.Kind == CadenceKind.OneTime ? ObligationStatus.Satisfied : ObligationStatus.NeedsDocumentInfo;
            }
        }
        else
        {
            // Present, no printed expiry, no cadence block at all → nothing more to track (a one-time credential we hold).
            nextDue = null;
            status = ObligationStatus.Satisfied;
        }

        return baseResult with { Status = status, NextDueDate = nextDue, MatchedDocumentId = match.Id };
    }

    /// <summary>
    /// Matches a tracked document to an obligation by (DocumentType, documentSubType) — RD-c. When the
    /// obligation names a subtype the document must carry that exact subtype (so an unmapped/legacy
    /// document with no subtype never auto-satisfies a specific obligation); otherwise DocumentType alone matches.
    /// </summary>
    private static bool Matches(IDocumentLike doc, Obligation obligation)
    {
        if (!Eq(doc.DocumentType, obligation.DocumentType)) return false;
        if (string.IsNullOrEmpty(obligation.DocumentSubType)) return true;
        return doc.DocumentSubType is { } sub && Eq(sub, obligation.DocumentSubType);
    }

    private static ObligationStatus StatusFromExpiry(DateOnly expiry, DateOnly evaluationDate)
    {
        if (expiry < evaluationDate) return ObligationStatus.Expired;                 // strict: expires-today isn't yet expired
        if (expiry <= evaluationDate.AddDays(ExpiringWindowDays)) return ObligationStatus.Expiring;
        return ObligationStatus.Satisfied;
    }

    private static ObligationStatus StatusFromTiming(CadenceTiming timing) => timing switch
    {
        CadenceTiming.Overdue => ObligationStatus.Expired,
        CadenceTiming.DueSoon => ObligationStatus.Expiring,
        _ => ObligationStatus.Satisfied, // NotYetDue / NoDeadline
    };

    /// <summary>
    /// Sort rank so ACTIONABLE statuses (something the user must collect / resolve) come before the inert
    /// ones (Satisfied / NotApplicable). This is what guarantees a same-obligationRef NotApplicable sibling
    /// can't shadow an actionable result when a caller does a naive First(ref==) lookup (A-4).
    /// </summary>
    private static int ActionabilityRank(ObligationStatus status) => status switch
    {
        ObligationStatus.Missing => 0,
        ObligationStatus.Expired => 0,
        ObligationStatus.Expiring => 0,
        ObligationStatus.NeedsDocumentInfo => 0,
        ObligationStatus.NeedsProfileInfo => 0,
        _ => 1, // Satisfied, NotApplicable
    };

    private static bool IsFederal(string jurisdiction) => Eq(jurisdiction, FederalJurisdiction);

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a profile state value to a jurisdiction slug. Accepts the common Texas spellings — "US-TX",
    /// "us-tx", "tx", "Texas" (case- and surrounding-space-insensitive) — all mapping to "us-tx" (A-7). Any
    /// other value passes through lowercased and, if not a covered state, yields NotCovered (fail-safe).
    /// </summary>
    private static string NormalizeStateSlug(string state)
    {
        var s = state.Trim().ToLowerInvariant();
        return s is "tx" or "texas" or "us-tx" ? "us-tx" : s;
    }

    private readonly record struct Candidate(Rule Rule, RuleVersion Version, ApplicabilityResult Applicability);
}
