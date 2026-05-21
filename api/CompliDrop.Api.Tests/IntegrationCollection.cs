using CompliDrop.Api.Tests.TestHelpers;

namespace CompliDrop.Api.Tests;

// All integration tests share a single Postgres container and run serially within this collection.
[CollectionDefinition("integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationTestFixture>
{
}
