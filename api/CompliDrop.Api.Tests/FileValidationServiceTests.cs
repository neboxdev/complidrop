using CompliDrop.Api.Services;
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
}
