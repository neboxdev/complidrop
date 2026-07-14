using System.Text.Json;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Entities;
using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// The persistence/flag wiring around the pure engine: the per-rule-set feature-flag catalog (SCHEMA §6 —
/// default OFF, hard safe posture, fail-fast on a bad selection) and the EF → engine adapters
/// (Organization/Vendor profile columns → EntityProfile; Document → IDocumentLike).
/// </summary>
public class RegulatoryWiringTests
{
    // ---------------- RegulatoryRuleCatalog (feature flags) ----------------

    [Fact]
    public void The_catalog_is_disabled_and_empty_by_default()
    {
        var catalog = RegulatoryRuleCatalog.Create(new RuleEngineSettings());

        catalog.Enabled.Should().BeFalse("the engine ships feature-flag OFF until gate G1 clears");
        catalog.RuleSet.Rules.Should().BeEmpty();
        catalog.EnabledRuleSets.Should().BeEmpty();
    }

    [Fact]
    public void Enabling_a_single_rule_set_loads_only_that_file_in_the_safe_posture()
    {
        var catalog = RegulatoryRuleCatalog.Create(new RuleEngineSettings
        {
            Enabled = true,
            EnabledRuleSets = ["us-fed/caterer"],
        });

        catalog.Enabled.Should().BeTrue();
        catalog.RuleSet.Rules.Should().OnlyContain(r => r.Jurisdiction == "us-fed" && r.EntityTypes.Contains("caterer"));
        catalog.RuleSet.Rules.Should().Contain(r => r.Id == "fed-caterer-ttb-alcohol-dealer");
    }

    [Fact]
    public void An_unknown_rule_set_key_fails_the_boot_rather_than_silently_loading_nothing()
    {
        var act = () => RegulatoryRuleCatalog.Create(new RuleEngineSettings
        {
            Enabled = true,
            EnabledRuleSets = ["us-tx/florist"],
        });

        act.Should().Throw<RuleSchemaException>().WithMessage("*matches no embedded RuleData file*");
    }

    [Fact]
    public void An_incoherent_selection_missing_a_satisfies_federal_counterpart_fails_the_boot()
    {
        // us-tx/transportation declares satisfiesFederal against the federal transportation file — enabling
        // the state file alone must fail loudly, not silently double-emit or dangle.
        var act = () => RegulatoryRuleCatalog.Create(new RuleEngineSettings
        {
            Enabled = true,
            EnabledRuleSets = ["us-tx/transportation"],
        });

        act.Should().Throw<RuleSchemaException>().WithMessage("*satisfiesFederal*");
    }

    [Fact]
    public void The_catalog_can_never_load_probable_rules_and_honors_a_review_gate()
    {
        // The safe posture is hard-coded, not configurable: probable versions never load from any file
        // (us-tx/venue-org carries the probable food-establishment permit), and a review-gated file loads
        // nothing (mechanism pinned on synthetic data in RuleSetLoaderGuardTests — the shipped TX security
        // gate was lifted 2026-07-09 after G2 closed, so it now loads its 5 verified rules).
        var venueCatalog = RegulatoryRuleCatalog.Create(new RuleEngineSettings
        {
            Enabled = true,
            EnabledRuleSets = ["us-tx/venue-org"],
        });
        venueCatalog.RuleSet.Rules.Should().NotContain(r => r.Id == "tx-venue-food-establishment-permit",
            "probable rules never load through the app wiring");
        venueCatalog.RuleSet.Rules.Should().Contain(r => r.Id == "tx-venue-franchise-tax-report");

        var securityCatalog = RegulatoryRuleCatalog.Create(new RuleEngineSettings
        {
            Enabled = true,
            EnabledRuleSets = ["us-tx/security-service"],
        });
        securityCatalog.RuleSet.Rules.Should().HaveCount(5,
            "the TX security set ships since its G2 gate closed (2026-07-09)");
    }

    [Fact]
    public void Every_shipped_rule_data_file_answers_to_a_friendly_flag_key()
    {
        var keys = EmbeddedRuleData.ResourceNames.Select(EmbeddedRuleData.FriendlyKey).ToList();

        keys.Should().BeEquivalentTo(
        [
            "us-fed/caterer", "us-fed/event-rental", "us-fed/photographer-videographer",
            "us-fed/transportation", "us-fed/venue-org",
            "us-tx/caterer", "us-tx/cross-cutting", "us-tx/event-rental", "us-tx/security-service",
            "us-tx/transportation", "us-tx/venue-org",
        ]);
    }

    // ---------------- RegulatoryProfileMapper (EF → engine) ----------------

    private static JsonDocument Json(string json) => JsonDocument.Parse(json);

    [Fact]
    public void An_organization_projects_as_a_venue_with_its_state_and_facts()
    {
        var org = new Organization
        {
            State = "US-TX",
            RegulatoryFactsJson = Json("""{ "employeeCount": 12, "servesOrSellsAlcohol": true }"""),
        };

        var profile = RegulatoryProfileMapper.ForOrganization(org);

        profile.TryGet(FactNames.State, out var state).Should().BeTrue();
        state.AsString.Should().Be("US-TX");
        profile.TryGet(FactNames.EntityType, out var type).Should().BeTrue();
        type.AsString.Should().Be(EntityTypes.VenueOrg);
        profile.TryGet(FactNames.EmployeeCount, out var count).Should().BeTrue();
        count.AsInt.Should().Be(12);
        profile.TryGet(FactNames.ServesOrSellsAlcohol, out var alcohol).Should().BeTrue();
        alcohol.AsBool.Should().BeTrue();
    }

    [Fact]
    public void A_vendor_projects_with_its_org_state_and_its_own_entity_type()
    {
        var org = new Organization { State = "US-TX" };
        var vendor = new Vendor
        {
            EntityType = "caterer",
            RegulatoryFactsJson = Json("""{ "preparesOrServesFood": true }"""),
        };

        var profile = RegulatoryProfileMapper.ForVendor(vendor, org);

        profile.TryGet(FactNames.State, out var state).Should().BeTrue();
        state.AsString.Should().Be("US-TX");
        profile.TryGet(FactNames.EntityType, out var type).Should().BeTrue();
        type.AsString.Should().Be("caterer");
        profile.IsSet(FactNames.PreparesOrServesFood).Should().BeTrue();
    }

    [Fact]
    public void Unset_columns_stay_unknown_so_the_engine_asks_rather_than_guesses()
    {
        var profile = RegulatoryProfileMapper.ForVendor(new Vendor(), new Organization());

        profile.IsSet(FactNames.State).Should().BeFalse();
        profile.IsSet(FactNames.EntityType).Should().BeFalse();
    }

    [Fact]
    public void Malformed_or_unknown_json_facts_are_skipped_never_guessed()
    {
        // Unknown fact name, wrong-kind value, and a legacy junk entry: each stays UNKNOWN (fail-safe —
        // the engine emits needs-profile-info) instead of throwing or being coerced.
        var org = new Organization
        {
            RegulatoryFactsJson = Json("""
                { "notARealFact": true, "employeeCount": "twelve", "servesOrSellsAlcohol": 1, "preparesOrServesFood": true }
                """),
        };

        var profile = RegulatoryProfileMapper.ForOrganization(org);

        profile.IsSet(FactNames.EmployeeCount).Should().BeFalse("a string is not an int fact value");
        profile.IsSet(FactNames.ServesOrSellsAlcohol).Should().BeFalse("a number is not a bool fact value");
        profile.IsSet(FactNames.PreparesOrServesFood).Should().BeTrue("the valid entry still loads");
    }

    [Fact]
    public void The_dedicated_state_and_entity_type_columns_win_over_json_duplicates()
    {
        var org = new Organization
        {
            State = "US-TX",
            RegulatoryFactsJson = Json("""{ "state": "US-CA" }"""),
        };

        var profile = RegulatoryProfileMapper.ForOrganization(org);

        profile.TryGet(FactNames.State, out var state).Should().BeTrue();
        state.AsString.Should().Be("US-TX");
    }

    [Fact]
    public void A_document_projects_its_dates_and_extracted_liability_limit()
    {
        var document = new Document
        {
            Id = Guid.NewGuid(),
            DocumentType = "coi",
            DocumentSubType = "tx-security-gl",
            EffectiveDate = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            ExpirationDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            GeneralLiabilityLimit = 1_000_000m,
        };

        var like = RegulatoryProfileMapper.ToDocumentLike(document);

        like.Id.Should().Be(document.Id.ToString());
        like.DocumentType.Should().Be("coi");
        like.DocumentSubType.Should().Be("tx-security-gl");
        like.IssueDate.Should().Be(new DateOnly(2026, 1, 15), "IssueDate maps from the extracted EffectiveDate");
        like.ExpirationDate.Should().Be(new DateOnly(2027, 1, 15));
        like.GeneralLiabilityLimit.Should().Be(1_000_000m);
    }
}
