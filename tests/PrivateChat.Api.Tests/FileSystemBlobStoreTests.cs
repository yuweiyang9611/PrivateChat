using System.Security.Cryptography;
using System.Text;
using PrivateChat.Api.Infrastructure.Blobs;

namespace PrivateChat.Api.Tests;

public sealed class FileSystemBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "privatechat-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemBlobStore _store;

    public FileSystemBlobStoreTests() => _store = new FileSystemBlobStore(_root);

    [Fact]
    public void KeyFor_maps_sha256_to_two_level_directory()
    {
        var sha = new string('A', 60) + "bcde";
        var key = _store.KeyFor(sha);
        Assert.Equal("aa/aa/" + sha.ToLowerInvariant(), key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz")]
    public void KeyFor_rejects_non_sha256(string bad)
        => Assert.Throws<ArgumentException>(() => _store.KeyFor(bad.PadRight(bad.Length == 0 ? 0 : 64, 'z')));

    [Fact]
    public void PathFor_rejects_traversal()
        => Assert.Throws<ArgumentException>(() => _store.PathFor("../../etc/passwd"));

    [Fact]
    public async Task Put_then_read_roundtrips_and_is_idempotent()
    {
        var content = Encoding.UTF8.GetBytes("hello family");
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var key = _store.KeyFor(sha);

        await _store.PutAsync(key, new MemoryStream(content));
        await _store.PutAsync(key, new MemoryStream("different"u8.ToArray())); // 已存在：不覆盖

        Assert.True(await _store.ExistsAsync(key));
        await using var s = await _store.OpenReadAsync(key);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        Assert.Equal(content, ms.ToArray());
        Assert.True(File.Exists(Path.Combine(_root, sha[..2], sha[2..4], sha)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
