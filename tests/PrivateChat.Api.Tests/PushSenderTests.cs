using System.Net;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using PrivateChat.Api.Features.Notifications;
using PrivateChat.Api.Infrastructure.Keys;

namespace PrivateChat.Api.Tests;

public class PushSenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-push-" + Guid.NewGuid().ToString("N"));

    // 一个真实格式的浏览器订阅（P-256 公钥 65 字节 + 16 字节 auth），端点随便填
    private static readonly StoredPushSubscription Sub = new(
        "https://push.example.test/send/abc",
        "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM",
        "tBHItJI5svbpez7KI4CCXg");

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public byte[]? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent("") };
        }
    }

    private (PushSender Sender, StubHandler Handler) Make(HttpStatusCode status)
    {
        var keys = new KeyStore(_dir);
        var vapid = new VapidAuthentication(keys.Vapid.PublicKey, keys.Vapid.PrivateKey) { Subject = "mailto:test@example.com" };
        var handler = new StubHandler(status);
        return (new PushSender(new HttpClient(handler), vapid, NullLogger.Instance), handler);
    }

    [Fact]
    public async Task Sends_encrypted_vapid_signed_request()
    {
        var (sender, handler) = Make(HttpStatusCode.Created);
        var payload = new PushPayload("妈妈 · 全家群", "晚上回来吃饭", "/messages/abc", "conv:abc");

        var result = await sender.SendAsync(Sub, payload, CancellationToken.None);

        Assert.Equal(PushSendResult.Sent, result);
        var req = handler.Request!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://push.example.test/send/abc", req.RequestUri!.ToString());
        Assert.StartsWith("vapid t=", req.Headers.Authorization!.ToString());
        Assert.Contains("aes128gcm", req.Content!.Headers.ContentEncoding);
        Assert.Equal("86400", req.Headers.GetValues("TTL").Single());
        Assert.Equal("convabc", req.Headers.GetValues("Topic").Single());
        Assert.Equal("high", req.Headers.GetValues("Urgency").Single());
        // 密文而不是明文 JSON
        Assert.NotNull(handler.Body);
        Assert.DoesNotContain("晚上回来吃饭", System.Text.Encoding.UTF8.GetString(handler.Body!));
        Assert.True(handler.Body!.Length > 100);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task Expired_subscription_reports_gone(HttpStatusCode status)
    {
        var (sender, _) = Make(status);
        var result = await sender.SendAsync(Sub, new PushPayload("t", "b", "/", "x"), CancellationToken.None);
        Assert.Equal(PushSendResult.Gone, result);
    }

    [Fact]
    public async Task Other_errors_report_failed()
    {
        var (sender, _) = Make(HttpStatusCode.TooManyRequests);
        var result = await sender.SendAsync(Sub, new PushPayload("t", "b", "/", "x"), CancellationToken.None);
        Assert.Equal(PushSendResult.Failed, result);
    }

    [Theory]
    [InlineData("conv:01a1-b2", "conv01a1-b2")]
    [InlineData("有中文/和符号!", "")]
    public void Topic_is_url_safe_and_short(string tag, string expected)
    {
        Assert.Equal(expected, PushSender.SafeTopic(tag));
        Assert.True(PushSender.SafeTopic(new string('a', 100)).Length == 32);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
