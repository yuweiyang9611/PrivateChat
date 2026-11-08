using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Blobs;
using PrivateChat.Api.Infrastructure;

namespace PrivateChat.Api.Features.Drive;

/// <summary>
/// 外链页 /s/{token}（docs/DESIGN.md 4.9）：服务端直接渲染的极简页面，不依赖前端应用、不需要登录。
/// 默认仍在 Cloudflare Access 后面（只有家人能开）；要分享给外人，在 Access 里给 /s 单独建 Bypass。
/// 安全：256 bit token、可选密码（失败限速）、过期时间、只能访问共享节点及其子树、严格 CSP、noindex、no-referrer。
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ShareLinkController(AppDbContext db, DriveAccess access, BlobServing serving, IDataProtectionProvider protection) : ControllerBase
{
    private static readonly PasswordHasher<NodeShare> Hasher = new();
    /// <summary>只转义 HTML 特殊字符；中文原样输出（Default 会把所有非 ASCII 变成 &#x...; 实体）。</summary>
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);
    /// <summary>每个链接的密码错误次数（进程内即可：家庭单实例），15 分钟内超过 10 次锁定。</summary>
    private static readonly ConcurrentDictionary<Guid, (int Count, DateTimeOffset Since)> Failures = new();
    private const int MaxFailures = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan UnlockLifetime = TimeSpan.FromHours(12);

    private ITimeLimitedDataProtector Protector => protection.CreateProtector("PrivateChat.ShareLink").ToTimeLimitedDataProtector();
    private static string CookieName(NodeShare s) => $"pc_s_{s.Id:N}";

    private async Task<(NodeShare Share, DriveNode Root)?> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64) return null;
        var share = await db.NodeShares.FirstOrDefaultAsync(s => s.LinkToken == token, ct);
        if (share is null || share.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow) return null;
        var root = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == share.NodeId && n.DeletedAt == null, ct);
        return root is null ? null : (share, root);
    }

    /// <summary>解锁凭证与当前密码绑定：改密码 / 重新生成后旧 Cookie 失效。</summary>
    private bool Unlocked(NodeShare share)
    {
        if (share.PasswordHash is null) return true;
        if (!Request.Cookies.TryGetValue(CookieName(share), out var cookie)) return false;
        try
        {
            return Protector.Unprotect(cookie) == share.PasswordHash;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>请求的节点必须是共享节点本身或其子孙，且未删除。</summary>
    private async Task<DriveNode?> InScopeAsync(NodeShare share, Guid nodeId, CancellationToken ct)
    {
        var node = await db.DriveNodes.FirstOrDefaultAsync(n => n.Id == nodeId && n.DeletedAt == null, ct);
        if (node is null) return null;
        if (node.Id == share.NodeId) return node;
        return (await access.ChainAsync(node.Id, ct)).Contains(share.NodeId) ? node : null;
    }

    [HttpGet("s/{token}")]
    public async Task<IActionResult> Page(string token, [FromQuery] Guid? folder, CancellationToken ct)
    {
        var resolved = await ResolveAsync(token, ct);
        if (resolved is not { } r) return Html_(404, "链接已失效", "<p>这个分享链接不存在、已过期或已被取消。</p>");
        if (!Unlocked(r.Share)) return PasswordPage(token, null);

        var owner = await OwnerNameAsync(r.Root, ct);
        var expiry = r.Share.ExpiresAt is { } e ? $"，{e.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm} 前有效" : "";
        var head = $"<p class=\"muted\">{Html.Encode(owner)} 分享{expiry}</p>";

        if (!r.Root.IsFolder)
            return Html_(200, r.Root.Name, head + await FileBodyAsync(token, r.Root, ct));

        var current = folder is { } fid ? await InScopeAsync(r.Share, fid, ct) : r.Root;
        if (current is null || !current.IsFolder) return Html_(404, "找不到文件夹", "<p>这个文件夹不在分享范围内。</p>");

        var body = new StringBuilder(head);
        body.Append(await BreadcrumbAsync(token, r.Share, current, ct));
        var children = await db.DriveNodes.Where(n => n.ParentId == current.Id && n.DeletedAt == null)
            .OrderByDescending(n => n.IsFolder).ThenBy(n => n.Name).ToListAsync(ct);
        if (children.Count == 0) body.Append("<p class=\"muted\">空文件夹</p>");
        body.Append("<ul class=\"list\">");
        foreach (var c in children)
        {
            var name = Html.Encode(c.Name);
            if (c.IsFolder)
            {
                body.Append($"<li><a class=\"row\" href=\"/s/{Html.Encode(token)}?folder={c.Id}\"><span class=\"icon\">📁</span><span class=\"name\">{name}</span></a></li>");
                continue;
            }
            var thumb = c.Mime is { } m && (m.StartsWith("image/") && m != "image/svg+xml" || m.StartsWith("video/"))
                ? $"<img class=\"thumb\" loading=\"lazy\" alt=\"\" src=\"{FileUrl(token, c, "thumb-320")}\">"
                : "<span class=\"icon\">📄</span>";
            body.Append($"<li><div class=\"row\">{thumb}<span class=\"name\">{name}</span><span class=\"muted size\">{FormatSize(c.Size)}</span>"
                + $"<a class=\"btn\" href=\"{FileUrl(token, c, null, download: true)}\">下载</a></div></li>");
        }
        body.Append("</ul>");
        return Html_(200, current.Name, body.ToString());
    }

    [HttpPost("s/{token}")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Unlock(string token, [FromForm] string? password, CancellationToken ct)
    {
        var resolved = await ResolveAsync(token, ct);
        if (resolved is not { } r) return Html_(404, "链接已失效", "<p>这个分享链接不存在、已过期或已被取消。</p>");
        if (r.Share.PasswordHash is null) return Redirect($"/s/{Uri.EscapeDataString(token)}");

        // 先原子地占用一次尝试再校验：并发提交也不能超过次数上限（成功后清零）
        var now = DateTimeOffset.UtcNow;
        var f = Failures.AddOrUpdate(r.Share.Id, _ => (1, now),
            (_, o) => now - o.Since > FailureWindow ? (1, now) : (o.Count + 1, o.Since));
        if (f.Count > MaxFailures) return PasswordPage(token, "尝试次数过多，请 15 分钟后再试");

        var ok = !string.IsNullOrEmpty(password) && Hasher.VerifyHashedPassword(r.Share, r.Share.PasswordHash, password) != PasswordVerificationResult.Failed;
        if (!ok)
        {
            await Task.Delay(500, ct);
            return PasswordPage(token, "密码不对");
        }
        Failures.TryRemove(r.Share.Id, out _);
        Response.Cookies.Append(CookieName(r.Share), Protector.Protect(r.Share.PasswordHash, UnlockLifetime), new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = $"/s/{token}",
            MaxAge = UnlockLifetime,
        });
        return Redirect($"/s/{Uri.EscapeDataString(token)}");
    }

    /// <summary>下载 / 预览共享范围内的文件。</summary>
    [HttpGet("s/{token}/f/{nodeId:guid}")]
    public async Task<IActionResult> File(string token, Guid nodeId, [FromQuery] string? v, [FromQuery] bool download = false, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(token, ct);
        if (resolved is not { } r || !Unlocked(r.Share)) return NotFound();
        var node = await InScopeAsync(r.Share, nodeId, ct);
        if (node is null || node.IsFolder || node.BlobId is null) return NotFound();
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Id == node.BlobId, ct);
        if (blob is null) return NotFound();
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return await serving.ServeAsync(Response, blob, v, node.Name, download, ct);
    }

    // ---------------- 页面片段 ----------------

    private async Task<string> FileBodyAsync(string token, DriveNode node, CancellationToken ct)
    {
        var mime = node.Mime ?? "";
        var preview = mime switch
        {
            _ when mime.StartsWith("image/") && mime != "image/svg+xml" => $"<img class=\"preview\" alt=\"\" src=\"{FileUrl(token, node, "thumb-1280")}\">",
            _ when mime.StartsWith("video/") => $"<video class=\"preview\" controls preload=\"metadata\" playsinline poster=\"{FileUrl(token, node, "thumb-1280")}\" src=\"{FileUrl(token, node, "play-720p")}\"></video>",
            _ when mime.StartsWith("audio/") => $"<audio controls preload=\"metadata\" src=\"{FileUrl(token, node, "audio-m4a")}\"></audio>",
            _ => "<div class=\"bigicon\">📄</div>",
        };
        await Task.CompletedTask;
        return $"{preview}<p><span class=\"muted\">{FormatSize(node.Size)}</span></p><p><a class=\"btn primary\" href=\"{FileUrl(token, node, null, download: true)}\">下载原文件</a></p>";
    }

    private async Task<string> BreadcrumbAsync(string token, NodeShare share, DriveNode current, CancellationToken ct)
    {
        if (current.Id == share.NodeId) return "";
        var chain = await access.ChainAsync(current.Id, ct);
        var upTo = chain.IndexOf(share.NodeId);
        var ids = chain.Take(upTo + 1).Reverse().ToList();
        var names = await db.DriveNodes.Where(n => ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Name, ct);
        var parts = ids.Select(id => id == current.Id
            ? $"<span>{Html.Encode(names[id])}</span>"
            : $"<a href=\"/s/{Html.Encode(token)}{(id == share.NodeId ? "" : $"?folder={id}")}\">{Html.Encode(names[id])}</a>");
        return $"<nav class=\"crumbs\">{string.Join(" / ", parts)}</nav>";
    }

    private async Task<string> OwnerNameAsync(DriveNode root, CancellationToken ct)
    {
        var space = await db.Spaces.FirstOrDefaultAsync(s => s.Id == root.SpaceId, ct);
        var ownerId = space?.OwnerUserId ?? root.CreatedBy;
        return await db.Users.Where(u => u.Id == ownerId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "家人";
    }

    private static string FileUrl(string token, DriveNode node, string? variant, bool download = false)
    {
        var q = new List<string>();
        if (variant is not null) q.Add("v=" + variant);
        if (download) q.Add("download=true");
        return Html.Encode($"/s/{token}/f/{node.Id}{(q.Count > 0 ? "?" + string.Join("&", q) : "")}");
    }

    private ContentResult PasswordPage(string token, string? error)
        => Html_(200, "需要密码", $"""
            <p>这个分享设置了密码。</p>
            <form method="post" action="/s/{Html.Encode(token)}">
              <input type="password" name="password" autocomplete="off" autofocus required placeholder="输入密码">
              <button class="btn primary" type="submit">打开</button>
            </form>
            {(error is null ? "" : $"<p class=\"error\">{Html.Encode(error)}</p>")}
            """);

    private ContentResult Html_(int status, string title, string body)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        var html = $$"""
            <!doctype html>
            <html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow"><title>{{Html.Encode(title)}} · 家</title>
            <style>
              :root { color-scheme: light dark; --p: #e8873a; --bg: #fdf8f2; --fg: #2b211a; --muted: #8a7a6d; --line: #efe4d8; --card: #fff; }
              @media (prefers-color-scheme: dark) { :root { --bg: #1d1916; --fg: #f3ece5; --muted: #a3958a; --line: #3a322d; --card: #272220; } }
              body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.6 system-ui, -apple-system, "PingFang SC", "Microsoft YaHei", sans-serif; }
              main { max-width: 760px; margin: 0 auto; padding: 24px 16px 48px; }
              h1 { font-size: 20px; margin: 0 0 4px; word-break: break-all; }
              .muted { color: var(--muted); font-size: 14px; }
              .error { color: #d4483b; }
              .btn { display: inline-block; padding: 6px 14px; border-radius: 8px; border: 1px solid var(--line); color: var(--fg); text-decoration: none; background: var(--card); font-size: 14px; cursor: pointer; }
              .btn.primary { background: var(--p); border-color: var(--p); color: #fff; }
              .list { list-style: none; padding: 0; margin: 12px 0; border: 1px solid var(--line); border-radius: 12px; overflow: hidden; background: var(--card); }
              .list li + li { border-top: 1px solid var(--line); }
              .row { display: flex; align-items: center; gap: 12px; padding: 10px 12px; color: inherit; text-decoration: none; }
              .name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
              .icon { width: 40px; text-align: center; font-size: 24px; }
              .thumb { width: 40px; height: 40px; object-fit: cover; border-radius: 6px; background: var(--line); }
              .size { white-space: nowrap; }
              .preview { display: block; max-width: 100%; max-height: 70vh; margin: 16px 0; border-radius: 12px; background: var(--line); }
              .bigicon { font-size: 64px; margin: 24px 0; }
              .crumbs { margin: 8px 0; font-size: 14px; }
              .crumbs a { color: var(--p); }
              input[type=password] { padding: 8px 10px; border-radius: 8px; border: 1px solid var(--line); background: var(--card); color: var(--fg); font-size: 16px; }
            </style></head>
            <body><main><h1>{{Html.Encode(title)}}</h1>{{body}}</main></body></html>
            """;
        return new ContentResult { StatusCode = status, ContentType = "text/html; charset=utf-8", Content = html };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024.0 / 1024 / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB",
    };
}
