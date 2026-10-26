namespace TypesafeSdk;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypesafeSdk.Models;

/// <summary>
/// Tracks the images in one request body while it is serialized: images listed in <c>assets</c> are written
/// as their names, others as their references, and every image whose reference ends up in the JSON is sent.
/// </summary>
internal sealed partial class RequestAssets
{
    // Serialization is synchronous, so the request being serialized is the one on this thread.
    [ThreadStatic]
    private static RequestAssets? t_current;

    private readonly IReadOnlyDictionary<string, Image> _named;
    private readonly Dictionary<Image, string> _names = [];
    private readonly Dictionary<string, Image> _known = [];

    private RequestAssets(IReadOnlyDictionary<string, Image>? named)
    {
        _named = named ?? new Dictionary<string, Image>();
        foreach (var (name, image) in _named)
        {
            ArgumentNullException.ThrowIfNull(image, $"assets[\"{name}\"]");

            // An image under several names is written as the last one.
            _names[image] = name;

            // Its reference may still appear in plain text; it is then sent under that reference too.
            _known[image.ToString()] = image;
        }
    }

    internal static RequestAssets? Current => t_current;

    /// <summary>Runs <paramref name="serialize"/> with image tracking, then returns the images to send in order.</summary>
    /// <exception cref="TypeSafeException">
    /// A name in <paramref name="named"/> isn't in the JSON, or the JSON holds the reference of an image that isn't attached.
    /// </exception>
    public static (string Json, IReadOnlyList<KeyValuePair<string, Image>> Sent) Serialize(
        IReadOnlyDictionary<string, Image>? named, JavaScriptEncoder encoder, Func<string> serialize)
    {
        var assets = new RequestAssets(named);
        var previous = t_current;
        t_current = assets;
        string json;
        try
        {
            json = serialize();
        }
        finally
        {
            t_current = previous;
        }

        return (json, assets.Collect(json, encoder));
    }

    internal string Write(Image image)
    {
        if (_names.TryGetValue(image, out var name))
        {
            return name;
        }

        _known[image.ToString()] = image;
        return image.ToString();
    }

    internal string Write(TextWithAssets text)
    {
        var written = text.Text;
        foreach (var image in text.Assets)
        {
            if (_names.TryGetValue(image, out var name))
            {
                written = written.Replace(image.ToString(), name, StringComparison.Ordinal);
            }
            else
            {
                _known[image.ToString()] = image;
            }
        }

        return written;
    }

    private List<KeyValuePair<string, Image>> Collect(string json, JavaScriptEncoder encoder)
    {
        // Names are matched as they appear in the JSON, with quotes and backslashes escaped.
        var unreferenced = _named.Keys.Where(name => !json.Contains(JsonEncodedText.Encode(name, encoder).Value, StringComparison.Ordinal)).ToList();
        if (unreferenced.Count > 0)
        {
            throw new TypeSafeException(
                $"Assets not referenced in the request: {string.Join(", ", unreferenced)}. "
                + "Write each name in assets into state, question instructions, or criteria.");
        }

        var sent = new List<KeyValuePair<string, Image>>(_named);
        var seen = new HashSet<string>(_named.Keys);
        var missing = new List<string>();
        foreach (Match match in ReferencePattern().Matches(json))
        {
            if (!seen.Add(match.Value))
            {
                continue;
            }

            if (_known.TryGetValue(match.Value, out var image))
            {
                sent.Add(new(match.Value, image));
            }
            else
            {
                missing.Add(match.Value);
            }
        }

        if (missing.Count > 0)
        {
            throw new TypeSafeException(
                $"Image references without an attached image: {string.Join(", ", missing)}. "
                + "Use TextWithAssets.Create for interpolated strings that contain images.");
        }

        return sent;
    }

    /// <summary>
    /// A multipart/form-data body (RFC 7578): the JSON payload as the <c>request</c> part, then each image's bytes
    /// in the part its entry in the payload's <c>assets</c> names.
    /// </summary>
    public static (byte[] Body, string ContentType) EncodeMultipart(string json, IReadOnlyList<(string Part, Image Image)> parts)
    {
        // A random boundary is practically certain not to occur in the content.
        var boundary = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        using var body = new MemoryStream();

        void WritePart(string disposition, string contentType, ReadOnlySpan<byte> content)
        {
            body.Write(Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Disposition: form-data; {disposition}\r\nContent-Type: {contentType}\r\n\r\n"));
            body.Write(content);
            body.Write("\r\n"u8);
        }

        WritePart("name=\"request\"", "application/json", Encoding.UTF8.GetBytes(json));
        foreach (var (part, image) in parts)
        {
            WritePart($"name=\"{part}\"; filename=\"{part}\"", image.ContentType, image.Data.Span);
        }

        body.Write(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"));
        return (body.ToArray(), $"multipart/form-data; boundary={boundary}");
    }

    [GeneratedRegex("<image:[0-9a-f]{32}>")]
    private static partial Regex ReferencePattern();
}
