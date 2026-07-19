using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// An <see cref="IComplianceCheckService"/> that always throws — swapped in via
/// <c>WithWebHostBuilder</c> to prove the best-effort re-eval guarantee shared by the
/// document mutation endpoints: a vendor assignment (DocumentEndpoints.UpdateDocument, #186)
/// or a field edit (DocumentEndpoints.UpdateFields, #216) must still succeed (200 + persisted
/// change) even when the inline compliance recompute blows up. Since #337 the verdict folds into
/// the edit's transaction, so a thrown <see cref="ApplyEvaluationAsync"/> degrades the verdict to
/// <see cref="ComplianceStatus.Pending"/> (never a torn confident verdict) while the edit still commits.
/// </summary>
public sealed class ThrowingComplianceCheckService : IComplianceCheckService
{
    public Task ApplyEvaluationAsync(Microsoft.EntityFrameworkCore.DbContext context, Document doc, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task ReevaluateForTemplateAsync(Guid templateId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task ReevaluateForTemplateOrDocumentsAsync(Guid templateId, IReadOnlyList<Guid> documentIds, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task ReevaluateForVendorAsync(Guid vendorId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task ReevaluateForVendorsAsync(IReadOnlyList<Guid> vendorIds, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task<RegradeResult> ReevaluateForTemplateForSystemAsync(Guid templateId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");
}
