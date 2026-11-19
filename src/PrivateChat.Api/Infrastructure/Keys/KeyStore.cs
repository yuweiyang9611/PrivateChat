using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PrivateChat.Api.Infrastructure.Keys;

public sealed record VapidKeyPair(string PublicKey, string PrivateKey);

/// <summary>
/// 首次启动在 {DataRoot}/keys 生成并持久化：
///  - device-token.key：设备令牌 HMAC-SHA256 签名密钥（32 字节）
///  - vapid.json：Web Push VAPID 密钥对（ECDsa P-256，base64url：公钥 65 字节未压缩点，私钥 32 字节标量）
///  - direct-tls.pfx：被控端局域网直连入口的自签证书（ECDsa P-256）。被控端按公钥指纹认它，不看签发者和有效期；
///    删掉这个文件就换一张新的，被控端直连失败后经公网拿到新指纹，自己会换过来
/// </summary>
public sealed class KeyStore
{
    private readonly string _dir;
    private readonly Lazy<byte[]> _deviceTokenKey;
    private readonly Lazy<VapidKeyPair> _vapid;
    private readonly Lazy<X509Certificate2> _directCertificate;

    public KeyStore(IOptions<AppOptions> options) : this(Path.Combine(options.Value.DataRoot, "keys")) { }

    public KeyStore(string dir)
    {
        _dir = dir;
        _deviceTokenKey = new(LoadOrCreateDeviceTokenKey);
        _vapid = new(LoadOrCreateVapid);
        _directCertificate = new(LoadOrCreateDirectCertificate);
    }

    public byte[] DeviceTokenKey => _deviceTokenKey.Value;
    public VapidKeyPair Vapid => _vapid.Value;
    public X509Certificate2 DirectCertificate => _directCertificate.Value;

    private byte[] LoadOrCreateDeviceTokenKey()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "device-token.key");
        if (File.Exists(path)) return Convert.FromBase64String(File.ReadAllText(path).Trim());
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        return key;
    }

    private VapidKeyPair LoadOrCreateVapid()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "vapid.json");
        if (File.Exists(path))
            return JsonSerializer.Deserialize<VapidKeyPair>(File.ReadAllText(path))
                   ?? throw new InvalidOperationException("vapid.json 损坏");

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        var pub = new byte[65];
        pub[0] = 0x04;
        p.Q.X!.CopyTo(pub, 1);
        p.Q.Y!.CopyTo(pub, 33);
        var pair = new VapidKeyPair(Base64Url.Encode(pub), Base64Url.Encode(p.D!));
        File.WriteAllText(path, JsonSerializer.Serialize(pair));
        return pair;
    }

    private X509Certificate2 LoadOrCreateDirectCertificate()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "direct-tls.pfx");
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PrivateChat remote agent direct", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        // 有效期给长：被控端只认指纹，不看有效期；到期换证书反而要所有电脑重新拿指纹
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        var pfx = created.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(path + ".tmp", pfx);
        File.Move(path + ".tmp", path, overwrite: true);
        // 重新从 PFX 读出来：CreateSelfSigned 给的私钥是临时的，有的平台上 TLS 用不了
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
        return Convert.FromBase64String(b);
    }
}
