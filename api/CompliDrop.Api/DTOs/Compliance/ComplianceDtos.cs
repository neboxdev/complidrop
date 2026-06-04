namespace CompliDrop.Api.DTOs.Compliance;

public record TemplateSummary(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystemTemplate,
    int RuleCount,
    int VendorCount);

public record TemplateDetail(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystemTemplate,
    TemplateRule[] Rules);

public record TemplateRule(
    Guid Id,
    string DocumentType,
    string? FieldName,
    string Operator,
    string? ExpectedValue,
    string? ErrorMessage,
    int SortOrder);

public record CreateTemplateRequest(string Name, string? Description);

public record UpdateTemplateRequest(string Name, string? Description);

public record UpsertRuleRequest(
    Guid? Id,
    string DocumentType,
    string? FieldName,
    string Operator,
    string? ExpectedValue,
    string? ErrorMessage,
    int SortOrder);

public record ComplianceCheckDto(
    Guid Id,
    Guid ComplianceRuleId,
    string? RuleFieldName,
    string? RuleOperator,
    string? RuleExpectedValue,
    // The owner-authored plain-English requirement text (e.g. "General liability
    // must be at least $1,000,000"). Preferred over the machine Notes for the
    // document-detail "why isn't this compliant?" explanation. (#193)
    string? RuleErrorMessage,
    string? ActualValue,
    bool IsPassed,
    string? Notes,
    DateTime CheckedAt);
