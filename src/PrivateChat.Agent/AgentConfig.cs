using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivateChat.Agent;

/// <summary>
/// 本机配置：%LOCALAPPDATA%\PrivateChat\Agent\config.json（测试时 --data-dir 指定）。
/// 设备令牌和 Access 服务令牌密钥用 DPAPI（当前用户）加密，换个 Windows 账户或拷到别的电脑上都解不开。
/// </summary>
public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "";
    public string AccessClientId { get; set; } = "";
    public string AccessClientSecretProtected { get; set; } = "";
    public string DeviceTokenProtected { get; set; } = "";
    public Guid? DeviceId { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateChat", "Agent");

    public static AgentConfig Load(string dir)
    {
        var path = Path.Combine(dir, "config.json");
        try
        {
            if (!File.Exists(path)) return new();
            var text = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AgentConfig>(text) ?? new();
            // 旧版本把解密后的令牌也写进了文件：读到就立刻重写一遍，去掉明文
            using (var doc = JsonDocument.Parse(text))
                if (doc.RootElement.TryGetProperty("DeviceToken", out _) || doc.RootElement.TryGetProperty("AccessClientSecret", out _))
                    config.Save(dir);
            return config;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            AgentLog.Warn($"配置文件读不出来，重新开始：{ex.Message}");
            return new();
        }
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>解密后的值只在内存里用，不序列化（落盘的只有 DPAPI 加密过的 *Protected）。</summary>
    [JsonIgnore]
    public string AccessClientSecret
    {
        get => Dpapi.Unprotect(AccessClientSecretProtected);
        set => AccessClientSecretProtected = Dpapi.Protect(value);
    }

    [JsonIgnore]
    public string DeviceToken
    {
        get => Dpapi.Unprotect(DeviceTokenProtected);
        set => DeviceTokenProtected = Dpapi.Protect(value);
    }

    [JsonIgnore]
    public bool HasServer => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    /// <summary>
    /// 网页"添加电脑"给的配置串：PCA1.&lt;base64url(JSON {v, url, id, secret})&gt;；也接受直接粘一个服务器地址。
    /// </summary>
    public static bool TryParseSetup(string text, out string url, out string? clientId, out string? clientSecret)
    {
        url = "";
        clientId = clientSecret = null;
        text = text.Trim();
        if (text.StartsWith("PCA1.", StringComparison.Ordinal))
        {
            try
            {
                var b64 = text[5..].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
                var root = doc.RootElement;
                url = root.GetProperty("url").GetString() ?? "";
                clientId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
                clientSecret = root.TryGetProperty("secret", out var s) ? s.GetString() : null;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return false;
            }
        }
        else
        {
            url = text;
        }
        url = url.TrimEnd('/');
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
    }
}

/// <summary>Windows DPAPI（CryptProtectData，当前用户范围）。</summary>
internal static class Dpapi
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        return Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    public static string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedText), protect: false));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            AgentLog.Warn("本地保存的令牌解不开（换了 Windows 账户？），需要重新添加");
            return "";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, "PrivateChat Agent", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new InvalidOperationException($"DPAPI 失败：{Marshal.GetLastPInvokeError()}");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>极简日志：写到数据目录下的 agent.log（超过 2 MB 换新），无界面模式同时打到控制台。</summary>
public static class AgentLog
{
    private static readonly Lock Gate = new();
    private static string? _path;
    private static bool _console;

    public static void Init(string dir, bool console)
    {
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "agent.log");
        _console = console;
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 2 << 20) File.Move(_path, _path + ".old", overwrite: true);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERR", ex is null ? message : $"{message}：{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            if (_console) Console.WriteLine(line);
            if (_path is null) return;
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch (IOException) { }
        }
    }
}
