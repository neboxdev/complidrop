using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Services;
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
                // Stripe webhook signature verification + plan resolution (SecretKey left unset,
                // so checkout/portal stay disabled — only the webhook path is exercised).
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
            // Stop the DB-polling workers from running during tests.
            var workers = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                    && (d.ImplementationType == typeof(ExtractionWorker)
                        || d.ImplementationType == typeof(ReminderBackgroundService)))
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
        });
    }
}
