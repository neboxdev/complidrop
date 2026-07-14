using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Services.Ocr;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Boots the real API host against a test Postgres database. Disables the DB-polling
/// background workers and supplies the minimum configuration the host needs to start
/// without any real secrets:
///   - a valid <c>Jwt:Secret</c> (>= 32 chars, required by ValidateOnStart),
///   - <c>Cookies:Secure=false</c> so auth cookies are stored/sent over http://localhost,
///   - <c>RateLimiting:Enabled=false</c> (the test server has no client IP to partition on).
/// External integrations (Stripe, Resend, Blob, Document AI, Gemini, Anthropic, Sentry)
/// are left unconfigured — they self-disable via their IsEnabled/IsConfigured gates.
/// </summary>
public sealed class CustomWebApplicationFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? configOverrides = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development env: Program.cs only calls UseHttpsRedirection() *outside* Development,
        // and an https redirect would 307 the plain-http test client.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Added last → wins over appsettings/env/user-secrets, so tests never touch a real DB.
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Jwt:Secret"] = "integration-test-signing-secret-key-0123456789",
                ["Jwt:Issuer"] = "complidrop-api-test",
                ["Jwt:Audience"] = "complidrop-frontend-test",
                ["Cookies:Secure"] = "false",
                ["Cookies:SameSite"] = "Lax",
                ["RateLimiting:Enabled"] = "false",
                // Template corrections ON in the test hosts (#416, ADR 0036 Amendment 3 — prod
                // default is OFF). Two reasons:
                //  1. The shared-fixture world runs the gated §4 corrected checklist set so every
                //     seed-dependent suite exercises the flag-ON behavior; the prod flag-OFF no-op
                //     posture is pinned explicitly by ComplianceTemplateSeedTests' flag-off
                //     merge-safety test and the isolated-DB TemplateCorrectionsFlagTests instead.
                //  2. Correctness of the SHARED test database: several tests boot a SECOND host
                //     against Fixture.ConnectionString and every boot re-runs the seed — a host
                //     booting flag-OFF would CONVERGE the shared system templates back to the
                //     legacy set mid-suite (the flag is reversible by design) and corrupt every
                //     later seed-dependent test. Only override this against an isolated database.
                ["TemplateCorrections:Enabled"] = "true",
                // Stripe webhook signature verification + plan resolution. SecretKey is
                // explicitly emptied — without this, a developer with `Stripe:SecretKey` in
                // user-secrets would have it leak into the test host (configuration ordering:
                // appsettings → user-secrets → env → in-memory, so we have to set it here
                // to win). The intent is for checkout/portal to be DISABLED in tests so
                // BillingCheckoutVocabTests can pin the IsEnabled gate behaviour without
                // accidentally hitting the live Stripe API.
                ["Stripe:SecretKey"] = "",
                ["Stripe:WebhookSecret"] = "whsec_test_secret_for_integration_tests",
                ["Stripe:MonthlyPriceId"] = "price_monthly_test",
                ["Stripe:AnnualPriceId"] = "price_annual_test",
                ["Stripe:FoundingPriceId"] = "price_founding_test",
                // Resend inbound-webhook (Svix) signature verification. The secret after the
                // "whsec_" prefix must be valid base64 — ResendWebhookTests signs with the same value.
                ["Resend:WebhookSecret"] = "whsec_Y29tcGxpZHJvcC1yZXNlbmQtd2ViaG9vay10ZXN0LXNlY3JldC0wMTIzNDU2Nzg5",
            });

            // Per-test overrides (added last → win). Lets a test unset a key, e.g. to exercise the
            // "no Resend:WebhookSecret configured" branch.
            if (configOverrides is not null)
                config.AddInMemoryCollection(configOverrides);
        });

        builder.ConfigureTestServices(services =>
        {
            // Stop the DB-polling workers from running during tests. ComplianceSweepBackgroundService
            // is included: left running it would sweep the shared test DB on startup and hourly,
            // racing other tests and risking silent vacuity in the on-read-overlay tests (a sweep
            // could persist the derived status into the stored column, so those tests would pass off
            // the swept value even if the overlay were reverted). ComplianceSweepBackgroundServiceTests
            // drives SweepAsync directly with a fixed clock instead.
            var workers = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                    && (d.ImplementationType == typeof(ExtractionWorker)
                        || d.ImplementationType == typeof(ReminderBackgroundService)
                        || d.ImplementationType == typeof(ComplianceSweepBackgroundService)))
                .ToList();
            foreach (var descriptor in workers)
                services.Remove(descriptor);

            // Replace Azure Blob storage with an in-memory fake — the real BlobStorageService
            // connects to Azure in its constructor, which has no credentials in the test host.
            services.RemoveAll<IBlobStorageService>();
            services.AddSingleton<IBlobStorageService, FakeBlobStorageService>();

            // Replace the Resend-backed email service with an in-memory fake. The real service
            // self-disables when ApiKey/FromEmail are unset (which is the test default), so the
            // reminder worker would short-circuit before sending. The fake reports IsEnabled=true
            // and captures every send so tests can assert recipients, subjects, and bodies.
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService, FakeEmailService>();

            // Replace the OCR + LLM extraction boundary with controllable in-memory fakes. The real
            // DocumentAiOcrService / Gemini / Anthropic clients make outbound HTTP calls, so the
            // ExtractionWorker tests swap these in to make extraction deterministically succeed or
            // throw. Registered both as the concrete type (for tests to grab a handle and toggle
            // knobs) and as the interface (so the worker resolves the same singleton). The real
            // ExtractionClientFactory is left in place — with a single registered client it returns
            // the fake.
            services.RemoveAll<IOcrService>();
            var fakeOcr = new FakeOcrService();
            services.AddSingleton(fakeOcr);
            services.AddSingleton<IOcrService>(fakeOcr);

            services.RemoveAll<IExtractionClient>();
            var fakeExtraction = new FakeExtractionClient();
            services.AddSingleton(fakeExtraction);
            services.AddSingleton<IExtractionClient>(fakeExtraction);

            // Replace IStripeService with FakeStripeService so checkout / portal endpoints
            // can be exercised end-to-end (200 + sessionUrl + captured priceId) without
            // a live Stripe call (#147, ADR 0011). The fake DELEGATES HandleWebhookEventAsync
            // to the real StripeService — so StripeWebhookTests still exercises the genuine
            // signature-verification → ResolvePlanFromPriceId path. The real StripeService is
            // re-registered as the concrete type so the fake can resolve it via DI.
            services.RemoveAll<IStripeService>();
            services.AddScoped<StripeService>();
            services.AddSingleton<IStripeService, FakeStripeService>();
        });
    }
}
