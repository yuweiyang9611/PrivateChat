using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PrivateChat.Api.Infrastructure.Keys;

public sealed record VapidKeyPair(string PublicKey, string PrivateKey);

/// <summary>
/// 首次启动在 {DataRoot}/keys 生成并持久化：
///  - device-token.key：设备令牌 HMAC-SHA256 签名密钥（32 字节）
///  - vapid.json：Web Push VAPID 密钥对（ECDsa P-256，base64url：公钥 65 字节未压缩点，私钥 32 字节标量）
/// </summary>
public sealed class KeyStore
{
    private readonly string _dir;
    private readonly Lazy<byte[]> _deviceTokenKey;
    private readonly Lazy<VapidKeyPair> _vapid;

    public KeyStore(IOptions<AppOptions> options) : this(Path.Combine(options.Value.DataRoot, "keys")) { }

    public KeyStore(string dir)
    {
        _dir = dir;
        _deviceTokenKey = new(LoadOrCreateDeviceTokenKey);
        _vapid = new(LoadOrCreateVapid);
    }

    public byte[] DeviceTokenKey => _deviceTokenKey.Value;
    public VapidKeyPair Vapid => _vapid.Value;

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
