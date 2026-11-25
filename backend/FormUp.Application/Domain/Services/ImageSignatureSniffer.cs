namespace FormUp.Application.Domain.Services;

/// <summary>
/// Magic-byte detection for the raster image types accepted from pre-signed uploads (jpeg, png,
/// webp). Shared by the chat-image and questionnaire-file flows, which both promote a staged object
/// only after its real content type is known — a pre-signed PUT binds neither type nor length.
/// </summary>
public static class ImageSignatureSniffer
{
    /// <summary>
    /// Detects the image's real content type from its magic-byte signature, ignoring whatever
    /// content type the client declared when the upload URL was minted. Returns null when the bytes
    /// match none of the three allowed signatures (JPEG <c>FF D8 FF</c>, PNG
    /// <c>89 50 4E 47 0D 0A 1A 0A</c>, WebP <c>RIFF....WEBP</c>) — including a spoofed or disguised
    /// file such as an HTML/SVG/PDF body renamed to <c>.jpg</c>.
    /// </summary>
    public static string? SniffContentType(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return "image/png";
        }

        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>Maps a sniffed content type (see <see cref="SniffContentType"/>) to its stored file extension.</summary>
    public static string ExtensionFor(string sniffedContentType) => sniffedContentType switch
    {
        "image/jpeg" => "jpg",
        "image/png"  => "png",
        "image/webp" => "webp",
        _            => throw new ArgumentOutOfRangeException(nameof(sniffedContentType), sniffedContentType, null),
    };
}
