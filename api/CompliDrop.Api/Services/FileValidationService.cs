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
        // ReadAtLeast (not a single Read) so a non-MemoryStream caller that returns a short read can't
        // make a genuine HEIC look truncated (read < 12) and get wrongly rejected.
        var read = content.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
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
        // HEIF / HEIC (the iPhone "High Efficiency" camera default): an ISO-BMFF container whose
        // "ftyp" box sits at offset 4 with a HEIF-family major brand at offset 8. Detected by magic
        // bytes, never Content-Type (per CLAUDE.md). The upload path transcodes these to JPEG before
        // storage (Document AI OCR can't read HEIC; browsers outside Safari can't render it) — see #220
        // and ADR 0018.
        else if (read >= 12
            && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70 // "ftyp"
            && IsHeifBrand(header[8], header[9], header[10], header[11]))
        {
            detected = IsHeicBrand(header[8], header[9], header[10], header[11]) ? "image/heic" : "image/heif";
        }

        if (detected is null)
            return new(false, null, "document.unsupported_format",
                "Only PDF, JPEG, PNG, and HEIC/HEIF files are supported.");

        return new(true, detected, null, null);
    }

    // HEVC-coded HEIC brands → reported as image/heic; the broader HEIF set iOS can also emit
    // (still-image and sequence) → image/heif. Both are transcoded to JPEG on ingest, so the exact
    // label only affects the recorded source type, not the handling.
    private static readonly HashSet<string> HeicBrands =
        new(StringComparer.Ordinal) { "heic", "heix", "hevc", "hevx" };

    private static readonly HashSet<string> HeifBrands =
        new(StringComparer.Ordinal) { "heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1", "mif2" };

    private static bool IsHeicBrand(byte a, byte b, byte c, byte d) => HeicBrands.Contains(Brand(a, b, c, d));
    private static bool IsHeifBrand(byte a, byte b, byte c, byte d) => HeifBrands.Contains(Brand(a, b, c, d));
    private static string Brand(byte a, byte b, byte c, byte d) => new([(char)a, (char)b, (char)c, (char)d]);
}
