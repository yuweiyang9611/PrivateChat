namespace PrivateChat.Api.Features.Blobs;

/// <summary>秒传探测：客户端先算 SHA-256，命中则直接拿 blobId。</summary>
public sealed record PrecheckRequest(string Sha256, long Size);
public sealed record PrecheckResponse(bool Exists, Guid? BlobId);

/// <summary>派生产物名（docs/DESIGN.md 3.3）。</summary>
public static class Variants
{
    public const string Thumb320 = "thumb-320";
    public const string Thumb1280 = "thumb-1280";
    public const string Poster = "poster";
    public const string Play720 = "play-720p";
    public const string AudioM4a = "audio-m4a";

    public static readonly string[] All = [Thumb320, Thumb1280, Poster, Play720, AudioM4a];
}

public static class MimeRules
{
    /// <summary>可以 inline 展示的类型；其它一律 attachment + nosniff，防止上传 HTML 之类的内容在同源下执行。</summary>
    public static bool IsInlineSafe(string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
        || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
        || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsImage(string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>只接受形如 type/subtype 的值，否则退回 octet-stream。</summary>
    public static string Normalize(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return "application/octet-stream";
        var m = mime.Trim().ToLowerInvariant();
        var slash = m.IndexOf('/');
        if (slash <= 0 || slash == m.Length - 1 || m.Length > 120) return "application/octet-stream";
        foreach (var ch in m)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '/' or '.' or '+' or '-'))
                return "application/octet-stream";
        return m;
    }

    /// <summary>Content-Disposition 里的文件名：去掉控制字符与引号，过长截断。</summary>
    public static string SafeFileName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        const string forbidden = "\"\\/:*?<>|";
        var chars = name.Where(c => !char.IsControl(c) && !forbidden.Contains(c)).ToArray();
        var s = new string(chars).Trim();
        if (s.Length == 0) return fallback;
        return s.Length > 180 ? s[..180] : s;
    }
}
