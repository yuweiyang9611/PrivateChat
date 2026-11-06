using System.Security.Cryptography;
using System.Text;
using PrivateChat.Api.Infrastructure.Keys;

namespace PrivateChat.Api.Features.Auth;

public sealed record DeviceTokenPayload(Guid DeviceId, Guid UserId, DateTimeOffset IssuedAt);

/// <summary>
/// 设备令牌：base64url(payload) + "." + base64url(HMAC-SHA256(payload))，密钥来自 KeyStore。
/// 令牌自包含，吊销靠 devices.token_hash 比对。
/// </summary>
public sealed class DeviceTokenService(KeyStore keys)
{
    public string Issue(Guid deviceId, Guid userId, DateTimeOffset? issuedAt = null)
    {
        var payload = $"{deviceId:N}.{userId:N}.{(issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}";
        var payloadBytes = Encoding.ASCII.GetBytes(payload);
        var sig = HMACSHA256.HashData(keys.DeviceTokenKey, payloadBytes);
        return Base64Url.Encode(payloadBytes) + "." + Base64Url.Encode(sig);
    }

    public bool TryValidate(string token, out DeviceTokenPayload payload)
    {
        payload = default!;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        byte[] payloadBytes, sig;
        try
        {
            payloadBytes = Base64Url.Decode(parts[0]);
            sig = Base64Url.Decode(parts[1]);
        }
        catch (FormatException) { return false; }

        var expected = HMACSHA256.HashData(keys.DeviceTokenKey, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, sig)) return false;

        var fields = Encoding.ASCII.GetString(payloadBytes).Split('.');
        if (fields.Length != 3
            || !Guid.TryParseExact(fields[0], "N", out var deviceId)
            || !Guid.TryParseExact(fields[1], "N", out var userId)
            || !long.TryParse(fields[2], out var unix))
            return false;

        payload = new DeviceTokenPayload(deviceId, userId, DateTimeOffset.FromUnixTimeSeconds(unix));
        return true;
    }

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
