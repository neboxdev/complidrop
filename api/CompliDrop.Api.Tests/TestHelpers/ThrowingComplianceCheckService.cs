using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// An <see cref="IComplianceCheckService"/> that always throws — swapped in via
/// <c>WithWebHostBuilder</c> to prove the best-effort re-eval guarantee shared by the
/// document mutation endpoints: a vendor assignment (DocumentEndpoints.UpdateDocument, #186)
/// or a field edit (DocumentEndpoints.UpdateFields, #216) must still succeed (200 + persisted
/// change) even when the inline compliance recompute blows up.
/// </summary>
public sealed class ThrowingComplianceCheckService : IComplianceCheckService
{
    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task ReevaluateForTemplateAsync(Guid templateId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task ReevaluateForVendorAsync(Guid vendorId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task ReevaluateForVendorsAsync(IReadOnlyList<Guid> vendorIds, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");
}
