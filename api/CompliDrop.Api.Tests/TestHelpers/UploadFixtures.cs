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
    /// <summary>
    /// %PDF header padded to 64 bytes — passes the validator's 8-byte minimum. Returns a fresh
    /// buffer per call so a test that mutates its bytes (e.g. corrupts a header to construct a
    /// bad fixture) can't poison subsequent tests that share the same magic-byte template.
    /// </summary>
    public static byte[] PdfBytes() => FileWith(0x25, 0x50, 0x44, 0x46);

    /// <summary>Plain text bytes ("hello wd") — matches no supported magic-byte signature. Fresh buffer per call (see <see cref="PdfBytes"/>).</summary>
    public static byte[] TextBytes() => FileWith(0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x77, 0x64);

    /// <summary>
    /// A real HEIC photo fixture (generated with pillow-heif, committed under <c>TestFixtures/</c>),
    /// used to exercise the #220 HEIC magic-byte validation + the Magick.NET transcode-to-JPEG path
    /// end-to-end. The test csproj copies <c>TestFixtures</c> next to the assembly, so it resolves via
    /// <see cref="AppContext.BaseDirectory"/> on any test host (incl. Linux CI). Fresh buffer per call.
    /// </summary>
    public static byte[] HeicPhotoBytes() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestFixtures", "sample-photo.heic"));

    /// <summary>Builds a 64-byte buffer prefixed with the given magic-byte header.</summary>
    public static byte[] FileWith(params byte[] header)
    {
        var buf = new byte[64];
        Array.Copy(header, buf, header.Length);
        return buf;
    }

    /// <summary>Builds a multipart/form-data body with a single "file" field.</summary>
    public static MultipartFormDataContent UploadForm(byte[] bytes, string fileName, string contentType)
        => UploadForm(bytes, fileName, contentType, null);

    /// <summary>
    /// Builds a multipart/form-data body with a "file" field plus any extra text fields (e.g.
    /// <c>vendorId</c> / <c>documentType</c>) — used to exercise the upload path that associates
    /// a document with a vendor + type at creation (#186).
    /// </summary>
    public static MultipartFormDataContent UploadForm(
        byte[] bytes, string fileName, string contentType, IReadOnlyDictionary<string, string>? fields)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        if (fields is not null)
            foreach (var (key, value) in fields)
                form.Add(new StringContent(value), key);
        return form;
    }
}
