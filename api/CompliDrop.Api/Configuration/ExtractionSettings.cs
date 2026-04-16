namespace CompliDrop.Api.Configuration;

public class ExtractionSettings
{
    public string Provider { get; set; } = "gemini";
}

public class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.5-flash";
    public int MaxTokens { get; set; } = 2000;
    public string Location { get; set; } = "us-central1";
    public string Endpoint { get; set; } = "vertex"; // "vertex" | "aistudio"
}

public class AnthropicSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; set; } = 2000;
}

public class DocumentAiSettings
{
    public bool Enabled { get; set; } = true;
    public string ProjectId { get; set; } = string.Empty;
    public string Location { get; set; } = "us";
    public string ProcessorId { get; set; } = string.Empty;
    public string? CredentialsPath { get; set; }
    public string? CredentialsJson { get; set; }
}
