using System.Text;
using PrivateChat.Api.Features.Blobs;

namespace PrivateChat.Api.Tests;

public class BlobRulesTests
{
    [Theory]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData(" Image/PNG ", "image/png")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    [InlineData("text/html; charset=utf-8", "application/octet-stream")]
    [InlineData("nope", "application/octet-stream")]
    [InlineData("a/", "application/octet-stream")]
    public void Normalize_only_accepts_plain_type_subtype(string? input, string expected)
        => Assert.Equal(expected, MimeRules.Normalize(input));

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/heic", true)]
    [InlineData("video/mp4", true)]
    [InlineData("audio/ogg", true)]
    [InlineData("application/pdf", true)]
    [InlineData("image/svg+xml", false)] // 可带脚本
    [InlineData("text/html", false)]
    [InlineData("application/javascript", false)]
    [InlineData("application/octet-stream", false)]
    public void Inline_only_for_media_and_pdf(string mime, bool inline)
        => Assert.Equal(inline, MimeRules.IsInlineSafe(mime));

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("  全家福 2026.heic ", "全家福 2026.heic")]
    [InlineData("a\"b\\c/d:e*f?g<h>i|j.txt", "abcdefghij.txt")]
    [InlineData("bad\r\nname.txt", "badname.txt")]
    [InlineData("", "fallback")]
    [InlineData("\"\"", "fallback")]
    public void SafeFileName_strips_dangerous_characters(string input, string expected)
        => Assert.Equal(expected, MimeRules.SafeFileName(input, "fallback"));

    [Fact]
    public void SafeFileName_truncates_long_names()
    {
        var name = new string('x', 500) + ".bin";
        Assert.Equal(180, MimeRules.SafeFileName(name, "f").Length);
    }

    [Fact]
    public async Task Sha256Hex_matches_known_vector()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        var hex = await BlobIngestService.Sha256HexAsync(ms, CancellationToken.None);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hex);
    }

    [Fact]
    public void All_variants_are_known()
    {
        Assert.Contains("thumb-320", Variants.All);
        Assert.Contains("thumb-1280", Variants.All);
        Assert.DoesNotContain("", Variants.All);
    }
}
