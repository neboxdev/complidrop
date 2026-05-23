using System.Net.Http.Headers;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Shared helpers for endpoint tests that POST file uploads: a valid PDF magic-byte buffer, a
/// non-matching plain-text buffer, a generic header-padder, and a multipart/form-data builder
/// with a single "file" field. Centralised so the document and vendor portal upload tests stay
/// in sync on what a "valid PDF" or "spoofed content type" looks like.
/// </summary>
public static class UploadFixtures
{
    /// <summary>%PDF header padded to 64 bytes — passes the validator's 8-byte minimum.</summary>
    public static readonly byte[] PdfBytes = FileWith(0x25, 0x50, 0x44, 0x46);

    /// <summary>Plain text bytes ("hello wd") — matches no supported magic-byte signature.</summary>
    public static readonly byte[] TextBytes = FileWith(0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x77, 0x64);

    /// <summary>Builds a 64-byte buffer prefixed with the given magic-byte header.</summary>
    public static byte[] FileWith(params byte[] header)
    {
        var buf = new byte[64];
        Array.Copy(header, buf, header.Length);
        return buf;
    }

    /// <summary>Builds a multipart/form-data body with a single "file" field.</summary>
    public static MultipartFormDataContent UploadForm(byte[] bytes, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return form;
    }
}
