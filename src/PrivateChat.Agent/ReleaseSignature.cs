using System.Security.Cryptography;
using System.Text;

namespace PrivateChat.Agent;

// 这个文件不碰 Windows（测试项目把它直接编进去）。

/// <summary>
/// 发布签名：exe 末尾附一段签名（tools/sign-agent-release.mjs 在开发者的笔记本上签，私钥不离开那台电脑、也不在服务器上）。
/// 装成系统服务后，服务以 SYSTEM 运行，服务器给什么版本它就装什么——服务器（或者 Cloudflare 账号）被人拿下的话，所有家里电脑都会被装上别人的程序。
/// 所以服务只装带着有效签名的版本（自动升级），安装程序也只把带有效签名的自己装进 Program Files。
///
/// 末尾的格式（全部附在原来的 exe 后面，Windows 加载程序和 .NET 单文件都不看文件末尾多出来的东西）：
///   版本号（UTF-8，N 字节）| 签名（64 字节，ECDSA P-256 + SHA-256，IEEE P1363）| N（2 字节小端）| 密钥编号（1 字节）| 格式（1 字节，=1）| "PCSIGN01"
/// 签的内容："PrivateChat.Agent release v1\n{版本号}\n" 后面接上原来那个 exe（不含末尾这段）的 SHA-256。
/// 版本号签在里面：服务器拿一个旧的、签过名的版本冒充新版本（降级攻击）也不行——升级时签名里的版本号必须和清单里的一样、而且比现在的新。
/// 公钥编进程序里：0 号是平时用的，1 号是离线备份（主钥丢了的时候用它签一个换了新主钥的版本）。
/// 文件验签使用内置公钥。单元测试可为内存流传入独立的测试公钥。
/// </summary>
internal static class ReleaseSignature
{
    private const string Magic = "PCSIGN01";
    private const byte Format = 1;
    private const int SignatureBytes = 64;
    private const string Domain = "PrivateChat.Agent release v1\n";

    private static readonly string[] Keys =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEu8GRB0Vbd+wVQA/DpozvEDPE4yztHtWRP3B8p5Nuqf7hMbYoC6jy6187aepZ4c5oOwFlTZ4nNu41t7uCUBOENQ==",
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZJb4Y1XWNm9RWybZJK3h54lMl85d8v3esoUy79RbsayUQg3P408TssnXglcjrAkGYNLwpVctl6id1kJHozIzuw==",
    ];

    /// <summary>签名里的版本号、用的哪把钥匙。</summary>
    public sealed record Signed(string Version, int KeyId);

    /// <summary>核对文件末尾的签名；没有签名、签名不对都抛 <see cref="ReleaseSignatureException"/>（说明原因）。</summary>
    public static Signed Verify(string path)
    {
        using var file = File.OpenRead(path);
        return Verify(file);
    }

    /// <param name="extraKeys">另外认的钥匙（编号 → Base64 公钥）：单元测试用。</param>
    public static Signed Verify(Stream file, IReadOnlyDictionary<byte, string>? extraKeys = null)
    {
        var length = file.Length;
        const int fixedTail = SignatureBytes + 2 + 1 + 1 + 8;
        if (length < fixedTail + 1) throw new ReleaseSignatureException("没有发布签名（文件太短）");
        var tail = new byte[12];
        file.Seek(length - 12, SeekOrigin.Begin);
        file.ReadExactly(tail);
        if (Encoding.ASCII.GetString(tail, 4, 8) != Magic) throw new ReleaseSignatureException("没有发布签名");
        var versionLength = BitConverter.ToUInt16(tail, 0);
        var keyId = tail[2];
        if (tail[3] != Format) throw new ReleaseSignatureException($"发布签名的格式 {tail[3]} 认不出来");
        var trailer = versionLength + fixedTail;
        if (versionLength is 0 or > 64 || trailer > length) throw new ReleaseSignatureException("发布签名坏了");
        var payloadLength = length - trailer;
        var block = new byte[versionLength + SignatureBytes];
        file.Seek(payloadLength, SeekOrigin.Begin);
        file.ReadExactly(block);
        var version = Encoding.UTF8.GetString(block, 0, versionLength);
        var signature = block.AsSpan(versionLength, SignatureBytes).ToArray();

        file.Seek(0, SeekOrigin.Begin);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        for (var remaining = payloadLength; remaining > 0;)
        {
            var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new InvalidDataException("文件读不完整");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        var message = Message(version, hash.GetHashAndReset());

        var key = (keyId < Keys.Length ? Keys[keyId] : extraKeys?.GetValueOrDefault(keyId))
            ?? throw new ReleaseSignatureException($"发布签名用的钥匙 {keyId} 不认识");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
        if (!ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new ReleaseSignatureException("发布签名不对（文件被改过，或者不是我们签的）");
        return new Signed(version, keyId);
    }

    /// <summary>签名的内容（tools/sign-agent-release.mjs 照这个拼）。</summary>
    internal static byte[] Message(string version, byte[] payloadSha256) => [.. Encoding.UTF8.GetBytes($"{Domain}{version}\n"), .. payloadSha256];
}

/// <summary>
/// 发布签名不让装：没有签名、签名不对、钥匙不认识，或者签名里的版本号和清单对不上 / 不比现在的新（Service/ServiceUpdate）。
/// 管理员诊断里单独算一类"签名"（UpdateCheckTrace），不混在"原因未知"里。
/// </summary>
internal sealed class ReleaseSignatureException(string message) : Exception(message);
