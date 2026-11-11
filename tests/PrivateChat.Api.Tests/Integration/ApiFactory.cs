using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Blobs;
using PrivateChat.Api.Features.Calls;
using PrivateChat.Api.Features.Notifications;
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
    private readonly bool _realtimeConfigured;
    private readonly bool _turnConfigured;

    /// <summary>假的 Cloudflare Realtime：记录请求、按官方形状应答。</summary>
    public FakeRealtime Realtime { get; } = new();
    /// <summary>发往推送服务的请求。</summary>
    public CapturingPush Push { get; } = new();

    public ApiFactory(bool realtimeConfigured = true, bool turnConfigured = true)
    {
        _realtimeConfigured = realtimeConfigured;
        _turnConfigured = turnConfigured;
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
        builder.UseSetting("CloudflareRealtime:BaseUrl", "https://rtc.test/v1");
        builder.UseSetting("CloudflareRealtime:AppId", _realtimeConfigured ? FakeRealtime.AppId : "");
        builder.UseSetting("CloudflareRealtime:AppSecret", _realtimeConfigured ? FakeRealtime.AppSecret : "");
        builder.UseSetting("CloudflareRealtime:TurnKeyId", _turnConfigured ? FakeRealtime.TurnKeyId : "");
        builder.UseSetting("CloudflareRealtime:TurnApiToken", _turnConfigured ? FakeRealtime.TurnToken : "");
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<CloudflareRealtimeClient>().ConfigurePrimaryHttpMessageHandler(() => Realtime);
            services.AddHttpClient(nameof(PushService)).ConfigurePrimaryHttpMessageHandler(() => Push);
        });
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

    /// <summary>以某人身份连上 SignalR（长轮询，走测试服务器），用来断言服务端广播。</summary>
    public async Task<HubListener> ConnectHubAsync(string usernameOrEmail)
    {
        var raw = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var res = await raw.PostAsJsonAsync("/api/auth/local/login", new { usernameOrEmail, password = Password });
        res.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", res.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        var listener = new HubListener(connection);
        await connection.StartAsync();
        return listener;
    }

    /// <summary>给某人登记一个浏览器推送订阅（真实格式的密钥，端点指向 CapturingPush）。</summary>
    public async Task<string> AddPushSubscriptionAsync(Guid userId, string name)
    {
        var endpoint = $"https://push.example.test/send/{name}";
        await WithDbAsync(async db =>
        {
            db.Devices.Add(new Device
            {
                UserId = userId, Kind = DeviceKind.Browser, Name = name, Platform = "web",
                PushSubscription = JsonSerializer.Serialize(new StoredPushSubscription(endpoint,
                    "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "tBHItJI5svbpez7KI4CCXg")),
            });
            await db.SaveChangesAsync();
        });
        return endpoint;
    }

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

/// <summary>收集一个 SignalR 连接上收到的事件，按事件名等待。</summary>
public sealed class HubListener : IAsyncDisposable
{
    private static readonly string[] Events =
    [
        "IncomingCall", "CallUpdated", "CallDeclined", "ParticipantJoined", "ParticipantLeft", "ParticipantUpdated",
        "TrackPublished", "TrackUnpublished", "MessageReceived", "ReadReceipt", "ConversationUpdated",
    ];

    private readonly List<(string Name, JsonElement Payload)> _received = [];
    private readonly SemaphoreSlim _signal = new(0);
    public HubConnection Connection { get; }

    public HubListener(HubConnection connection)
    {
        Connection = connection;
        foreach (var name in Events)
            connection.On<JsonElement>(name, payload =>
            {
                lock (_received) _received.Add((name, payload));
                _signal.Release();
            });
    }

    public List<JsonElement> All(string name)
    {
        lock (_received) return _received.Where(e => e.Name == name).Select(e => e.Payload).ToList();
    }

    /// <summary>等到第 count 个该事件（含之前已收到的），返回它。</summary>
    public async Task<JsonElement> WaitAsync(string name, int count = 1, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var all = All(name);
            if (all.Count >= count) return all[count - 1];
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await _signal.WaitAsync(left))
                throw new Xunit.Sdk.XunitException($"等不到第 {count} 个 {name} 事件（已收到 {All(name).Count} 个）");
        }
    }

    public async Task<T> WaitAsync<T>(string name, int count = 1, int timeoutMs = 5000)
        => (await WaitAsync(name, count, timeoutMs)).Deserialize<T>(ApiFactory.Json)!;

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
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
