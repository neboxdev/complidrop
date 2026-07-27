using System.Text;
using System.Text.RegularExpressions;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Middleware;
using CompliDrop.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pure unit tests (no database) for the audit-row client-input boundary (#372).
///
/// The inbound <c>User-Agent</c> and <c>X-Trace-Id</c> headers are unbounded client input that
/// lands in bounded <c>AuditLog</c> columns, and the audit row is written in the SAME
/// <c>SaveChanges</c> as the business mutation — so before this clamp an over-length header failed
/// the whole unit of work (Postgres 22001 -> 500), unauthenticated on the portal-upload route and
/// on the failed-login path, where it suppressed the attacker's own <c>user.login_failed</c> row.
///
/// The two width tests below are shared with <b>ADR 0046 / #389</b>, which generalized the same
/// structural binding to the request-input columns (<c>Services/InputLengths.cs</c>): ADR 0046 §1's
/// central decision is "one width source, consumed by the EF model", and reviewers.md calls a
/// re-inlined literal there a real finding. This is what makes it one — the mechanism is identical
/// (built-model equality plus a source-text assertion for the equal-valued re-inline the model test
/// cannot see), so the two constant families are enforced by one pair of tests rather than two.
/// </summary>
public class AuditClientInputClampTests
{
    // ---------------- ColumnClamp ----------------

    [Fact]
    public void Clamp_leaves_a_value_that_fits_exactly_as_it_is()
    {
        var atLimit = new string('a', 500);

        ColumnClamp.To(atLimit, 500).Should().BeSameAs(atLimit, "a fitting value must not be rebuilt");
        ColumnClamp.To("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", 500)
            .Should().Be("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "a normal UA must not be mangled");
    }

    [Fact]
    public void Clamp_truncates_an_over_limit_value_to_the_column_width()
    {
        ColumnClamp.To(new string('a', 501), 500).Should().HaveLength(500);
        ColumnClamp.To(new string('a', 100_000), 500).Should().HaveLength(500);
        ColumnClamp.To(new string('a', 65), 64).Should().HaveLength(64);
    }

    [Fact]
    public void Clamp_preserves_the_prefix_it_keeps()
    {
        // Truncation is forensically useful only if the head survives intact — the browser/OS
        // sits in the first ~120 chars of a User-Agent.
        var ua = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)" + new string('x', 600);

        ColumnClamp.To(ua, 500).Should().StartWith("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)");
    }

    [Fact]
    public void Clamp_passes_null_and_empty_through_untouched()
    {
        ColumnClamp.To(null, 500).Should().BeNull();
        ColumnClamp.To("", 500).Should().BeEmpty();
    }

    [Fact]
    public void Clamp_never_splits_a_surrogate_pair_at_the_cut()
    {
        // A lone high surrogate is an invalid string that Npgsql's strict UTF-8 encoder rejects at
        // SaveChangesAsync — the very write-path failure this clamp exists to remove. A hostile UA
        // can place an emoji on the boundary deliberately.
        var straddling = new string('a', 499) + "\U0001F600" + new string('b', 100);

        var clamped = ColumnClamp.To(straddling, 500)!;

        clamped.Should().HaveLength(499);
        clamped.Should().EndWith("a");
        var act = () => new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(clamped);
        act.Should().NotThrow("the clamped value must stay valid Unicode");
    }

    [Fact]
    public void Clamp_keeps_a_surrogate_pair_that_ends_exactly_on_the_boundary()
    {
        // The complement of the test above: when the pair ENDS at the cut it is whole and must be
        // kept, so the back-off never costs a character it didn't have to.
        var ending = new string('a', 498) + "\U0001F600" + new string('b', 100);

        ColumnClamp.To(ending, 500).Should().HaveLength(500).And.EndWith("\U0001F600");
    }

    [Theory]
    [InlineData(null, 0, null)]
    [InlineData("", 0, "")]
    [InlineData("a", 0, "")]
    [InlineData("abc", 0, "")]
    [InlineData("a", 1, "a")]
    [InlineData("ab", 1, "a")]
    [InlineData("\U0001F600x", 1, "")]  // the pair can't fit; backing off lands on index 0
    public void Clamp_handles_the_degenerate_widths_without_indexing_out_of_range(
        string? value, int maxLength, string? expected)
    {
        // The class doc invites new callers, and `To(value, 0)` used to throw
        // IndexOutOfRangeException: `1 <= 0` is false, then the surrogate probe read value[-1].
        // Zero is a legal width meaning "nothing fits" -> string.Empty, and null still passes
        // through as null (an absent value is not an over-length one).
        ColumnClamp.To(value, maxLength).Should().Be(expected);
    }

    [Fact]
    public void Clamp_refuses_a_negative_width_instead_of_silently_blanking_the_value()
    {
        // A negative width is a caller bug, not a narrow column — there is no such column. Returning
        // empty would quietly erase every audited value the mistyped call touched, which is exactly
        // the forensic evidence this clamp exists to preserve.
        var act = () => ColumnClamp.To("Mozilla/5.0", -1);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxLength");
        // Even for a value that would have "fit" trivially: fail on the bad width, not on the input.
        ((Action)(() => ColumnClamp.To(null, -1))).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---------------- the widths themselves ----------------

    [Fact]
    public void Every_clamped_column_takes_its_width_from_the_shared_constant()
    {
        // Since #372's structural wiring these widths agree BY CONSTRUCTION — ModelConfiguration
        // calls HasMaxLength(AuditColumnLengths.X) / HasMaxLength(CheckColumnMaxLength) rather than
        // re-declaring a number (the #369 ContactEmail.MaxLength pattern).
        //
        // Scope, precisely: this test reads the BUILT EF model and compares it to the constant, so
        // it catches a DIVERGENT literal (`HasMaxLength(200)`) — but an EQUAL-valued re-inline
        // (`HasMaxLength(500)`) passes here and only goes red once the constant later moves. The
        // structural binding itself is pinned at the source-text level by
        // ModelConfiguration_names_the_width_constants_rather_than_a_numeric_literal below.
        using var db = new SystemDbContext(new DbContextOptionsBuilder<SystemDbContext>()
            .UseNpgsql("Host=model-only;Database=none")
            .Options);

        int Width<TEntity>(string prop) =>
            db.Model.FindEntityType(typeof(TEntity))!.FindProperty(prop)!.GetMaxLength()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name}.{prop} is unbounded");

        Width<AuditLog>(nameof(AuditLog.UserAgent)).Should().Be(AuditColumnLengths.UserAgent);
        Width<AuditLog>(nameof(AuditLog.IpAddress)).Should().Be(AuditColumnLengths.IpAddress);
        Width<AuditLog>(nameof(AuditLog.CorrelationId)).Should().Be(AuditColumnLengths.CorrelationId);
        Width<ComplianceCheck>(nameof(ComplianceCheck.ActualValue))
            .Should().Be(ComplianceCheckService.CheckColumnMaxLength);
        Width<ComplianceCheck>(nameof(ComplianceCheck.Notes))
            .Should().Be(ComplianceCheckService.CheckColumnMaxLength);

        // ADR 0046 / #389: every REQUEST-input width, same rule. If one of these columns is widened or
        // narrowed out from under InputLengths, the edge guard that feeds it either re-opens the
        // 22001-as-500 or starts refusing values the column would have taken.
        Width<Organization>(nameof(Organization.Name)).Should().Be(InputLengths.OrganizationName);
        Width<Organization>(nameof(Organization.Industry)).Should().Be(InputLengths.OrganizationIndustry);
        Width<Organization>(nameof(Organization.CompanySize)).Should().Be(InputLengths.OrganizationCompanySize);
        Width<User>(nameof(User.FullName)).Should().Be(InputLengths.UserFullName);
        Width<User>(nameof(User.Email)).Should().Be(InputLengths.UserEmail);
        Width<EmailVerificationToken>(nameof(EmailVerificationToken.NewEmail)).Should().Be(InputLengths.UserEmail);
        Width<WaitlistEntry>(nameof(WaitlistEntry.Email)).Should().Be(InputLengths.WaitlistEmail);
        Width<WaitlistEntry>(nameof(WaitlistEntry.CompanyName)).Should().Be(InputLengths.WaitlistCompanyName);
        Width<WaitlistEntry>(nameof(WaitlistEntry.Industry)).Should().Be(InputLengths.WaitlistIndustry);
        Width<WaitlistEntry>(nameof(WaitlistEntry.Source)).Should().Be(InputLengths.WaitlistSource);
        Width<Document>(nameof(Document.OriginalFileName)).Should().Be(InputLengths.DocumentOriginalFileName);
        Width<DocumentField>(nameof(DocumentField.FieldName)).Should().Be(InputLengths.DocumentFieldName);
        Width<DocumentField>(nameof(DocumentField.FieldValue)).Should().Be(InputLengths.DocumentFieldValue);
        // OriginalValue is only ever a copy of FieldValue, so it shares that width by construction.
        Width<DocumentField>(nameof(DocumentField.OriginalValue)).Should().Be(InputLengths.DocumentFieldValue);
        Width<Vendor>(nameof(Vendor.Name)).Should().Be(InputLengths.VendorName);
        Width<Vendor>(nameof(Vendor.ContactPhone)).Should().Be(InputLengths.VendorContactPhone);
        Width<Vendor>(nameof(Vendor.Category)).Should().Be(InputLengths.VendorCategory);
        Width<ComplianceTemplate>(nameof(ComplianceTemplate.Name)).Should().Be(InputLengths.TemplateName);
        Width<ComplianceTemplate>(nameof(ComplianceTemplate.Description)).Should().Be(InputLengths.TemplateDescription);
        Width<ComplianceRule>(nameof(ComplianceRule.FieldName)).Should().Be(InputLengths.RuleFieldName);
        Width<ComplianceRule>(nameof(ComplianceRule.Operator)).Should().Be(InputLengths.RuleOperator);
        Width<ComplianceRule>(nameof(ComplianceRule.ExpectedValue)).Should().Be(InputLengths.RuleExpectedValue);
        Width<ComplianceRule>(nameof(ComplianceRule.ErrorMessage)).Should().Be(InputLengths.RuleErrorMessage);
        Width<Reminder>(nameof(Reminder.EmailSubjectTemplate))
            .Should().Be(InputLengths.ReminderEmailSubjectTemplate);
        Width<IdempotencyRecord>(nameof(IdempotencyRecord.Key)).Should().Be(InputLengths.IdempotencyKey);
        // Not an InputLengths entry on purpose (see that file's summary): ContactEmail owns its own
        // cap because #369's mirrored validator, not a #389 edge guard, is what bounds this column.
        Width<Vendor>(nameof(Vendor.ContactEmail)).Should().Be(ContactEmail.MaxLength);

        // The consequence that actually matters, asserted against the MODEL's width rather than the
        // constant: whatever the clamp emits fits the column EF will create.
        var hostile = new string('x', 100_000);
        ColumnClamp.To(hostile, AuditColumnLengths.UserAgent)!.Length
            .Should().BeLessThanOrEqualTo(Width<AuditLog>(nameof(AuditLog.UserAgent)));
        ColumnClamp.To(hostile, AuditColumnLengths.IpAddress)!.Length
            .Should().BeLessThanOrEqualTo(Width<AuditLog>(nameof(AuditLog.IpAddress)));
        ComplianceCheckService.ClampToColumn(hostile)!.Length
            .Should().BeLessThanOrEqualTo(Width<ComplianceCheck>(nameof(ComplianceCheck.Notes)));
        CorrelationIdMiddleware.Resolve(hostile).Length
            .Should().BeLessThanOrEqualTo(Width<AuditLog>(nameof(AuditLog.CorrelationId)));
    }

    [Fact]
    public void ModelConfiguration_names_the_width_constants_rather_than_a_numeric_literal()
    {
        // The model-width test above cannot catch an EQUAL-valued re-inline: it compares the built
        // model to the same constant, so `HasMaxLength(500)` hand-typed back over
        // `HasMaxLength(AuditColumnLengths.UserAgent)` stays green until the constant moves — at
        // which point the column and the clamp that feeds it silently disagree, which is exactly
        // the 22001 that #372 removed. So pin the BINDING itself, at the source-text level, the way
        // CleanupGateConfigTests pins the repo-root .editorconfig that drives the format gate.
        // reviewers.md calls a re-inlined literal here a real finding; this is what makes it one.
        var source = ReadModelConfigurationSource();

        AssertWidthNamesConstant<AuditLog>(source, nameof(AuditLog.IpAddress), "AuditColumnLengths.IpAddress");
        AssertWidthNamesConstant<AuditLog>(source, nameof(AuditLog.UserAgent), "AuditColumnLengths.UserAgent");
        AssertWidthNamesConstant<AuditLog>(source, nameof(AuditLog.CorrelationId), "AuditColumnLengths.CorrelationId");
        AssertWidthNamesConstant<ComplianceCheck>(
            source, nameof(ComplianceCheck.ActualValue), "ComplianceCheckService.CheckColumnMaxLength");
        AssertWidthNamesConstant<ComplianceCheck>(
            source, nameof(ComplianceCheck.Notes), "ComplianceCheckService.CheckColumnMaxLength");

        // ADR 0046 / #389 — the same binding for every request-input width. This half is the one that
        // catches an EQUAL-valued re-inline (`HasMaxLength(200)` typed back over
        // `HasMaxLength(InputLengths.OrganizationName)`), which the model-width test above cannot see.
        AssertWidthNamesConstant<Organization>(source, nameof(Organization.Name), "InputLengths.OrganizationName");
        AssertWidthNamesConstant<Organization>(source, nameof(Organization.Industry), "InputLengths.OrganizationIndustry");
        AssertWidthNamesConstant<Organization>(source, nameof(Organization.CompanySize), "InputLengths.OrganizationCompanySize");
        AssertWidthNamesConstant<User>(source, nameof(User.FullName), "InputLengths.UserFullName");
        AssertWidthNamesConstant<User>(source, nameof(User.Email), "InputLengths.UserEmail");
        AssertWidthNamesConstant<EmailVerificationToken>(
            source, nameof(EmailVerificationToken.NewEmail), "InputLengths.UserEmail");
        AssertWidthNamesConstant<WaitlistEntry>(source, nameof(WaitlistEntry.Email), "InputLengths.WaitlistEmail");
        AssertWidthNamesConstant<WaitlistEntry>(source, nameof(WaitlistEntry.CompanyName), "InputLengths.WaitlistCompanyName");
        AssertWidthNamesConstant<WaitlistEntry>(source, nameof(WaitlistEntry.Industry), "InputLengths.WaitlistIndustry");
        AssertWidthNamesConstant<WaitlistEntry>(source, nameof(WaitlistEntry.Source), "InputLengths.WaitlistSource");
        AssertWidthNamesConstant<Document>(source, nameof(Document.OriginalFileName), "InputLengths.DocumentOriginalFileName");
        AssertWidthNamesConstant<DocumentField>(source, nameof(DocumentField.FieldName), "InputLengths.DocumentFieldName");
        AssertWidthNamesConstant<DocumentField>(source, nameof(DocumentField.FieldValue), "InputLengths.DocumentFieldValue");
        AssertWidthNamesConstant<DocumentField>(source, nameof(DocumentField.OriginalValue), "InputLengths.DocumentFieldValue");
        AssertWidthNamesConstant<Vendor>(source, nameof(Vendor.Name), "InputLengths.VendorName");
        AssertWidthNamesConstant<Vendor>(source, nameof(Vendor.ContactPhone), "InputLengths.VendorContactPhone");
        AssertWidthNamesConstant<Vendor>(source, nameof(Vendor.Category), "InputLengths.VendorCategory");
        AssertWidthNamesConstant<Vendor>(source, nameof(Vendor.ContactEmail), "ContactEmail.MaxLength");
        AssertWidthNamesConstant<ComplianceTemplate>(source, nameof(ComplianceTemplate.Name), "InputLengths.TemplateName");
        AssertWidthNamesConstant<ComplianceTemplate>(source, nameof(ComplianceTemplate.Description), "InputLengths.TemplateDescription");
        AssertWidthNamesConstant<ComplianceRule>(source, nameof(ComplianceRule.FieldName), "InputLengths.RuleFieldName");
        AssertWidthNamesConstant<ComplianceRule>(source, nameof(ComplianceRule.Operator), "InputLengths.RuleOperator");
        AssertWidthNamesConstant<ComplianceRule>(source, nameof(ComplianceRule.ExpectedValue), "InputLengths.RuleExpectedValue");
        AssertWidthNamesConstant<ComplianceRule>(source, nameof(ComplianceRule.ErrorMessage), "InputLengths.RuleErrorMessage");
        AssertWidthNamesConstant<Reminder>(
            source, nameof(Reminder.EmailSubjectTemplate), "InputLengths.ReminderEmailSubjectTemplate");
        AssertWidthNamesConstant<IdempotencyRecord>(source, nameof(IdempotencyRecord.Key), "InputLengths.IdempotencyKey");
    }

    /// <summary>
    /// Asserts that <c>ModelConfiguration</c> configures <typeparamref name="TEntity"/>'s
    /// <paramref name="property"/> width by NAMING <paramref name="constant"/>, not by restating its
    /// number. Scoped to the entity's own <c>builder.Entity&lt;T&gt;(...)</c> block because property
    /// names are NOT unique across the file — <c>Name</c> alone appears on three entities, <c>Email</c>
    /// and <c>FieldName</c> on two — so a file-wide regex would silently assert against whichever
    /// entity happened to be configured first.
    /// </summary>
    private static void AssertWidthNamesConstant<TEntity>(string source, string property, string constant)
    {
        var block = EntityBlock(source, typeof(TEntity).Name);
        var match = Regex.Match(
            block, @$"\.Property\(\w+ => \w+\.{Regex.Escape(property)}\)\s*\.HasMaxLength\(([^)]*)\)");

        match.Success.Should().BeTrue(
            $"ModelConfiguration must still bound {typeof(TEntity).Name}.{property}");
        match.Groups[1].Value.Trim().Should().EndWith(
            constant,
            "the width of {0}.{1} must NAME {2}, not restate its number — a hand-typed literal makes "
                + "the column and the guard that feeds it two copies that can drift",
            typeof(TEntity).Name, property, constant);
    }

    /// <summary>
    /// The body of <c>builder.Entity&lt;<paramref name="entity"/>&gt;(e =&gt; { … })</c>, brace-matched
    /// from the first <c>{</c> after the marker.
    /// </summary>
    private static string EntityBlock(string source, string entity)
    {
        var marker = $"builder.Entity<{entity}>(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "ModelConfiguration must still configure {0}", entity);

        var open = source.IndexOf('{', start);
        open.Should().BeGreaterThanOrEqualTo(0);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces in the builder.Entity<{entity}> block");
    }

    private static string ReadModelConfigurationSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "api", "CompliDrop.Api", "Data", "ModelConfiguration.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate ModelConfiguration.cs from {AppContext.BaseDirectory}");
    }

    // ---------------- inbound X-Trace-Id ----------------

    [Theory]
    [InlineData("a1b2c3d4e5f6")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]                  // W3C trace-id
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")] // W3C traceparent
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479")]              // UUID
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV")]                        // ULID
    [InlineData("req_01HQ8Z-edge_2")]                                 // vendor id: '-' and '_'
    public void A_usable_inbound_trace_id_is_honored_verbatim(string inbound) =>
        CorrelationIdMiddleware.Resolve(inbound).Should().Be(inbound);

    [Theory]
    [InlineData("pat@gardenhall.com")]              // an EMAIL is 18 visible-ASCII chars
    [InlineData("pat.owner+coi@gardenhall.com")]
    [InlineData("(512) 555-0134")]                  // a phone number
    [InlineData("Bluebonnet Events, LLC")]          // free text / a customer name
    [InlineData("req_01HQ8Z/edge-2.upstream:9")]    // any other punctuation
    [InlineData("<script>alert(1)</script>")]
    [InlineData("'; DROP TABLE audit_logs; --")]
    public void A_pii_shaped_inbound_trace_id_is_replaced_never_echoed_back(string inbound)
    {
        // LOAD-BEARING, not aesthetics. An accepted id is echoed in the X-Trace-Id response header,
        // becomes ApiError.correlationId in the frontend, and is shipped to Sentry as the
        // `correlation_id` tag — which ADR 0037 deliberately applies AFTER scrubEvent and does NOT
        // redact, on the premise that a correlation id is an opaque identifier. Under a
        // visible-ASCII charset a client could put an email address in that tag. This test goes RED
        // the moment IsUsableTraceId is widened back.
        var resolved = CorrelationIdMiddleware.Resolve(inbound);

        resolved.Should().NotBe(inbound);
        resolved.Should().NotContain("@").And.NotContain(" ");
        CorrelationIdMiddleware.IsUsableTraceId(inbound)
            .Should().BeFalse("only [A-Za-z0-9_-] may reach the un-redacted Sentry correlation_id tag");
    }

    [Fact]
    public void The_trace_id_charset_is_exactly_ascii_alphanumerics_plus_dash_and_underscore()
    {
        // Walk every BMP code point rather than trusting a hand-listed sample: the class the
        // predicate accepts must be exactly the documented one, in both directions.
        for (var cp = 0; cp <= 0xFFFF; cp++)
        {
            var c = (char)cp;
            var expected = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';

            CorrelationIdMiddleware.IsUsableTraceId(c.ToString())
                .Should().Be(expected, "U+{0:X4} must {1} be a legal trace-id character",
                    cp, expected ? "" : "not");
        }
    }

    [Fact]
    public void An_inbound_trace_id_that_would_not_fit_the_column_is_replaced_not_truncated()
    {
        var oversize = new string('a', 65);

        var resolved = CorrelationIdMiddleware.Resolve(oversize);

        // Replaced, not clamped: a truncated prefix correlates nothing while looking like it does,
        // and it would manufacture collisions in the activity feed's
        // (CorrelationId, EntityType, EntityId) collapse.
        resolved.Should().NotBe(oversize);
        resolved.Should().NotStartWith("aaaa");
        resolved.Length.Should().BeLessThanOrEqualTo(AuditColumnLengths.CorrelationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]                       // blank would blank the echoed response header
    [InlineData("trace\r\nX-Injected: 1")]    // header injection — its own self-inflicted 500
    [InlineData("trace\u0000id")]              // a raw NUL byte
    [InlineData("trace\tid")]                  // any other control character
    [InlineData("caf\u00e9-trace")]                // non-ASCII: a response header is not a Unicode sink
    [InlineData("trace id with spaces")]
    public void An_unusable_inbound_trace_id_is_replaced_with_a_fresh_one(string? inbound)
    {
        var resolved = CorrelationIdMiddleware.Resolve(inbound);

        resolved.Should().NotBe(inbound);
        CorrelationIdMiddleware.IsUsableTraceId(resolved)
            .Should().BeTrue("a minted id must itself satisfy the rule it replaced a value for");
    }

    [Fact]
    public void A_minted_trace_id_always_fits_the_column()
    {
        for (var i = 0; i < 50; i++)
            CorrelationIdMiddleware.Resolve(null).Length
                .Should().BeLessThanOrEqualTo(AuditColumnLengths.CorrelationId);
    }

    [Fact]
    public void The_trace_id_length_bound_is_inclusive_at_the_column_width()
    {
        var atLimit = new string('a', AuditColumnLengths.CorrelationId);

        CorrelationIdMiddleware.IsUsableTraceId(atLimit).Should().BeTrue();
        CorrelationIdMiddleware.IsUsableTraceId(atLimit + "a").Should().BeFalse();
    }
}
