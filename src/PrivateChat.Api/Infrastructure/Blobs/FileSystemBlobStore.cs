using Microsoft.Extensions.Options;

namespace PrivateChat.Api.Infrastructure.Blobs;

/// <summary>{DataRoot}/blobs/{sha256 前 2 位}/{次 2 位}/{sha256}</summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _root;

    public FileSystemBlobStore(IOptions<AppOptions> options) : this(Path.Combine(options.Value.DataRoot, "blobs")) { }

    public FileSystemBlobStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string KeyFor(string sha256)
    {
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("sha256 必须是 64 位十六进制", nameof(sha256));
        var s = sha256.ToLowerInvariant();
        return $"{s[..2]}/{s[2..4]}/{s}";
    }

    public string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("存储键越界", nameof(key));
        return full;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(File.Exists(PathFor(key)));

    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await content.CopyToAsync(fs, ct);
            File.Move(tmp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // 并发写入同一内容：别人先落盘，丢弃自己的临时文件即可
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public Task MoveInAsync(string key, string sourcePath, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(sourcePath);
        else File.Move(sourcePath, path);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
        => Task.FromResult<Stream>(new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
