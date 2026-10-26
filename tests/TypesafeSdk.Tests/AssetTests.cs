using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypesafeSdk.Models;

namespace TypesafeSdk.Tests;

public class AssetTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 9];

    private static IReadOnlyDictionary<string, Question> Ask(object? instructions) =>
        new Dictionary<string, Question> { ["q"] = new NoulQuestion { Instructions = instructions } };

    [Fact]
    public async Task ImageInState_IsSentAsMultipartPartUnderItsReference()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);
        var photo = new Image(Png);

        await client.SystemOneAsync(new { claim = "Chipped rim.", photo }, Ask("Damaged?"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("multipart/form-data", request.ContentType);
        var parts = MultipartParser.Parse(request);
        Assert.Equal(2, parts.Count);

        Assert.Equal("form-data; name=\"request\"", parts[0].Headers["Content-Disposition"]);
        Assert.Equal("application/json", parts[0].Headers["Content-Type"]);
        var payload = JsonNode.Parse(parts[0].Content)!;
        Assert.Equal(photo.ToString(), (string?)payload["state"]!["photo"]);
        JsonAssert.Equal($$"""{ "{{photo}}": { "type": "image", "part": "asset-0" } }""", payload["assets"]);

        Assert.Equal("form-data; name=\"asset-0\"; filename=\"asset-0\"", parts[1].Headers["Content-Disposition"]);
        Assert.Equal("image/png", parts[1].Headers["Content-Type"]);
        Assert.Equal(Png, parts[1].Content);
    }

    [Fact]
    public async Task TextWithAssets_WritesReferencesIntoTextAndSendsEachImageOnce()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);
        var upload = new Image(Png);
        var catalog = new Image(Jpeg);

        await client.SystemOneAsync(
            TextWithAssets.Create($"A seller uploaded {upload} for a listing whose photo is {catalog}."),
            Ask(TextWithAssets.Create($"Does {upload} show the product in {catalog}?")));

        var parts = MultipartParser.Parse(handler.Requests[0]);
        var payload = JsonNode.Parse(parts[0].Content)!;
        Assert.Equal($"A seller uploaded {upload} for a listing whose photo is {catalog}.", (string?)payload["state"]);
        Assert.Equal($"Does {upload} show the product in {catalog}?", (string?)payload["questions"]!["q"]!["instructions"]);
        JsonAssert.Equal(
            $$"""{ "{{upload}}": { "type": "image", "part": "asset-0" }, "{{catalog}}": { "type": "image", "part": "asset-1" } }""",
            payload["assets"]);
        Assert.Equal(3, parts.Count);
        Assert.Equal(Png, parts[1].Content);
        Assert.Equal("image/jpeg", parts[2].Headers["Content-Type"]);
        Assert.Equal(Jpeg, parts[2].Content);
    }

    [Fact]
    public async Task NamedAssets_AreWrittenAsTheirNamesWherePlaced()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);
        var delivered = new Image(Png);
        var catalog = new Image(Jpeg);

        await client.SystemOneAsync(
            "A customer returned <delivered>.",
            new Dictionary<string, Question>
            {
                ["matches"] = new NoulQuestion { Instructions = TextWithAssets.Create($"Is <delivered> the product in {catalog}?") },
                ["photo"] = new BoxQuestion { Instructions = delivered },
            },
            assets: new Dictionary<string, Image> { ["<delivered>"] = delivered, ["<catalog>"] = catalog });

        var payload = JsonNode.Parse(MultipartParser.Parse(handler.Requests[0])[0].Content)!;
        Assert.Equal("A customer returned <delivered>.", (string?)payload["state"]);
        Assert.Equal("Is <delivered> the product in <catalog>?", (string?)payload["questions"]!["matches"]!["instructions"]);
        Assert.Equal("<delivered>", (string?)payload["questions"]!["photo"]!["instructions"]);
        JsonAssert.Equal(
            """{ "<delivered>": { "type": "image", "part": "asset-0" }, "<catalog>": { "type": "image", "part": "asset-1" } }""",
            payload["assets"]);
    }

    [Fact]
    public async Task UnreferencedAssetName_ThrowsBeforeSending()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        var error = await Assert.ThrowsAsync<TypeSafeException>(() => client.SystemOneAsync(
            "No names here.", Ask("?"), assets: new Dictionary<string, Image> { ["<photo>"] = new(Png) }));

        Assert.Contains("<photo>", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PlainInterpolatedImage_ThrowsBeforeSending()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);
        var photo = new Image(Png);

        var error = await Assert.ThrowsAsync<TypeSafeException>(() => client.SystemOneAsync($"Compare {photo}", Ask("?")));

        Assert.Contains(photo.ToString(), error.Message);
        Assert.Contains("TextWithAssets.Create", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PlainInterpolatedImage_IsAllowedWhenTheImageIsAlsoAttached()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);
        var photo = new Image(Png);

        await client.SystemOneAsync(new object[] { photo, $"Compare {photo}" }, Ask("?"));

        Assert.Equal(2, MultipartParser.Parse(handler.Requests[0]).Count);
    }

    [Fact]
    public async Task RequestsWithoutImages_StayJson()
    {
        var handler = new StubHandler(_ => Reply.Ok());
        using var client = TestClient.Create(handler);

        await client.SystemOneAsync("<b>bold</b>", Ask("?"));

        Assert.Equal("application/json", handler.Requests[0].ContentType);
        Assert.Null(handler.Requests[0].Json["assets"]);
        Assert.Contains("\"<b>bold</b>\"", handler.Requests[0].Body);
    }

    [Fact]
    public void Join_KeepsImagesAndFormatsOtherValues()
    {
        var a = new Image(Png);
        var b = new Image(Jpeg);

        var joined = TextWithAssets.Join(", ", [a, 2, b, a]);

        Assert.Equal($"{a}, 2, {b}, {a}", joined.Text);
        Assert.Equal(new[] { a, b }, joined.Assets);
    }

    [Fact]
    public void Create_SupportsNestingFormatsAndAlignment()
    {
        var a = new Image(Png);
        var b = new Image(Jpeg);
        var inner = TextWithAssets.Create($"{a} and {b}");

        var text = TextWithAssets.Create($"[{7,3}] {255:X} {inner}");

        Assert.Equal($"[  7] FF {a} and {b}", text.Text);
        Assert.Equal(new[] { a, b }, text.Assets);
        Assert.Equal(text.Text, text.ToString());
    }

    [Fact]
    public void Image_CopiesDataAndDetectsContentType()
    {
        var data = (byte[])Png.Clone();
        var image = new Image(data);
        data[0] = 0;

        Assert.Equal(Png, image.Data.ToArray());
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal("image/jpeg", new Image(Jpeg).ContentType);
        Assert.Equal("application/octet-stream", new Image("GIF89a"u8).ContentType);
        Assert.Matches("^<image:[0-9a-f]{32}>$", image.ToString());
        Assert.NotEqual(image.ToString(), new Image(Png).ToString());
    }

    [Fact]
    public async Task Image_FactoriesReadBase64FilesAndStreams()
    {
        Assert.Equal(Png, Image.FromBase64(Convert.ToBase64String(Png)).Data.ToArray());
        Assert.Equal(Png, (await Image.FromStreamAsync(new MemoryStream(Png))).Data.ToArray());

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, Jpeg);
            Assert.Equal(Jpeg, Image.FromFile(path).Data.ToArray());
            Assert.Equal(Jpeg, (await Image.FromFileAsync(path)).Data.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Image_RejectsEmptyOrInvalidData()
    {
        Assert.Equal("Image data is empty.", Assert.Throws<TypeSafeException>(() => new Image([])).Message);
        Assert.Throws<TypeSafeException>(() => Image.FromBase64(""));
        Assert.IsType<FormatException>(Assert.Throws<TypeSafeException>(() => Image.FromBase64("not base64!")).InnerException);
    }

    [Fact]
    public void OutsideARequest_ImagesSerializeAsReferences()
    {
        var image = new Image(Png);
        var text = TextWithAssets.Create($"See {image}");

        Assert.Equal($"\"{image}\"", JsonSerializer.Serialize(image).Replace("\\u003C", "<").Replace("\\u003E", ">"));
        Assert.Equal($"See {image}", JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(text)));
    }
}

internal static class MultipartParser
{
    public sealed record Part(Dictionary<string, string> Headers, byte[] Content);

    public static List<Part> Parse(RecordedRequest request)
    {
        var boundary = request.ContentHeaders!.ContentType!.Parameters.Single(p => p.Name == "boundary").Value!;
        var body = request.Bytes;
        var delimiter = Encoding.ASCII.GetBytes($"--{boundary}");
        var parts = new List<Part>();

        var start = IndexOf(body, delimiter, 0) + delimiter.Length;
        while (!body.AsSpan(start).StartsWith("--"u8))
        {
            start += 2; // CRLF after the delimiter
            var headersEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), start);
            var headers = Encoding.ASCII.GetString(body, start, headersEnd - start)
                .Split("\r\n")
                .Select(line => line.Split(": ", 2))
                .ToDictionary(pair => pair[0], pair => pair[1]);
            var contentStart = headersEnd + 4;
            var next = IndexOf(body, delimiter, contentStart);
            parts.Add(new Part(headers, body[contentStart..(next - 2)]));
            start = next + delimiter.Length;
        }

        return parts;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        var index = haystack.AsSpan(from).IndexOf(needle);
        Assert.True(index >= 0, "Malformed multipart body.");
        return from + index;
    }
}
