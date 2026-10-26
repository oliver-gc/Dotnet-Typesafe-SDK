namespace TypesafeSdk.Models;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A PNG or JPEG image to send with a request. Place it in state, question instructions or criteria,
/// write it into text with <see cref="TextWithAssets.Create"/>, or pass it in <c>assets</c> under a name of your choice.
/// </summary>
/// <remarks>
/// Experimental: send the <c>TypeSafe-Danger-Zone-Images: true</c> header to opt in. The API detects the format
/// from the bytes and rejects other formats, such as WebP and GIF, with a 400.
/// </remarks>
[JsonConverter(typeof(ImageJsonConverter))]
public sealed class Image
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly byte[] JpegSignature = [0xff, 0xd8, 0xff];

    private readonly byte[] _data;
    private readonly string _reference = $"<image:{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}>";

    /// <summary>Copies <paramref name="data"/>, so later changes to the caller's buffer don't change what is sent.</summary>
    /// <exception cref="TypeSafeException"><paramref name="data"/> is empty.</exception>
    public Image(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new TypeSafeException("Image data is empty.");
        }

        _data = data.ToArray();
    }

    /// <summary>The image file bytes.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>image/png or image/jpeg from the file signature, else application/octet-stream.</summary>
    public string ContentType =>
        _data.AsSpan().StartsWith(PngSignature) ? "image/png"
        : _data.AsSpan().StartsWith(JpegSignature) ? "image/jpeg"
        : "application/octet-stream";

    /// <summary>Decodes base64 file bytes, without a <c>data:</c> URL prefix.</summary>
    /// <exception cref="TypeSafeException"><paramref name="text"/> isn't valid base64 or decodes to no bytes.</exception>
    public static Image FromBase64(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new TypeSafeException("Image data must contain valid base64 text.", ex);
        }

        return new Image(bytes);
    }

    public static Image FromFile(string path) => new(File.ReadAllBytes(path));

    public static async Task<Image> FromFileAsync(string path, CancellationToken ct = default) =>
        new(await File.ReadAllBytesAsync(path, ct));

    /// <summary>Reads <paramref name="stream"/> to its end.</summary>
    public static async Task<Image> FromStreamAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return new Image(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>The unique reference this image is sent under, such as <c>&lt;image:0f3c…&gt;</c>.</summary>
    public override string ToString() => _reference;
}

/// <summary>Text with images written into it; create it with <see cref="Create"/> or <see cref="Join"/>.</summary>
/// <remarks>
/// A plain interpolated string such as <c>$"Compare {photo}"</c> holds only the image's reference, not the image,
/// and is rejected before sending unless that image is attached some other way.
/// </remarks>
[JsonConverter(typeof(TextWithAssetsJsonConverter))]
public sealed class TextWithAssets
{
    internal TextWithAssets(string text, IReadOnlyList<Image> assets)
    {
        Text = text;
        Assets = assets;
    }

    /// <summary>The text, with each image written as its reference.</summary>
    public string Text { get; }

    /// <summary>The images in the text, including those in nested <see cref="TextWithAssets"/>.</summary>
    public IReadOnlyList<Image> Assets { get; }

    /// <summary>Interpolates images and other values into text, as in <c>TextWithAssets.Create($"Is {upload} the product in {catalog}?")</c>.</summary>
    public static TextWithAssets Create(TextWithAssetsHandler text) => text.Build();

    /// <summary>Joins values with a separator, as <c>string.Join</c> does, keeping the images among them.</summary>
    public static TextWithAssets Join(string separator, IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var handler = new TextWithAssetsHandler(0, 0);
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                handler.AppendLiteral(separator);
            }

            handler.AppendFormatted(value);
            first = false;
        }

        return handler.Build();
    }

    public override string ToString() => Text;
}

/// <summary>Builds a <see cref="TextWithAssets"/> from an interpolated string; not used directly.</summary>
[InterpolatedStringHandler]
public ref struct TextWithAssetsHandler
{
    private DefaultInterpolatedStringHandler _text;
    private List<Image>? _assets;

    public TextWithAssetsHandler(int literalLength, int formattedCount)
    {
        _text = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
    }

    public void AppendLiteral(string value) => _text.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);

    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, 0, format);

    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, null);

    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        switch (value)
        {
            case Image image:
                Track(image);
                _text.AppendFormatted(image.ToString(), alignment);
                break;
            case TextWithAssets text:
                foreach (var asset in text.Assets)
                {
                    Track(asset);
                }

                _text.AppendFormatted(text.Text, alignment);
                break;
            default:
                _text.AppendFormatted(value, alignment, format);
                break;
        }
    }

    internal TextWithAssets Build() => new(_text.ToStringAndClear(), _assets ?? []);

    private void Track(Image image)
    {
        _assets ??= [];
        if (!_assets.Contains(image))
        {
            _assets.Add(image);
        }
    }
}

// Outside a request, an image is written as its reference and text as itself; during one,
// RequestAssets also writes images under their names in assets and records which images are sent.
internal sealed class ImageJsonConverter : JsonConverter<Image>
{
    public override Image Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Images can't be read from JSON.");

    public override void Write(Utf8JsonWriter writer, Image value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RequestAssets.Current?.Write(value) ?? value.ToString());
}

internal sealed class TextWithAssetsJsonConverter : JsonConverter<TextWithAssets>
{
    public override TextWithAssets Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("TextWithAssets can't be read from JSON.");

    public override void Write(Utf8JsonWriter writer, TextWithAssets value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RequestAssets.Current?.Write(value) ?? value.Text);
}
