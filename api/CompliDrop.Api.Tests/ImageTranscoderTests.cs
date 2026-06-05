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
    public void NormalizeForStorage_passes_non_heic_through_unchanged()
    {
        var pdf = UploadFixtures.PdfBytes();

        var (content, contentType) = _sut.NormalizeForStorage(pdf, "application/pdf");

        content.Should().BeSameAs(pdf); // no copy, no transcode
        contentType.Should().Be("application/pdf");
    }

    [Fact]
    public void NormalizeForStorage_transcodes_heic_to_jpeg()
    {
        var (content, contentType) = _sut.NormalizeForStorage(UploadFixtures.HeicPhotoBytes(), "image/heic");

        content.Should().NotBeNull();
        contentType.Should().Be("image/jpeg");
        content![0].Should().Be(0xFF);
        content[1].Should().Be(0xD8);
    }

    [Fact]
    public void NormalizeForStorage_returns_null_when_a_heic_cannot_be_decoded()
    {
        var garbage = new byte[]
        {
            0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63,
            0x09, 0x09, 0x09, 0x09,
        };

        var (content, contentType) = _sut.NormalizeForStorage(garbage, "image/heic");

        content.Should().BeNull();
        contentType.Should().Be("");
    }
}
