using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Unit tests for the #220 HEIC/HEIF -> JPEG transcode (<see cref="MagickImageTranscoder"/>) and the
/// <see cref="ImageTranscoderExtensions.NormalizeForStorage"/> ingest helper. The real-fixture decode
/// also serves as the cross-platform guard: it runs on Linux CI, proving the bundled Magick.NET
/// libheif delegate decodes HEIC there (and therefore in the Debian prod container).
/// </summary>
public class ImageTranscoderTests
{
    private readonly MagickImageTranscoder _sut = new(NullLogger<MagickImageTranscoder>.Instance);

    [Theory]
    [InlineData("image/heic", true)]
    [InlineData("image/heif", true)]
    [InlineData("IMAGE/HEIC", true)]   // case-insensitive
    [InlineData("image/jpeg", false)]
    [InlineData("application/pdf", false)]
    [InlineData("", false)]
    public void NeedsTranscodeToJpeg_matches_only_heic_and_heif(string contentType, bool expected) =>
        _sut.NeedsTranscodeToJpeg(contentType).Should().Be(expected);

    [Fact]
    public void ToJpeg_decodes_a_real_heic_into_a_valid_jpeg()
    {
        var jpeg = _sut.ToJpeg(UploadFixtures.HeicPhotoBytes());

        jpeg.Should().NotBeEmpty();
        jpeg[0].Should().Be(0xFF); // JPEG SOI
        jpeg[1].Should().Be(0xD8);
        jpeg[2].Should().Be(0xFF);

        // And it round-trips back through the decoder as a real, non-empty JPEG image.
        using var roundTrip = new MagickImage(jpeg);
        roundTrip.Format.Should().Be(MagickFormat.Jpeg);
        roundTrip.Width.Should().BeGreaterThan(0);
        roundTrip.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ToJpeg_strips_exif_and_gps_metadata()
    {
        // Privacy guarantee (ADR 0018): the source HEIC carries EXIF (orientation) + GPS; the output
        // JPEG must carry no EXIF profile, so a vendor's photo location never lands in our blob store.
        var jpeg = _sut.ToJpeg(UploadFixtures.OrientedHeicPhotoBytes());

        using var img = new MagickImage(jpeg);
        img.GetExifProfile().Should().BeNull();
    }

    [Fact]
    public void ToJpeg_yields_an_upright_jpeg_with_no_residual_orientation_metadata()
    {
        // The source is flagged for 90-degree display rotation + carries GPS. The output must be an
        // upright (portrait) JPEG with NO residual orientation tag, so no viewer re-rotates it. The
        // rotation itself comes from libheif (it bakes HEIC orientation into pixels on decode);
        // AutoOrient is defensive for any decode path that doesn't. The orientation assertion here
        // specifically guards Strip: drop Strip and the stale EXIF orientation (=6) survives into the
        // JPEG, so a re-read would report a non-TopLeft Orientation and this fails.
        var jpeg = _sut.ToJpeg(UploadFixtures.OrientedHeicPhotoBytes());

        using var img = new MagickImage(jpeg);
        img.Height.Should().BeGreaterThan(img.Width); // upright/portrait, not the raw landscape
        img.Orientation.Should().BeOneOf(OrientationType.Undefined, OrientationType.TopLeft);
    }

    [Fact]
    public void ToJpeg_rejects_an_oversized_image_before_decoding_it()
    {
        // Decompression-bomb guard: a real, well-formed 8000x8000 (64 MP) HEIC — over the ~50 MP
        // ceiling — must be rejected via the cheap MagickImageInfo header pre-check, NOT decoded into a
        // giant bitmap. The "too large" message binds this to the size guard specifically; and because
        // the fixture is a genuinely decodable HEIC, removing the guard would make ToJpeg transcode it
        // successfully (no throw) — so this test actually fails if the guard regresses.
        var act = () => _sut.ToJpeg(UploadFixtures.HugeHeicPhotoBytes());

        act.Should().Throw<ImageTranscodeException>().WithMessage("*too large*");
    }

    [Fact]
    public void ToJpeg_throws_ImageTranscodeException_on_undecodable_bytes()
    {
        // A valid HEIC magic-byte header but a garbage body the decoder can't read.
        var garbage = new byte[]
        {
            0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        };

        var act = () => _sut.ToJpeg(garbage);

        act.Should().Throw<ImageTranscodeException>();
    }

    [Fact]
    public void NormalizeForStorage_passes_non_heic_through_as_the_same_buffer()
    {
        using var buffer = new MemoryStream(UploadFixtures.PdfBytes());

        var (content, contentType) = _sut.NormalizeForStorage(buffer, "application/pdf");

        content.Should().BeSameAs(buffer); // no copy, no transcode — the same stream, rewound
        content!.Position.Should().Be(0);
        contentType.Should().Be("application/pdf");
    }

    [Theory]
    [InlineData("image/heic")]
    [InlineData("image/heif")] // the heif-branded path routes through the same pinned HEIC coder
    public void NormalizeForStorage_transcodes_heic_to_a_jpeg_stream(string detectedContentType)
    {
        using var buffer = new MemoryStream(UploadFixtures.HeicPhotoBytes());

        var (content, contentType) = _sut.NormalizeForStorage(buffer, detectedContentType);

        content.Should().NotBeNull();
        contentType.Should().Be("image/jpeg");
        content!.Position.Should().Be(0);
        var bytes = ((MemoryStream)content).ToArray();
        bytes[0].Should().Be(0xFF);
        bytes[1].Should().Be(0xD8);
    }

    [Fact]
    public void NormalizeForStorage_returns_null_when_a_heic_cannot_be_decoded()
    {
        var garbage = new byte[]
        {
            0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63,
            0x09, 0x09, 0x09, 0x09,
        };
        using var buffer = new MemoryStream(garbage);

        var (content, contentType) = _sut.NormalizeForStorage(buffer, "image/heic");

        content.Should().BeNull();
        contentType.Should().Be("");
    }

}
