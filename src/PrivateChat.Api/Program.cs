using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Blobs;
using PrivateChat.Api.Features.Drive;
using PrivateChat.Api.Features.Moments;
using PrivateChat.Api.Features.Calls;
using PrivateChat.Api.Features.Chat;
using PrivateChat.Api.Features.Notifications;
using PrivateChat.Api.Infrastructure;
using PrivateChat.Api.Infrastructure.Blobs;
using PrivateChat.Api.Infrastructure.Keys;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// ---- 配置 ----
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.Configure<CloudflareAccessOptions>(builder.Configuration.GetSection(CloudflareAccessOptions.Section));
builder.Services.Configure<CloudflareRealtimeOptions>(builder.Configuration.GetSection(CloudflareRealtimeOptions.Section));
builder.Services.Configure<WebPushOptions>(builder.Configuration.GetSection(WebPushOptions.Section));

// nginx 转发；tus 分片 8 MB，放宽到 64 MB
builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// ---- 数据 ----
var connectionString = builder.Configuration.GetConnectionString("Default")
                       ?? "Host=localhost;Database=privatechat;Username=privatechat;Password=privatechat";
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
builder.Services.AddSingleton<IBlobStore, FileSystemBlobStore>();

// Cookie 加密密钥持久化到数据卷，否则容器重启后所有本地登录 Cookie 失效
var dataRoot = builder.Configuration.GetValue<string>("App:DataRoot") ?? "data";
builder.Services.AddDataProtection()
    .SetApplicationName("PrivateChat")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys", "dataprotection")));
builder.Services.AddSingleton<KeyStore>();

// ---- 认证：默认 Smart 方案按请求特征转发到 Access / DeviceToken / Local ----
builder.Services.AddHttpClient<JwksAccessKeyProvider>();
builder.Services.AddSingleton<IAccessKeyProvider>(sp => sp.GetRequiredService<JwksAccessKeyProvider>());
builder.Services.AddSingleton<CloudflareAccessTokenValidator>();
builder.Services.AddSingleton<DeviceTokenService>();
builder.Services.AddScoped<UserProvisioningService>();

builder.Services.AddAuthentication(AuthSchemes.Smart)
    .AddPolicyScheme(AuthSchemes.Smart, "Smart", o => o.ForwardDefaultSelector = ctx =>
    {
        if (ctx.Request.Headers.ContainsKey(AuthSchemes.AccessHeader)) return AuthSchemes.CloudflareAccess;
        if (ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthSchemes.DeviceToken;
        return AuthSchemes.Local;
    })
    .AddScheme<AuthenticationSchemeOptions, CloudflareAccessAuthenticationHandler>(AuthSchemes.CloudflareAccess, null)
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(AuthSchemes.DeviceToken, null)
    .AddCookie(AuthSchemes.Local, o =>
    {
        o.Cookie.Name = "pc_local";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.Admin, p => p.RequireRole(nameof(UserRole.Admin)))
    .AddPolicy(AuthPolicies.Member, p => p.RequireRole(nameof(UserRole.Admin), nameof(UserRole.Member)));

// ---- Web ----
builder.Services.AddControllers(o => o.Filters.Add<GlobalExceptionFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// OpenAPI 文档走 HttpJsonOptions，这里也注册字符串枚举，保证生成的前端类型是字符串联合而非整数
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    // 默认 AllowReadingFromString 会让文档里的 long 变成 integer | string，前端类型随之变脏
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
});
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");
builder.Services.AddHealthChecks().AddNpgSql(connectionString, name: "postgres");
builder.Services.AddHttpClient<CloudflareRealtimeClient>();

// ---- Features ----
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddSingleton<DerivedStore>();
builder.Services.AddSingleton<MediaQueue>();
builder.Services.AddSingleton<PrivateChat.Api.Infrastructure.Media.Ffmpeg>();
builder.Services.AddScoped<BlobIngestService>();
builder.Services.AddScoped<BlobAccessService>();
builder.Services.AddScoped<BlobServing>();
builder.Services.AddHostedService<MediaWorker>();
builder.Services.AddScoped<DriveAccess>();
builder.Services.AddScoped<DriveService>();
builder.Services.AddScoped<DriveSharing>();
builder.Services.AddScoped<GalleryService>();
builder.Services.AddScoped<MomentsNotifier>();
builder.Services.AddScoped<MomentsService>();
builder.Services.AddScoped<AlbumService>();
builder.Services.AddHostedService<StorageMaintenanceWorker>();
builder.Services.AddHostedService<TusCleanupService>();
builder.Services.AddHttpClient(nameof(PushService));
builder.Services.AddSingleton<PushService>();
builder.Services.AddHostedService<PushWorker>();
builder.Services.AddSingleton<CallLocks>();
builder.Services.AddScoped<CallService>();
builder.Services.AddHostedService<CallMaintenanceWorker>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapUploads();
app.MapHub<ChatHub>("/hubs/chat");
app.MapOpenApi("/openapi/{documentName}.json");
app.MapScalarApiReference("/scalar", o => o.WithOpenApiRoutePattern("/openapi/{documentName}.json"));
app.MapHealthChecks("/healthz");

// 启动时自动迁移；首次启动生成密钥
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsNpgsql() && !app.Configuration.GetValue<bool>("App:SkipMigrate"))
        await db.Database.MigrateAsync();
    var keys = scope.ServiceProvider.GetRequiredService<KeyStore>();
    _ = keys.DeviceTokenKey;
    _ = keys.Vapid;
}
await AdminBootstrap.RunAsync(app.Services);

app.Run();

public partial class Program;
