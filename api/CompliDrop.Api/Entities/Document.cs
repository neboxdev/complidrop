namespace CompliDrop.Api.Entities;

public enum ExtractionStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    ManualRequired
}

public enum ComplianceStatus
{
    Pending,
    Compliant,
    NonCompliant,
    ExpiringSoon,
    Expired
}

public class Document
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? VendorId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string BlobStorageUrl { get; set; } = string.Empty;
    public string? BlobStoragePath { get; set; }
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;

    // Document classification
    public string DocumentType { get; set; } = "other";
    public string? DocumentSubType { get; set; }

    // Extraction results
    public ExtractionStatus ExtractionStatus { get; set; } = ExtractionStatus.Pending;
    public double? ExtractionConfidence { get; set; }
    public string? ExtractionRawJson { get; set; }
    public DateTime? ExtractionCompletedAt { get; set; }

    // Key extracted dates
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }

    // Compliance status
    public ComplianceStatus ComplianceStatus { get; set; } = ComplianceStatus.Pending;

    // Computed (not persisted)
    public bool IsExpired => ExpirationDate.HasValue && ExpirationDate.Value.Date < DateTime.UtcNow.Date;
    public int? DaysUntilExpiry => ExpirationDate.HasValue
        ? (int)(ExpirationDate.Value.Date - DateTime.UtcNow.Date).TotalDays
        : null;

    // Metadata
    public string? UploadedBy { get; set; }
    public bool IsManuallyVerified { get; set; } = false;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organization Organization { get; set; } = null!;
    public Vendor? Vendor { get; set; }
    public ICollection<DocumentField> Fields { get; set; } = [];
    public ICollection<ComplianceCheck> ComplianceChecks { get; set; } = [];
}

public class DocumentField
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? FieldValue { get; set; }
    public string? FieldType { get; set; }
    public double Confidence { get; set; }
    public bool IsManuallyEdited { get; set; } = false;
    public string? OriginalValue { get; set; }

    // Navigation
    public Document Document { get; set; } = null!;
}
