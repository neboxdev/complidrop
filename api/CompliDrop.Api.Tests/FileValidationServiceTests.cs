using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>Pure unit tests for magic-byte + size validation (<see cref="FileValidationService"/>).</summary>
public class FileValidationServiceTests
{
    private readonly FileValidationService _validator = new();

    private static MemoryStream WithHeader(params byte[] header)
    {
        var buf = new byte[Math.Max(header.Length, 16)]; // pad past the 8-byte minimum
        Array.Copy(header, buf, header.Length);
        return new MemoryStream(buf);
    }

    [Fact]
    public void Accepts_pdf_by_magic_bytes()
    {
        var r = _validator.Validate(WithHeader(0x25, 0x50, 0x44, 0x46), "application/octet-stream", "x.pdf");
        r.IsValid.Should().BeTrue();
        r.DetectedContentType.Should().Be("application/pdf");
    }

    [Fact]
    public void Accepts_jpeg_by_magic_bytes()
    {
        var r = _validator.Validate(WithHeader(0xFF, 0xD8, 0xFF, 0xE0), "image/jpeg", "x.jpg");
        r.IsValid.Should().BeTrue();
        r.DetectedContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public void Accepts_png_by_magic_bytes()
    {
        var r = _validator.Validate(WithHeader(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), "image/png", "x.png");
        r.IsValid.Should().BeTrue();
        r.DetectedContentType.Should().Be("image/png");
    }

    [Fact]
    public void Rejects_unsupported_bytes_even_when_content_type_claims_pdf()
    {
        // Plain text bytes with a .pdf name and application/pdf Content-Type — the lie doesn't help.
        var r = _validator.Validate(WithHeader(0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x77, 0x64), "application/pdf", "evil.pdf");
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("document.unsupported_format");
    }

    [Fact]
    public void Rejects_file_over_10mb()
    {
        var big = new MemoryStream(new byte[10 * 1024 * 1024 + 1]);
        big.Write([0x25, 0x50, 0x44, 0x46], 0, 4); // valid PDF header — size check still fires first
        var r = _validator.Validate(big, "application/pdf", "big.pdf");
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("document.too_large");
    }

    [Fact]
    public void Rejects_file_too_small()
    {
        var r = _validator.Validate(new MemoryStream([0x25, 0x50, 0x44]), "application/pdf", "tiny.pdf");
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("document.unsupported_format");
    }

    // ---- #220: HEIC / HEIF (iPhone "High Efficiency" photos) ----

    [Fact]
    public void Accepts_a_real_heic_photo_by_magic_bytes()
    {
        var r = _validator.Validate(new MemoryStream(UploadFixtures.HeicPhotoBytes()), "application/octet-stream", "coi.heic");
        r.IsValid.Should().BeTrue();
        r.DetectedContentType.Should().Be("image/heic");
    }

    [Theory]
    [InlineData("heic", "image/heic")]
    [InlineData("heix", "image/heic")]
    [InlineData("hevc", "image/heic")]
    [InlineData("mif1", "image/heif")]
    [InlineData("msf1", "image/heif")]
    public void Accepts_each_heif_family_brand(string brand, string expectedType)
    {
        var r = _validator.Validate(Ftyp(brand), "application/octet-stream", $"x.{brand}");
        r.IsValid.Should().BeTrue();
        r.DetectedContentType.Should().Be(expectedType);
    }

    [Fact]
    public void Rejects_an_ftyp_box_whose_brand_is_not_a_heif_family()
    {
        // An MP4/QuickTime container also opens with an "ftyp" box (brand "isom"/"mp42"), so the
        // brand check must gate acceptance — a video must not be stored as an image.
        var r = _validator.Validate(Ftyp("isom"), "video/mp4", "movie.mp4");
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("document.unsupported_format");
    }

    [Fact]
    public void Rejects_a_heic_content_type_on_non_heic_bytes()
    {
        // Magic bytes win over the declared Content-Type: a spoofed "image/heic" header on plain text.
        var r = _validator.Validate(WithHeader(0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x77, 0x64), "image/heic", "fake.heic");
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_truncated_ftyp_header_too_short_to_read_the_brand()
    {
        // 11 bytes: an "ftyp" box but one byte short of the 12 needed to read the brand at offset 8-11.
        // The `read >= 12` guard must make this fall through to unsupported, not misread a partial brand.
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69 };
        var r = _validator.Validate(new MemoryStream(bytes), "image/heic", "x.heic");
        r.IsValid.Should().BeFalse();
        r.ErrorCode.Should().Be("document.unsupported_format");
    }

    // A minimal ISO-BMFF "ftyp" box: [size=0x18]["ftyp"][major brand], padded by WithHeader.
    private static MemoryStream Ftyp(string brand) => WithHeader(
        0x00, 0x00, 0x00, 0x18,
        0x66, 0x74, 0x79, 0x70, // "ftyp"
        (byte)brand[0], (byte)brand[1], (byte)brand[2], (byte)brand[3]);
}
