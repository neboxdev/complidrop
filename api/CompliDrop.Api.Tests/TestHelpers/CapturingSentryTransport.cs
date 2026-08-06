using System.Text;
using System.Text.Json;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// A Sentry <see cref="ITransport"/> that keeps every envelope the SDK would have uploaded, serialized
/// to the exact bytes that would have gone over the wire (#386 / ADR 0053).
/// <para/>
/// Asserting on the SERIALIZED payload rather than on a <c>SentryEvent</c> object is deliberate: the
/// PII claims in this area are claims about what LEAVES the process, and an event object can carry a
/// value in a place a test forgot to look. A substring assertion over the whole envelope cannot.
/// </summary>
internal sealed class CapturingSentryTransport : ITransport
{
    private readonly List<string> _payloads = [];

    /// <summary>A snapshot — the SDK's background worker writes from its own thread.</summary>
    public IReadOnlyList<string> Payloads
    {
        get { lock (_payloads) return _payloads.ToList(); }
    }

    public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await envelope.SerializeAsync(buffer, null, cancellationToken);
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        lock (_payloads) _payloads.Add(text);
    }

    /// <summary>
    /// An envelope is newline-delimited: envelope header, then (item header, item payload) pairs. Pulls
    /// out the payload that follows the <c>"type":"event"</c> item header, and throws rather than
    /// returning a default so a shape change fails loudly instead of emptying every assertion.
    /// </summary>
    internal static JsonElement ParseEventItem(string payload)
    {
        var lines = payload.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
            if (lines[i].Contains("\"type\":\"event\"", StringComparison.Ordinal))
                return JsonDocument.Parse(lines[i + 1]).RootElement.Clone();

        throw new InvalidOperationException($"No event item in envelope: {payload}");
    }
}
