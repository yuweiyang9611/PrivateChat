using System.Security.Cryptography;
using System.Text;
using PrivateChat.Agent;
using PrivateChat.Agent.Platform;

namespace PrivateChat.Api.Tests;

/// <summary>
/// 被控端的发布签名（exe 末尾附的那段，tools/sign-agent-release.mjs 签）：服务以 SYSTEM 运行，只装签过名的版本。
/// 这里用一把临时的钥匙照同样的格式签，看核对得对不对：签了的认，改一个字节、换了钥匙、版本号被改、没有签名的都不认。
/// </summary>
public class AgentReleaseSignatureTests
{
    private const byte TestKeyId = 7;

    private static (ECDsa Key, Dictionary<byte, string> Keys) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, new Dictionary<byte, string> { [TestKeyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
    }

    /// <summary>照 tools/sign-agent-release.mjs 的格式附上签名。</summary>
    private static byte[] Sign(byte[] payload, string version, ECDsa key, byte keyId = TestKeyId)
    {
        var message = ReleaseSignature.Message(version, SHA256.HashData(payload));
        var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var versionBytes = Encoding.UTF8.GetBytes(version);
        var tail = new byte[12];
        BitConverter.TryWriteBytes(tail.AsSpan(0, 2), (ushort)versionBytes.Length);
        tail[2] = keyId;
        tail[3] = 1;
        Encoding.ASCII.GetBytes("PCSIGN01").CopyTo(tail, 4);
        return [.. payload, .. versionBytes, .. signature, .. tail];
    }

    private static ReleaseSignature.Signed Verify(byte[] file, Dictionary<byte, string> keys)
    {
        using var stream = new MemoryStream(file);
        return ReleaseSignature.Verify(stream, keys);
    }

    [Fact]
    public void A_signed_file_is_accepted_with_its_version()
    {
        var (key, keys) = NewKey();
        var payload = RandomNumberGenerator.GetBytes(200_000);

        var signed = Verify(Sign(payload, "0.9.0", key), keys);

        Assert.Equal(new ReleaseSignature.Signed("0.9.0", TestKeyId), signed);
    }

    [Fact]
    public void A_reference_fixture_from_the_node_signer_is_accepted()
    {
        // tools/sign-agent-release.mjs 签的普通文本。只保存测试公钥和签名结果。
        var keys = new Dictionary<byte, string> { [TestKeyId] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAETSmXeO3A2qVkFubUmZb3FI8Krlnt79w+zw0YbfVOKBPZ8Mps2VHqh+1alW+nNFjAcOYSXzIjdNEgomX3VMMQ3Q==" };
        var file = Convert.FromBase64String("UHJpdmF0ZUNoYXQgcmVsZWFzZSB2ZXJpZmljYXRpb24gdGVzdCBmaXh0dXJlCjEuMi4zkSGx0mVzsF6pI6DU1Ytt3iI4isU7cMIRc77LwvpMoqapxRFEHKAyEL052WPNxiqhSu+iJUA3e4Tob2ekIJYZOQUABwFQQ1NJR04wMQ==");

        Assert.Equal(new ReleaseSignature.Signed("1.2.3", TestKeyId), Verify(file, keys));
        using var stream = new MemoryStream(file);
        Assert.Throws<ReleaseSignatureException>(() => ReleaseSignature.Verify(stream));
    }

    [Fact]
    public void Changing_any_byte_of_the_program_breaks_the_signature()
    {
        var (key, keys) = NewKey();
        var file = Sign(RandomNumberGenerator.GetBytes(10_000), "0.9.0", key);
        file[1234] ^= 1;

        Assert.Throws<ReleaseSignatureException>(() => Verify(file, keys));
    }

    [Fact]
    public void Rewriting_the_version_in_the_trailer_breaks_the_signature()
    {
        // 服务器拿签过名的旧版本冒充新版本：版本号签在里面，改了就不认
        var (key, keys) = NewKey();
        var file = Sign(RandomNumberGenerator.GetBytes(10_000), "0.9.0", key);
        var at = file.Length - 12 - 64 - 5;
        Encoding.UTF8.GetBytes("0.9.9").CopyTo(file, at);

        Assert.Throws<ReleaseSignatureException>(() => Verify(file, keys));
    }

    [Fact]
    public void A_file_signed_with_another_key_is_refused()
    {
        var (_, keys) = NewKey();
        var (other, _) = NewKey();

        Assert.Throws<ReleaseSignatureException>(() => Verify(Sign(RandomNumberGenerator.GetBytes(10_000), "0.9.0", other), keys));
    }

    [Fact]
    public void An_unknown_key_id_is_refused()
    {
        var (key, keys) = NewKey();

        Assert.Throws<ReleaseSignatureException>(() => Verify(Sign(RandomNumberGenerator.GetBytes(10_000), "0.9.0", key, keyId: 9), keys));
    }

    [Fact]
    public void An_unsigned_file_is_refused()
    {
        var (_, keys) = NewKey();

        Assert.Throws<ReleaseSignatureException>(() => Verify(RandomNumberGenerator.GetBytes(10_000), keys));
        Assert.Throws<ReleaseSignatureException>(() => Verify([1, 2, 3], keys));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void Built_in_release_keys_take_precedence_over_fixture_keys(byte keyId)
    {
        var (key, keys) = NewKey();
        keys[keyId] = keys[TestKeyId];
        var file = Sign(RandomNumberGenerator.GetBytes(1000), "0.9.0", key, keyId);

        var error = Assert.Throws<ReleaseSignatureException>(() => Verify(file, keys));
        Assert.Contains("签名不对", error.Message);
    }
}

/// <summary>服务起子进程、写服务命令行时的引号：子进程按 CommandLineToArgvW 的规矩拆出来的参数要和原来的一模一样。</summary>
public class AgentCommandLineTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData(@"C:\Program Files\PrivateChat\Agent\PrivateChat.Agent.exe")]
    [InlineData(@"C:\ProgramData\PrivateChat\Agent\logs\")]
    [InlineData(@"ends with backslash\")]
    [InlineData("say \"hi\"")]
    [InlineData(@"tricky\""quote")]
    [InlineData(@"\\server\share\dir with space\")]
    [InlineData("tab\there")]
    [InlineData("中文 路径")]
    public void Every_argument_round_trips(string arg)
    {
        var line = CommandLines.Build(@"C:\x\a.exe", [arg, "--next"]);

        Assert.Equal([@"C:\x\a.exe", arg, "--next"], Parse(line));
    }

    /// <summary>CommandLineToArgvW（参数部分）的规矩：2n 个反斜杠 + 引号 → n 个反斜杠、引号开关；2n+1 个 → n 个反斜杠 + 字面引号；别的反斜杠原样。</summary>
    private static List<string> Parse(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArg = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                var count = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    count++;
                    i++;
                }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
                hasArg = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasArg = true;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasArg) args.Add(current.ToString());
                current.Clear();
                hasArg = false;
            }
            else
            {
                current.Append(c);
                hasArg = true;
            }
        }
        if (hasArg) args.Add(current.ToString());
        return args;
    }
}
