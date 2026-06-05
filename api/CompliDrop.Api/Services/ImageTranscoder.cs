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
    /// stripped). Throws <see cref="ImageTranscodeException"/> on any decode/encode failure — including
    /// an over-large (decompression-bomb) image — so the caller can return a clean 400 rather than
    /// storing an unreadable file or exhausting memory.
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

    /// <summary>
    /// Hard ceiling on decoded pixel count (~50 MP) — comfortably above any real phone camera (a
    /// 48 MP iPhone Pro photo is 48 MP) but far below a decompression bomb. HEIC/HEVC compresses so
    /// well that a sub-10 MB file (the Kestrel cap) can declare enormous dimensions; without this guard
    /// the decode would allocate the full bitmap and could OOM the public upload path. Enforced
    /// per-call (a cheap header read in <see cref="ToJpeg"/>) and process-wide via
    /// <see cref="ResourceLimits"/> (static ctor).
    /// </summary>
    public const long MaxPixels = 50_000_000;

    static MagickImageTranscoder()
    {
        // Defense-in-depth behind the per-call MaxPixels guard: cap ImageMagick's decode resources
        // process-wide so a malicious image can't exhaust host memory even on a path that skipped the
        // guard. Set once, before any decode. (#220 review — security/perf)
        ResourceLimits.Width = 50_000;
        ResourceLimits.Height = 50_000;
        ResourceLimits.Area = (ulong)MaxPixels;
    }

    public bool NeedsTranscodeToJpeg(string contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && TranscodeTypes.Contains(contentType.Trim());

    public byte[] ToJpeg(byte[] source)
    {
        try
        {
            // Reject a decompression bomb BEFORE allocating pixels: MagickImageInfo reads only the
            // header, so a small HEIC declaring enormous dimensions becomes a clean 400 instead of
            // OOMing the (public, untrusted) upload path. (#220 review — security/perf)
            var info = new MagickImageInfo(source);
            if ((long)info.Width * info.Height > MaxPixels)
                throw new ImageTranscodeException($"Image dimensions too large to process ({info.Width}x{info.Height}).");

            // Pin the input coder to HEIC so a crafted file that slipped past the ftyp magic-byte gate
            // can't steer ImageMagick into an unexpected delegate (SVG/MSL/URL/PS/...). libheif reads
            // every HEIF brand (heic/heif/mif1/...) through this one coder. (#220 review — security)
            using var image = new MagickImage(source, new MagickReadSettings { Format = MagickFormat.Heic });
            // iPhones flag rotation rather than rotating pixels; libheif applies it on decode, but
            // AutoOrient is the belt-and-suspenders for any path that doesn't — before Strip drops the
            // now-stale orientation tag along with EXIF/GPS (so no viewer re-rotates it, and no location
            // metadata lands in our blob store).
            image.AutoOrient();
            image.Format = MagickFormat.Jpeg;
            image.Quality = JpegQuality;
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
    /// Normalizes an already-validated upload <paramref name="buffer"/> for storage: HEIC/HEIF is
    /// decoded + re-encoded to JPEG (a new stream), every other (already-supported) type passes through
    /// as the SAME buffer (rewound, no copy). Returns a null <c>Content</c> when a HEIC/HEIF can't be
    /// decoded, which the caller maps to a 400. The returned stream is positioned at 0 and ready to
    /// upload; its <c>Length</c> is the byte count to record. (#220)
    /// </summary>
    public static (Stream? Content, string ContentType) NormalizeForStorage(
        this IImageTranscoder transcoder, MemoryStream buffer, string detectedContentType)
    {
        if (!transcoder.NeedsTranscodeToJpeg(detectedContentType))
        {
            buffer.Position = 0;
            return (buffer, detectedContentType);
        }
        try
        {
            // Only materialize a byte[] when we actually transcode; the passthrough path above streams
            // the existing buffer directly (no extra full-size copy on the common PDF/JPEG/PNG case).
            return (new MemoryStream(transcoder.ToJpeg(buffer.ToArray())), "image/jpeg");
        }
        catch (ImageTranscodeException)
        {
            return (null, "");
        }
    }
}
