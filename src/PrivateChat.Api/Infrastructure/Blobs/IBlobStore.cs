namespace PrivateChat.Api.Infrastructure.Blobs;

/// <summary>
/// 内容寻址的原始文件存储。v1 为本地磁盘；将来可换 S3 兼容实现（docs/DESIGN.md 决策 B）。
/// </summary>
public interface IBlobStore
{
    /// <summary>把 sha256 映射为存储键（本地实现为相对路径 ab/cd/sha256）。</summary>
    string KeyFor(string sha256);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    /// <summary>把流写入 key；若已存在则直接返回。</summary>
    Task PutAsync(string key, Stream content, CancellationToken ct = default);
    /// <summary>把已落盘的临时文件移动到 key（tus 上传完成后使用，避免二次拷贝）。</summary>
    Task MoveInAsync(string key, string sourcePath, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
