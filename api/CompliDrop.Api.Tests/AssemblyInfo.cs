using Xunit;

// The integration tests share a single Postgres container and a single WebApplicationFactory
// host (the "integration" collection). Run the whole suite serially so those tests never contend
// with the CPU-heavy pure unit tests (BCrypt, JWT) that otherwise run in parallel collections —
// keeping the integration tests deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
