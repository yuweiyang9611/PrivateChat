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

/** 每种内容要生成哪些派生产物。 */
public static class DerivedPlan
{
    public static List<string> For(string mime)
    {
        var v = new List<string>();
        if (MimeRules.IsImage(mime) || MimeRules.IsVideo(mime)) v.AddRange([Variants.Thumb320, Variants.Thumb1280]);
        // 视频是否需要转码要先探测编码才知道；先登记，探测后不需要就删掉这一行
        if (MimeRules.IsVideo(mime)) v.Add(Variants.Play720);
        if (MimeRules.NeedsAudioTranscode(mime)) v.Add(Variants.AudioM4a);
        return v;
    }

    public static bool NeedsProbe(string mime) => MimeRules.IsImage(mime) || MimeRules.IsVideo(mime);
}

public static class MimeRules
{
    public static bool IsVideo(string mime) => mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    /// <summary>可以 inline 展示的类型；其它一律 attachment + nosniff，防止上传 HTML 之类的内容在同源下执行。</summary>
    public static bool IsInlineSafe(string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
        || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
        || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsImage(string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>浏览器录音多为 webm/opus 或 ogg，Safari 放不了，统一转 m4a；本来就是 AAC / MP3 的不动。</summary>
    public static bool NeedsAudioTranscode(string mime) =>
        mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        && mime is not ("audio/mp4" or "audio/x-m4a" or "audio/aac" or "audio/mpeg" or "audio/mp3");

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
