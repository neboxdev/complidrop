namespace CompliDrop.Api.Services;

public interface IFileValidationService
{
    FileValidationResult Validate(Stream content, string declaredContentType, string? fileName);
}

public record FileValidationResult(bool IsValid, string? DetectedContentType, string? ErrorCode, string? ErrorMessage);

public class FileValidationService : IFileValidationService
{
    private const int MaxBytes = 10 * 1024 * 1024;

    public FileValidationResult Validate(Stream content, string declaredContentType, string? fileName)
    {
        if (!content.CanSeek)
            return new(false, null, "document.unsupported_format", "Upload stream must be seekable.");

        if (content.Length > MaxBytes)
            return new(false, null, "document.too_large", "File exceeds the 10 MB limit.");

        if (content.Length < 8)
            return new(false, null, "document.unsupported_format", "File is too small to be valid.");

        Span<byte> header = stackalloc byte[16];
        content.Position = 0;
        var read = content.Read(header);
        content.Position = 0;

        if (read < 4) return new(false, null, "document.unsupported_format", "Unable to read file header.");

        string? detected = null;
        // PDF: "%PDF-"
        if (header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46)
            detected = "application/pdf";
        // JPEG: FF D8 FF
        else if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            detected = "image/jpeg";
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        else if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            detected = "image/png";

        if (detected is null)
            return new(false, null, "document.unsupported_format",
                "Only PDF, JPEG, and PNG files are supported.");

        return new(true, detected, null, null);
    }
}
