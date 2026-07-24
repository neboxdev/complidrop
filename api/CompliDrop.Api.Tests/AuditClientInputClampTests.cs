using System.Text;
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

    // ---------------- the widths themselves ----------------

    [Fact]
    public void The_clamp_widths_equal_the_audit_columns_they_protect()
    {
        // The mechanical pin: these constants exist only to mirror ModelConfiguration. If a column
        // is ever widened or narrowed without the boundary following, the clamp either truncates
        // usable evidence or stops preventing the 22001 — so pin them equal rather than trusting
        // two hand-copied numbers (the #367 PlanDocumentScope lesson).
        using var db = new SystemDbContext(new DbContextOptionsBuilder<SystemDbContext>()
            .UseNpgsql("Host=model-only;Database=none")
            .Options);
        var audit = db.Model.FindEntityType(typeof(AuditLog))!;

        int? Width(string prop) => audit.FindProperty(prop)!.GetMaxLength();

        Width(nameof(AuditLog.UserAgent)).Should().Be(AuditColumnLengths.UserAgent);
        Width(nameof(AuditLog.IpAddress)).Should().Be(AuditColumnLengths.IpAddress);
        Width(nameof(AuditLog.CorrelationId)).Should().Be(AuditColumnLengths.CorrelationId);
    }

    // ---------------- inbound X-Trace-Id ----------------

    [Theory]
    [InlineData("a1b2c3d4e5f6")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]                  // W3C trace-id
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479")]              // UUID
    [InlineData("req_01HQ8Z/edge-2.upstream:9")]                      // punctuation is fine
    public void A_usable_inbound_trace_id_is_honored_verbatim(string inbound) =>
        CorrelationIdMiddleware.Resolve(inbound).Should().Be(inbound);

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
