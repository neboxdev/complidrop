using CompliDrop.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services.Extraction;

public class ExtractionClientFactory(
    IOptions<ExtractionSettings> settings,
    IEnumerable<IExtractionClient> clients) : IExtractionClientFactory
{
    public IExtractionClient Get()
    {
        var preferred = settings.Value.Provider?.Trim().ToLowerInvariant() ?? "gemini";
        var target = preferred switch
        {
            "anthropic" => ExtractionProvider.Anthropic,
            _ => ExtractionProvider.Gemini
        };
        return clients.FirstOrDefault(c => c.Provider == target)
            ?? clients.First();
    }
}
