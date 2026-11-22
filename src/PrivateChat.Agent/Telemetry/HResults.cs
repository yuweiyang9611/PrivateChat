using System.Runtime.InteropServices;
using PrivateChat.Agent.Session;
using PrivateChat.Agent.Video;

namespace PrivateChat.Agent.Telemetry;

/// <summary>探测里常见的错误码写成名字（DXGI_ERROR_UNSUPPORTED（0x887A0004）这样），管理员在诊断里一眼能认出来。</summary>
internal static class HResults
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [unchecked((int)0x80004001)] = "E_NOTIMPL",
        [unchecked((int)0x80004002)] = "E_NOINTERFACE",
        [unchecked((int)0x80004003)] = "E_POINTER",
        [unchecked((int)0x80004005)] = "E_FAIL",
        [unchecked((int)0x8000FFFF)] = "E_UNEXPECTED",
        [unchecked((int)0x80070005)] = "E_ACCESSDENIED",
        [unchecked((int)0x8007000E)] = "E_OUTOFMEMORY",
        [unchecked((int)0x80070057)] = "E_INVALIDARG",
        [unchecked((int)0x80040154)] = "REGDB_E_CLASSNOTREG",
        [unchecked((int)0x887A0001)] = "DXGI_ERROR_INVALID_CALL",
        [unchecked((int)0x887A0002)] = "DXGI_ERROR_NOT_FOUND",
        [unchecked((int)0x887A0004)] = "DXGI_ERROR_UNSUPPORTED",
        [unchecked((int)0x887A0005)] = "DXGI_ERROR_DEVICE_REMOVED",
        [unchecked((int)0x887A0006)] = "DXGI_ERROR_DEVICE_HUNG",
        [unchecked((int)0x887A0007)] = "DXGI_ERROR_DEVICE_RESET",
        [unchecked((int)0x887A0020)] = "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
        [unchecked((int)0x887A0022)] = "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE",
        [unchecked((int)0x887A0026)] = "DXGI_ERROR_ACCESS_LOST",
        [unchecked((int)0x887A0027)] = "DXGI_ERROR_WAIT_TIMEOUT",
        [unchecked((int)0x887A0028)] = "DXGI_ERROR_SESSION_DISCONNECTED",
        [unchecked((int)0x887A002B)] = "DXGI_ERROR_ACCESS_DENIED",
        [unchecked((int)0x887A002D)] = "DXGI_ERROR_SDK_COMPONENT_MISSING",
        [unchecked((int)0xC00D36B4)] = "MF_E_INVALIDMEDIATYPE",
        [unchecked((int)0xC00D36B5)] = "MF_E_NOTACCEPTING",
        [unchecked((int)0xC00D36B6)] = "MF_E_NOT_INITIALIZED",
        [unchecked((int)0xC00D36B9)] = "MF_E_NO_MORE_TYPES",
        [unchecked((int)0xC00D36BA)] = "MF_E_UNSUPPORTED_SERVICE",
        [unchecked((int)0xC00D36BB)] = "MF_E_UNEXPECTED",
        [unchecked((int)0xC00D36BD)] = "MF_E_INVALIDTYPE",
        [unchecked((int)0xC00D36E6)] = "MF_E_ATTRIBUTENOTFOUND",
        [unchecked((int)0xC00D3E80)] = "MF_E_NO_EVENTS_AVAILABLE",
        [unchecked((int)0xC00D3E85)] = "MF_E_SHUTDOWN",
        [unchecked((int)0xC00D6D60)] = "MF_E_TRANSFORM_TYPE_NOT_SET",
        [unchecked((int)0xC00D6D61)] = "MF_E_TRANSFORM_STREAM_CHANGE",
        [unchecked((int)0xC00D6D72)] = "MF_E_TRANSFORM_NEED_MORE_INPUT",
        [unchecked((int)0xC00D6D74)] = "MF_E_TRANSFORM_CANNOT_CHANGE_MEDIATYPE_WHILE_PROCESSING",
        [unchecked((int)0xC00D6D76)] = "MF_E_UNSUPPORTED_D3D_TYPE",
        [unchecked((int)0xC00D6D77)] = "MF_E_TRANSFORM_ASYNC_LOCKED",
    };

    /// <summary>"名字（0x……）"，不认识的只写十六进制。</summary>
    public static string Describe(int hr) => Names.TryGetValue(hr, out var name) ? $"{name}（0x{hr:X8}）" : $"0x{hr:X8}";

    /// <summary>异常写成一行给人看的说明：Media Foundation / COM 的错误码换成名字。</summary>
    public static string Explain(Exception ex) => ex switch
    {
        MediaFoundationException mf when Names.TryGetValue(mf.HResult2, out var name) => $"{mf.Message}（{name}）",
        COMException com when !com.Message.Contains("0x", StringComparison.OrdinalIgnoreCase) => $"{com.Message}：{Describe(com.HResult)}",
        _ => ex.Message,
    };

    /// <summary>
    /// 压成一行、文件路径换成"&lt;路径&gt;"（和每次远程的汇总一样，见 <see cref="SessionTelemetry.ScrubPaths"/>：路径里可能有用户名；
    /// 先换再截，截断的路径才不会漏一半）、截到 max 个字（服务器收 300 字；探测结果里的说明短一点，整份才不会超过 64 KB）。
    /// 探测结果里出错的说明（Problems、编码器 / 试编 / 截屏的 Error）都经这里。
    /// </summary>
    public static string Clip(string text, int max = 200)
    {
        var line = SessionTelemetry.ScrubPaths(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return line.Length > max ? line[..max] : line;
    }
}
