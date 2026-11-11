using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PrivateChat.Api.Infrastructure;

/// <summary>业务异常，直接映射为 HTTP 状态码 + ProblemDetails。</summary>
public class ApiException(int status, string title, string? detail = null) : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Title { get; } = title;

    public static ApiException NotFound(string what) => new(404, $"{what}不存在");
    public static ApiException Forbidden(string? detail = null) => new(403, "没有权限", detail);
    public static ApiException BadRequest(string detail) => new(400, "请求无效", detail);
    public static ApiException Conflict(string detail) => new(409, "冲突", detail);
}

/// <summary>全局异常过滤器：ApiException → 对应状态码，其它 → 500，统一 ProblemDetails。</summary>
public sealed class GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger, IHostEnvironment env) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        // 浏览器在请求途中跳走 / 关页面：取消不是错误，也没人等响应（499 是 nginx 对这种情况的约定）
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("请求被客户端取消 {Path}", context.HttpContext.Request.Path);
            context.Result = new StatusCodeResult(499);
            context.ExceptionHandled = true;
            return;
        }

        ProblemDetails problem;
        if (context.Exception is ApiException api)
        {
            problem = new ProblemDetails { Status = api.Status, Title = api.Title, Detail = api.Message };
        }
        else
        {
            logger.LogError(context.Exception, "未处理异常 {Path}", context.HttpContext.Request.Path);
            problem = new ProblemDetails
            {
                Status = 500,
                Title = "服务器内部错误",
                Detail = env.IsDevelopment() ? context.Exception.ToString() : null,
            };
        }
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.Result = new ObjectResult(problem) { StatusCode = problem.Status };
        context.ExceptionHandled = true;
    }
}
