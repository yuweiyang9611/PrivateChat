using System.Security.Cryptography;
using PrivateChat.Agent;

namespace PrivateChat.Api.Tests;

/// <summary>被控端自动升级里不碰网络和进程的那部分：读清单、比版本、核对下载到的文件、重启参数。</summary>
public class AgentUpdateManifestTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void Manifest_is_read_and_normalized()
    {
        var manifest = UpdateManifest.Parse($$"""{"version":" 0.6.2 ","sha256":"{{Hash.ToUpperInvariant()}}","size":57880937}""");

        Assert.Equal(new UpdateManifest("0.6.2", Hash, 57880937), manifest);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("<html>登录页</html>")]
    [InlineData("null")]
    [InlineData("""{"version":"0.6.2"}""")]
    [InlineData("""{"version":"latest","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":10}""")]
    [InlineData("""{"version":"0.6.2","sha256":"abc","size":10}""")]
    [InlineData("""{"version":"0.6.2","sha256":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz","size":10}""")]
    [InlineData("""{"version":"0.6.2","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":0}""")]
    [InlineData("""{"version":"0.6.2","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":99999999999}""")]
    public void Anything_that_is_not_a_proper_manifest_is_refused(string json)
    {
        // 被登录页拦住、字段不全、版本号不像版本号、哈希不对、大小离谱：都当没有清单，不升级
        Assert.Null(UpdateManifest.Parse(json));
    }

    [Theory]
    [InlineData("0.6.2", "0.6.1", true)]
    [InlineData("0.10.0", "0.9.9", true)]   // 按数字比，不是按字符串
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.6.2+build7", "0.6.1", true)]
    [InlineData("0.6.2", "0.6.2", false)]   // 一样的不折腾
    [InlineData("0.6.1", "0.6.2", false)]   // 不降级
    [InlineData("0.6.2", "0.6.2+local", false)]
    [InlineData("latest", "0.6.1", false)]  // 认不出来的不升
    [InlineData("0.6.2", "dev", false)]
    [InlineData("7", "0.6.1", false)]
    public void Only_a_strictly_higher_version_counts_as_newer(string candidate, string current, bool newer)
        => Assert.Equal(newer, UpdateManifest.IsNewer(candidate, current));

    [Fact]
    public async Task Downloaded_file_must_match_the_manifest_byte_for_byte()
    {
        var path = Path.Combine(Path.GetTempPath(), "pc-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = RandomNumberGenerator.GetBytes(100_000);
            await File.WriteAllBytesAsync(path, content);
            var manifest = new UpdateManifest("0.6.2", Convert.ToHexStringLower(SHA256.HashData(content)), content.Length);
            Assert.True(await manifest.MatchesAsync(path));

            // 没下完整
            await File.WriteAllBytesAsync(path, content[..^1]);
            Assert.False(await manifest.MatchesAsync(path));

            // 大小一样，中间有一个字节被改了
            content[50_000] ^= 0x01;
            await File.WriteAllBytesAsync(path, content);
            Assert.False(await manifest.MatchesAsync(path));

            Assert.False(await manifest.MatchesAsync(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Relaunch_keeps_how_the_program_was_started_and_records_where_it_came_from()
    {
        // 托盘版：数据目录、自启标记原样带上
        Assert.Equal(["--autostart", "--data-dir", @"C:\Users\mom\AppData\Local\PrivateChat\Agent", "--updated-from", "0.6.1"],
            UpdateManifest.RelaunchArguments(["--autostart", "--data-dir", @"C:\Users\mom\AppData\Local\PrivateChat\Agent"], "0.6.1"));

        // 上一次升级留下的标记换成这一次的；只给测试用的"假装是旧版本"不能带过去（不然新版本又以为自己旧，没完没了地升）
        Assert.Equal(["--headless", "--server", "http://localhost:8080", "--updated-from", "0.0.1"],
            UpdateManifest.RelaunchArguments(["--headless", "--pretend-version", "0.0.1", "--server", "http://localhost:8080", "--updated-from", "0.5.0"], "0.0.1"));

        Assert.Equal(["--updated-from", "0.6.1"], UpdateManifest.RelaunchArguments([], "0.6.1"));
    }
}
