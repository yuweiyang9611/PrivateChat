using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Infrastructure;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Models.Expiration;
using tusdotnet.Stores;

namespace PrivateChat.Api.Features.Blobs;

/// <summary>
/// tus 断点续传端点 /api/uploads（tusdotnet + TusDiskStore，临时区 {DataRoot}/tus）。
/// 元数据：filename、filetype、sha256（可选）。最后一个 PATCH 完成时入库，并在响应头 X-Blob-Id 返回 blobId。
/// </summary>
public static class TusUploads
{
    public const string Path = "/api/uploads";
    public const string BlobIdHeader = "X-Blob-Id";
    private const long MaxSize = 8L * 1024 * 1024 * 1024; // 8 GB

    public static string TempRoot(AppOptions app) => System.IO.Path.Combine(app.DataRoot, "tus");

    public static IEndpointConventionBuilder MapUploads(this IEndpointRouteBuilder endpoints)
    {
        var app = endpoints.ServiceProvider.GetRequiredService<IOptions<AppOptions>>().Value;
        var root = TempRoot(app);
        Directory.CreateDirectory(root);
        var store = new TusDiskStore(root, deletePartialFilesOnConcat: true);

        return endpoints.MapTus(Path, _ => Task.FromResult(new DefaultTusConfiguration
            {
                Store = store,
                MaxAllowedUploadSizeInBytesLong = MaxSize,
                UsePipelinesIfAvailable = true,
                Expiration = new SlidingExpiration(TimeSpan.FromDays(3)),
                Events = new Events
                {
                    OnAuthorizeAsync = ctx =>
                    {
                        if (ctx.HttpContext.User.Identity?.IsAuthenticated != true)
                            ctx.FailRequest(HttpStatusCode.Unauthorized);
                        return Task.CompletedTask;
                    },
                    OnBeforeCreateAsync = ctx =>
                    {
                        if (ctx.Metadata.TryGetValue("sha256", out var sha))
                        {
                            var s = sha.GetString(Encoding.UTF8);
                            if (s.Length != 64 || !s.All(Uri.IsHexDigit)) ctx.FailRequest("sha256 必须是 64 位十六进制");
                        }
                        return Task.CompletedTask;
                    },
                    OnFileCompleteAsync = OnFileCompleteAsync,
                },
            }))
            // 写入权限在登记成网盘文件 / 发消息时判定；这里只要求已登录（孩子账号也能发照片）
            .RequireAuthorization();
    }

    private static async Task OnFileCompleteAsync(FileCompleteContext ctx)
    {
        var http = ctx.HttpContext;
        var ingest = http.RequestServices.GetRequiredService<BlobIngestService>();
        var app = http.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value;
        var file = await ctx.GetFileAsync();
        var metadata = await file.GetMetadataAsync(ctx.CancellationToken);
        string? Meta(string k) => metadata.TryGetValue(k, out var v) ? v.GetString(Encoding.UTF8) : null;

        // TusDiskStore 的数据文件就是 {root}/{fileId}；直接移动，避免再拷一遍大文件
        var dataPath = System.IO.Path.Combine(TempRoot(app), ctx.FileId);
        var result = await ingest.IngestFileAsync(dataPath, Meta("filetype"), Meta("sha256"), http.User.UserId(), ctx.CancellationToken);

        // 数据文件已被移走 / 删除，清掉 tus 的元数据与长度文件
        await ((TusDiskStore)ctx.Store).DeleteFileAsync(ctx.FileId, ctx.CancellationToken);
        http.Response.Headers[BlobIdHeader] = result.Blob.Id.ToString();
    }
}

/// <summary>每小时清理超过 3 天没动静的半截上传。</summary>
public sealed class TusCleanupService(IOptions<AppOptions> app, ILogger<TusCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var store = new TusDiskStore(TusUploads.TempRoot(app.Value));
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var n = await store.RemoveExpiredFilesAsync(ct);
                if (n > 0) logger.LogInformation("清理过期上传 {Count} 个", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "清理过期上传失败"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
