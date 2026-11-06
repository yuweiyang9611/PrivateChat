using Microsoft.Extensions.Options;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>缩略图 / 转码产物存放在 {DataRoot}/derived，目录结构与原件一致：ab/cd/{sha256}-{variant}.{ext}</summary>
public sealed class DerivedStore(IOptions<AppOptions> options)
{
    private readonly FileSystemBlobStore _fs = new(Path.Combine(options.Value.DataRoot, "derived"));

    public string KeyFor(string sha256, string variant, string ext)
        => $"{_fs.KeyFor(sha256)}-{variant}.{ext}";

    public string PathFor(string key) => _fs.PathFor(key);
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => _fs.ExistsAsync(key, ct);
    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => _fs.OpenReadAsync(key, ct);
    public Task DeleteAsync(string key, CancellationToken ct = default) => _fs.DeleteAsync(key, ct);

    /// <summary>先写临时文件再原子改名。</summary>
    public async Task WriteAsync(string key, Func<string, Task> writeTo, CancellationToken ct = default)
    {
        var path = _fs.PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await writeTo(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        _ = ct;
    }
}
