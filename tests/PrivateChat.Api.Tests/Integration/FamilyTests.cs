using System.Net;
using Microsoft.EntityFrameworkCore;
using PrivateChat.Api.Domain;
using PrivateChat.Api.Features.Auth;
using PrivateChat.Api.Features.Family;

namespace PrivateChat.Api.Tests.Integration;

/// <summary>家庭页背后的接口：邀请邮箱（重复邀请改的是同一条）、撤销邀请、改成员名字 / 角色、停用和恢复；只有管理员能动。</summary>
public class FamilyTests
{
    private sealed record Ctx(ApiFactory App, HttpClient Admin, Guid AdminId, User Bob, HttpClient BobClient) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Ctx> SetupAsync()
    {
        var app = new ApiFactory();
        var admin = await app.LoginAsync("admin@example.com");
        var adminId = (await (await admin.GetAsync("/api/auth/me")).ReadAsync<UserDto>()).Id;
        var bob = await app.AddUserAsync("bob", UserRole.Member);
        return new Ctx(app, admin, adminId, bob, await app.LoginAsync("bob"));
    }

    private static async Task<List<InviteDto>> InvitesAsync(HttpClient c) => await (await c.GetAsync("/api/family/invites")).ReadAsync<List<InviteDto>>();

    [PgFact]
    public async Task Admin_invites_an_email_and_inviting_it_again_updates_the_same_invite()
    {
        await using var x = await SetupAsync();

        var first = await (await x.Admin.PostJsonAsync("/api/family/invites", new { email = " Mom@Example.com ", role = "Member" })).ReadAsync<InviteDto>();
        Assert.Equal("mom@example.com", first.Email);
        Assert.Equal(UserRole.Member, first.Role);
        Assert.Null(first.UsedBy);
        Assert.InRange(first.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));

        // 过期了再邀请一次（或者想换个角色）：还是那一条，不会攒出一堆重复的
        await x.App.WithDbAsync(async db =>
        {
            var row = await db.Invites.SingleAsync(i => i.Id == first.Id);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        });
        var again = await (await x.Admin.PostJsonAsync("/api/family/invites", new { email = "mom@example.com", role = "Child" })).ReadAsync<InviteDto>();
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(UserRole.Child, again.Role);
        Assert.True(again.ExpiresAt > DateTimeOffset.UtcNow.AddDays(29));

        var invite = Assert.Single(await InvitesAsync(x.Admin));
        Assert.Equal(first.Id, invite.Id);
    }

    [PgFact]
    public async Task Invite_is_refused_for_bad_emails_and_existing_members()
    {
        await using var x = await SetupAsync();

        foreach (var bad in new[] { "", "mom", "mom@", "@example.com", "mom@example", "mom @example.com", "a@b@example.com" })
            Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostJsonAsync("/api/family/invites", new { email = bad })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await x.Admin.PostJsonAsync("/api/family/invites", new { email = "BOB@example.com" })).StatusCode);
        Assert.Empty(await InvitesAsync(x.Admin));
    }

    [PgFact]
    public async Task Revoking_removes_an_unused_invite_but_not_a_used_one()
    {
        await using var x = await SetupAsync();
        var invite = await (await x.Admin.PostJsonAsync("/api/family/invites", new { email = "mom@example.com" })).ReadAsync<InviteDto>();

        Assert.Equal(HttpStatusCode.NoContent, (await x.Admin.DeleteAsync($"/api/family/invites/{invite.Id}")).StatusCode);
        Assert.Empty(await InvitesAsync(x.Admin));
        Assert.Equal(HttpStatusCode.NotFound, (await x.Admin.DeleteAsync($"/api/family/invites/{invite.Id}")).StatusCode);

        // 已经用过的邀请是历史记录：要拦人得去停用成员
        var used = await (await x.Admin.PostJsonAsync("/api/family/invites", new { email = "dad@example.com" })).ReadAsync<InviteDto>();
        await x.App.WithDbAsync(async db =>
        {
            var row = await db.Invites.SingleAsync(i => i.Id == used.Id);
            row.UsedBy = x.Bob.Id;
            row.UsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Conflict, (await x.Admin.DeleteAsync($"/api/family/invites/{used.Id}")).StatusCode);
        Assert.Single(await InvitesAsync(x.Admin));
    }

    [PgFact]
    public async Task Only_admins_can_see_send_or_revoke_invites_and_change_members()
    {
        await using var x = await SetupAsync();
        var invite = await (await x.Admin.PostJsonAsync("/api/family/invites", new { email = "mom@example.com" })).ReadAsync<InviteDto>();

        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync("/api/family/invites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PostJsonAsync("/api/family/invites", new { email = "friend@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.DeleteAsync($"/api/family/invites/{invite.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { role = "Admin" })).StatusCode);

        // 成员名单谁都能看
        var members = await (await x.BobClient.GetAsync("/api/family/members")).ReadAsync<List<MemberDto>>();
        Assert.Equal(2, members.Count);
        Assert.Single(await InvitesAsync(x.Admin));
    }

    [PgFact]
    public async Task Admin_renames_changes_role_disables_and_restores_a_member()
    {
        await using var x = await SetupAsync();

        var renamed = await (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { displayName = "  爸爸  " })).ReadAsync<MemberDto>();
        Assert.Equal("爸爸", renamed.DisplayName);
        foreach (var bad in new[] { "", "   ", new string('字', 33) })
            Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { displayName = bad })).StatusCode);

        var child = await (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { role = "Child" })).ReadAsync<MemberDto>();
        Assert.Equal(UserRole.Child, child.Role);
        Assert.Equal("爸爸", child.DisplayName);

        var disabled = await (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { disabled = true })).ReadAsync<MemberDto>();
        Assert.True(disabled.Disabled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.App.Anonymous().PostJsonAsync("/api/auth/local/login", new { usernameOrEmail = "bob", password = ApiFactory.Password })).StatusCode);
        // 停用的成员不能靠"再邀请一次"绕回来，要在成员列表里恢复
        Assert.Equal(HttpStatusCode.Conflict, (await x.Admin.PostJsonAsync("/api/family/invites", new { email = "bob@example.com" })).StatusCode);

        var restored = await (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { disabled = false })).ReadAsync<MemberDto>();
        Assert.False(restored.Disabled);
        Assert.Equal(HttpStatusCode.OK, (await x.App.Anonymous().PostJsonAsync("/api/auth/local/login", new { usernameOrEmail = "bob", password = ApiFactory.Password })).StatusCode);
    }

    [PgFact]
    public async Task Role_change_and_disable_take_effect_on_an_already_signed_in_member()
    {
        await using var x = await SetupAsync();
        // 只有 Admin / Member 能调的接口：成员拿到"设备码不存在"，孩子直接被挡
        const string memberOnly = "/api/auth/device/lookup?code=00000000";
        Assert.Equal(HttpStatusCode.NotFound, (await x.BobClient.GetAsync(memberOnly)).StatusCode);

        // bob 的 Cookie 是改角色之前签发的（30 天有效）：角色以库里现在的为准
        (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { role = "Child" })).EnsureSuccessStatusCode();
        Assert.Equal(UserRole.Child, (await (await x.BobClient.GetAsync("/api/auth/me")).ReadAsync<UserDto>()).Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.BobClient.GetAsync(memberOnly)).StatusCode);

        (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { role = "Admin" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await x.BobClient.GetAsync("/api/family/invites")).StatusCode);

        // 停用：同一个 Cookie 立刻不认了
        (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { disabled = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.BobClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.BobClient.GetAsync("/api/family/members")).StatusCode);

        // 恢复之后要重新登录（停用时那个 Cookie 已经被收回）
        (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { disabled = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.BobClient.GetAsync("/api/auth/me")).StatusCode);
        var again = await x.App.LoginAsync("bob");
        Assert.Equal(UserRole.Admin, (await (await again.GetAsync("/api/auth/me")).ReadAsync<UserDto>()).Role);
    }

    [PgFact]
    public async Task Disabling_or_changing_role_drops_the_members_live_connections()
    {
        await using var x = await SetupAsync();

        async Task<TaskCompletionSource> ConnectedAsync()
        {
            var listener = await x.App.ConnectHubAsync("bob");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.Connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            return closed;
        }

        // 改名字不用断
        var closed = await ConnectedAsync();
        (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { displayName = "爸爸" })).EnsureSuccessStatusCode();
        Assert.False(await Task.WhenAny(closed.Task, Task.Delay(500)) == closed.Task);

        // 改角色：连接上记的还是旧角色，掐断让它重连
        (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { role = "Child" })).EnsureSuccessStatusCode();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        closed = await ConnectedAsync();
        (await x.Admin.PatchJsonAsync($"/api/family/members/{x.Bob.Id}", new { disabled = true })).EnsureSuccessStatusCode();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 重连也连不上了
        await Assert.ThrowsAnyAsync<Exception>(() => x.App.ConnectHubAsync("bob"));
    }

    [PgFact]
    public async Task Admin_cannot_demote_or_disable_themselves()
    {
        await using var x = await SetupAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PatchJsonAsync($"/api/family/members/{x.AdminId}", new { role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PatchJsonAsync($"/api/family/members/{x.AdminId}", new { disabled = true })).StatusCode);

        var me = await (await x.Admin.GetAsync("/api/auth/me")).ReadAsync<UserDto>();
        Assert.Equal(UserRole.Admin, me.Role);
    }
}
