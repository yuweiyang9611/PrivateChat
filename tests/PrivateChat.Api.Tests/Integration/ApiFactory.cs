using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Blobs;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>
/// 集成测试用的 PostgreSQL：默认连开发用的 pc-postgres 容器（README 里的 docker run），
/// CI 里由 PC_TEST_PG 指定。设置了 PC_TEST_PG 时连不上就让测试失败，否则跳过。
/// </summary>
public static class TestDatabase
{
    private static readonly string? FromEnv = Environment.GetEnvironmentVariable("PC_TEST_PG");
    public static string AdminConnection => FromEnv ?? "Host=localhost;Port=5432;Username=privatechat;Password=privatechat;Database=postgres;Timeout=3";

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        if (FromEnv is not null) return true;
        try
        {
            using var c = new NpgsqlConnection(AdminConnection);
            c.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool Available => Reachable.Value;
}

/// <summary>需要真实 PostgreSQL 的测试。</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (!TestDatabase.Available) Skip = "需要 PostgreSQL：启动开发用的 pc-postgres 容器，或设置 PC_TEST_PG";
    }
}

/// <summary>每个测试一套独立的应用 + 数据库 + 数据目录，互不干扰。</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string DbName { get; } = "pc_test_" + Guid.NewGuid().ToString("N")[..12];
    public string DataRoot { get; }
    private readonly string _connection;

    public ApiFactory()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), DbName);
        _connection = new NpgsqlConnectionStringBuilder(TestDatabase.AdminConnection) { Database = DbName, Timeout = 15 }.ConnectionString;
        using var c = new NpgsqlConnection(TestDatabase.AdminConnection);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{DbName}\"";
        cmd.ExecuteNonQuery();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connection);
        builder.UseSetting("App:DataRoot", DataRoot);
        builder.UseSetting("App:AdminEmail", "admin@example.com");
        builder.UseSetting("App:AdminPassword", Password);
        builder.UseSetting("App:PublicUrl", "http://localhost");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }

    public const string Password = "test-password-123";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>建一个能用密码登录的家庭成员。</summary>
    public async Task<User> AddUserAsync(string username, UserRole role)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Email = $"{username}@example.com", Username = username, DisplayName = username, Role = role };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>以某人身份登录，返回带 Cookie 的客户端。</summary>
    public async Task<HttpClient> LoginAsync(string usernameOrEmail)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        var res = await client.PostAsJsonAsync("/api/auth/local/login", new { usernameOrEmail, password = Password });
        res.EnsureSuccessStatusCode();
        return client;
    }

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    /// <summary>直接落一个 blob（文件写入数据目录 + blobs 行 + 上传者凭证），模拟上传完成。</summary>
    public async Task<Blob> AddBlobAsync(Guid ownerId, string mime = "image/png", byte[]? content = null, DateTimeOffset? takenAt = null)
    {
        content ??= RandomNumberGenerator.GetBytes(256);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var key = store.KeyFor(sha);
        await store.PutAsync(key, new MemoryStream(content));
        var blob = new Blob { Sha256 = sha, Size = content.Length, Mime = mime, StorageKey = key, CreatedBy = ownerId, TakenAt = takenAt?.ToUniversalTime(), MediaProbedAt = DateTimeOffset.UtcNow };
        db.Blobs.Add(blob);
        await db.SaveChangesAsync();
        await BlobAccessService.ClaimAsync(db, blob.Id, ownerId, default);
        return blob;
    }

    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        try
        {
            await using var c = new NpgsqlConnection(TestDatabase.AdminConnection);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{DbName}\" WITH (FORCE)";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // 清理失败不影响结果
        }
        try { if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, true); } catch (IOException) { }
    }
}

public static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"{(int)res.StatusCode} {res.RequestMessage?.Method} {res.RequestMessage?.RequestUri}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient c, string url, object body) => c.PostAsJsonAsync(url, body, ApiFactory.Json);
    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient c, string url, object body) => c.PatchAsJsonAsync(url, body, ApiFactory.Json);
}
