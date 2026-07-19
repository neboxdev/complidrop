using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// An <see cref="IComplianceCheckService"/> spy that records which template ids the seed's convergence
/// asked to re-grade via <see cref="ReevaluateForTemplateForSystemAsync"/>. Lets the convergence tests
/// (#416 / ADR 0036) assert the re-grade DISCRIMINATION contract directly — that a verdict-affecting
/// change (rule added / deleted / ExpectedValue changed) triggers a cross-org re-grade while a purely
/// verdict-neutral edit (error message / sort order / description) updates the row but triggers NONE —
/// without seeding documents and observing status flips. Every other method throws: the seed path only
/// ever calls the system fan-out, so an unexpected call is a bug the test should surface.
/// </summary>
public sealed class RecordingComplianceCheckService : IComplianceCheckService
{
    public List<Guid> RegradedTemplateIds { get; } = [];

    public Task<RegradeResult> ReevaluateForTemplateForSystemAsync(Guid templateId, CancellationToken ct)
    {
        RegradedTemplateIds.Add(templateId);
        // Report FULL success (no failed page) so the seed advances the template's watermark — the
        // convergence tests using this spy assert the template WAS re-graded. The partial-failure
        // (watermark-held-back) path is exercised by FailingSystemReevaluator instead.
        return Task.FromResult(new RegradeResult(0, 0, 0));
    }

    public Task ApplyEvaluationAsync(DbContext context, Document doc, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls ApplyEvaluationAsync.");

    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls EvaluateAsync.");

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls EvaluateForSystemAsync.");

    public Task ReevaluateForTemplateAsync(Guid templateId, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls the tenant-filtered ReevaluateForTemplateAsync.");

    public Task ReevaluateForTemplateOrDocumentsAsync(Guid templateId, IReadOnlyList<Guid> documentIds, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls ReevaluateForTemplateOrDocumentsAsync.");

    public Task ReevaluateForVendorAsync(Guid vendorId, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls ReevaluateForVendorAsync.");

    public Task ReevaluateForVendorsAsync(IReadOnlyList<Guid> vendorIds, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls ReevaluateForVendorsAsync.");
}
