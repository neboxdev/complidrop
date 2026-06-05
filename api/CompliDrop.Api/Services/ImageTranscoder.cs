using ImageMagick;

namespace CompliDrop.Api.Services;

/// <summary>Raised when a HEIC/HEIF upload can't be decoded and re-encoded to JPEG.</summary>
public sealed class ImageTranscodeException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IImageTranscoder
{
    /// <summary>
    /// True when <paramref name="contentType"/> is a format the rest of the pipeline can't consume
    /// directly — HEIC/HEIF, which Document AI OCR rejects and browsers (outside Safari) can't render —
    /// and which we therefore normalize to JPEG on ingest. See ADR 0018.
    /// </summary>
    bool NeedsTranscodeToJpeg(string contentType);

    /// <summary>
    /// Decodes HEIC/HEIF bytes and re-encodes them as JPEG (EXIF orientation baked in, metadata
    /// stripped). Throws <see cref="ImageTranscodeException"/> on any decode/encode failure so the
    /// caller can return a clean 400 rather than storing an unreadable file.
    /// </summary>
    byte[] ToJpeg(byte[] source);
}

public sealed class MagickImageTranscoder(ILogger<MagickImageTranscoder> logger) : IImageTranscoder
{
    // The content types FileValidationService assigns to a HEIC/HEIF magic-byte match. Kept in sync
    // with the brands detected there.
    private static readonly HashSet<string> TranscodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/heic", "image/heif"
    };

    // JPEG quality for the transcoded copy: high enough that fine print on a COI photo stays legible
    // for OCR, low enough that the stored blob isn't bloated. 82 is the usual "visually lossless" point.
    private const uint JpegQuality = 82;

    public bool NeedsTranscodeToJpeg(string contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && TranscodeTypes.Contains(contentType.Trim());

    public byte[] ToJpeg(byte[] source)
    {
        try
        {
            using var image = new MagickImage(source);
            // iPhones store the photo upright + an EXIF orientation tag rather than rotating pixels.
            // Bake the rotation in before stripping metadata, or the JPEG would render sideways.
            image.AutoOrient();
            image.Format = MagickFormat.Jpeg;
            image.Quality = JpegQuality;
            // Drop EXIF/GPS so a vendor's photo location never lands in our blob store.
            image.Strip();
            return image.ToByteArray();
        }
        catch (Exception ex) when (ex is not ImageTranscodeException)
        {
            logger.LogWarning(ex, "HEIC/HEIF transcode to JPEG failed.");
            throw new ImageTranscodeException("Could not decode the uploaded image.", ex);
        }
    }
}

public static class ImageTranscoderExtensions
{
    /// <summary>The 400 copy when a HEIC/HEIF upload can't be decoded — shared by both upload endpoints.</summary>
    public const string UnreadableImageMessage = "We couldn't read that photo. Please upload a PDF, JPEG, or PNG.";

    /// <summary>
    /// Normalizes already-validated upload bytes for storage: HEIC/HEIF is decoded + re-encoded to
    /// JPEG, every other (already-supported) type passes through unchanged. Returns
    /// <c>(null, "")</c> when a HEIC/HEIF can't be decoded, which the caller maps to a 400. (#220)
    /// </summary>
    public static (byte[]? Content, string ContentType) NormalizeForStorage(
        this IImageTranscoder transcoder, byte[] content, string detectedContentType)
    {
        if (!transcoder.NeedsTranscodeToJpeg(detectedContentType))
            return (content, detectedContentType);
        try
        {
            return (transcoder.ToJpeg(content), "image/jpeg");
        }
        catch (ImageTranscodeException)
        {
            return (null, "");
        }
    }
}
