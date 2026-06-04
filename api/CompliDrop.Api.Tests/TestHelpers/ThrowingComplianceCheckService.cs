using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// An <see cref="IComplianceCheckService"/> that always throws — swapped in via
/// <c>WithWebHostBuilder</c> to prove the PATCH endpoint's best-effort re-eval guarantee:
/// a vendor assignment must still succeed (200 + persisted VendorId) even when the inline
/// compliance recompute blows up. See DocumentEndpoints.UpdateDocument (#186).
/// </summary>
public sealed class ThrowingComplianceCheckService : IComplianceCheckService
{
    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("Simulated compliance-engine failure.");
}
