using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SIPSorcery.Net;
using SIPSorcery.Sys;

namespace PrivateChat.Agent.Realtime;

/// <summary>
/// 接通以后每隔两三秒在选定的那对地址上发一个带 USE-CANDIDATE 的 STUN 检查，让 Cloudflare 一直认画面走的这条路。
/// <para>
/// 为什么（"快速线路接通约 35 秒后断流"的原因，2026-11-22 在真实 Cloudflare 上查清）：Cloudflare 的 SFU 是 ICE-lite，
/// 以最后收到的 USE-CANDIDATE 决定走哪条路。SIPSorcery 只在提名时发一次 USE-CANDIDATE，之后的保活检查都不带；
/// 接通那一下，中转（TURN）那对的检查要是比直连那对的提名晚到——库里两个线程同时处理回话、提名不是原子的——
/// Cloudflare 就改认中转那条路。可画面、声音、保活都还走直连，中转那条路上什么都收不到，Cloudflare 35–36 秒后
/// 把整条连接拆掉：两头都还显示"已连接"，画面和声音同时停。
/// </para>
/// <para>
/// 实测：故意在接通时 / 接通 5 秒后往中转那对补一个 USE-CANDIDATE，12 次全部照这个样子断（时间对得上线上那三次）；
/// 补完 1 秒后再在直连那对发一个 USE-CANDIDATE，Cloudflare 一秒内认回直连，2 分钟都不断。不带 USE-CANDIDATE 的检查不会让它改路，
/// 停掉所有检查也不会断（只要画面在走）。Chrome 对 ICE-lite 的对端每次检查都带 USE-CANDIDATE，所以浏览器不会这样。
/// </para>
/// 选定的是中转那对时，<see cref="RtpIceChannel.Send"/> 自己会经 TURN 转发，照样是"在画面走的那条路上"发。
/// Cloudflare 的回话库不认（事务号是这里自己起的），连接状态里会照常忽略。
/// </summary>
internal sealed class IcePathKeeper : IDisposable
{
    /// <summary>接通后多久发第一个（中转那对的检查一般在接通前后几十毫秒内到，Cloudflare 每秒定一次路）</summary>
    private const int FirstMs = 1000;
    /// <summary>之后每隔这么久一个，前后随机错开 0.5 秒（和 Chrome 的检查间隔差不多）</summary>
    private const int EveryMs = 2500;
    private const int JitterMs = 500;

    private static readonly FieldInfo? TiebreakerField = typeof(RtpIceChannel).GetField("_iceTiebreaker", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly RtpIceChannel _ice;
    private readonly ulong _tiebreaker;
    private readonly System.Threading.Timer _timer;
    private int _started;
    private int _disposed;
    private long _sent;

    /// <summary>排查用：第一次发、出错时各说一句（平时是 null）</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>发出去多少个</summary>
    public long Sent => Interlocked.Read(ref _sent);

    public IcePathKeeper(RtpIceChannel ice)
    {
        _ice = ice;
        // 用库自己的 tiebreaker（ICE-lite 的对端不比这个，取不到时随便一个固定值也行）
        _tiebreaker = TiebreakerField?.GetValue(ice) is ulong t ? t : BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>接通时调一次（再调不起作用）</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
        _timer.Change(FirstMs, Timeout.Infinite);
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var entry = _ice.NominatedEntry;
            if (entry is not null && _ice.IceConnectionState is RTCIceConnectionState.connected or RTCIceConnectionState.disconnected)
            {
                Send(entry);
                if (Interlocked.Increment(ref _sent) == 1)
                    Trace?.Invoke($"开始定期在选定的那对上发 USE-CANDIDATE（{entry.LocalCandidate.ToShortString()} → {entry.RemoteCandidate.ToShortString()}）");
            }
        }
        catch (Exception ex)
        {
            // 定时器线程：异常漏出去会让整个程序退出
            Trace?.Invoke($"发 USE-CANDIDATE 出错：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                try { _timer.Change(EveryMs + Random.Shared.Next(-JitterMs, JitterMs + 1), Timeout.Infinite); }
                catch (ObjectDisposedException) { /* 刚好关了 */ }
            }
        }
    }

    private void Send(ChecklistEntry entry)
    {
        var request = new STUNMessage(STUNMessageTypesEnum.BindingRequest);
        request.Header.TransactionId = Encoding.ASCII.GetBytes(Crypto.GetRandomString(STUNHeader.TRANSACTION_ID_LENGTH));
        request.AddUsernameAttribute($"{_ice.RemoteIceUser}:{_ice.LocalIceUser}");
        var priority = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(priority, entry.LocalPriority);
        request.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Priority, priority));
        request.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.IceControlling, NetConvert.GetBytes(_tiebreaker)));
        request.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.UseCandidate, null));
        var bytes = request.ToByteBufferStringKey(_ice.RemoteIcePassword, true);
        _ice.Send(RTPChannelSocketsEnum.RTP, entry.RemoteCandidate.DestinationEndPoint, bytes);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }
}
