using System.Security.Cryptography;
using System.Text.Json;

namespace PrivateChat.Agent;

/// <summary>
/// 服务器上安装包旁边的清单（/downloads/PrivateChat.Agent.json，镜像构建时生成）：现在发布的是哪个版本、文件多大、SHA-256 是多少。
/// 被控端据此决定要不要升级，并核对下载到的文件一个字节都没坏。
/// </summary>
public sealed record UpdateManifest(string Version, string Sha256, long Size)
{
    /// <summary>安装包再大也不该超过这个数：清单写得离谱（或被换成别的东西）就不下。</summary>
    public const long MaxSize = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>解析清单；字段不全、版本号不像版本号、哈希不是 64 位十六进制、大小离谱的一律不认（返回 null）。</summary>
    public static UpdateManifest? Parse(string json)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (manifest is null || ParseVersion(manifest.Version) is null) return null;
        if (manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit)) return null;
        if (manifest.Size is <= 0 or > MaxSize) return null;
        return manifest with { Version = manifest.Version.Trim(), Sha256 = manifest.Sha256.ToLowerInvariant() };
    }

    /// <summary>"0.6.2"、"0.6.2+abc"、"0.6.2-beta" 都按 0.6.2 算；不是数字点数字的不认。</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().Split('+', '-')[0];
        return System.Version.TryParse(core, out var v) && v.Major >= 0 && core.Contains('.') ? v : null;
    }

    /// <summary>只往高了升：服务器上的版本比现在跑的新才算（一样、更旧、认不出来的都不升）。</summary>
    public static bool IsNewer(string candidate, string current)
        => ParseVersion(candidate) is { } c && ParseVersion(current) is { } now && c > now;

    /// <summary>下载到的文件和清单对得上：大小一样，SHA-256 一样。</summary>
    public async Task<bool> MatchesAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != Size) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(Sha256));
    }

    /// <summary>
    /// 升级后重新启动自己用的参数：原来怎么启动的就怎么启动（数据目录、无界面等都带上），
    /// 去掉上一次升级留下的标记和只给测试用的"假装是旧版本"，加上"从哪个版本升上来的"。
    /// </summary>
    public static List<string> RelaunchArguments(IReadOnlyList<string> original, string fromVersion)
    {
        var args = new List<string>();
        for (var i = 0; i < original.Count; i++)
        {
            if (original[i] is "--updated-from" or "--pretend-version")
            {
                i++; // 连同它的值一起去掉
                continue;
            }
            args.Add(original[i]);
        }
        args.Add("--updated-from");
        args.Add(fromVersion);
        return args;
    }
}
