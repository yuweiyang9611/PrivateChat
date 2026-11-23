using System.Runtime.InteropServices;

namespace PrivateChat.Agent.Video;

/// <summary>D3D11_MAPPED_SUBRESOURCE。数据只在 Map 和 Unmap 之间有效。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MappedSubresource
{
    public IntPtr Data;
    public uint RowPitch, DepthPitch;

    public readonly unsafe void CopyNv12To(int width, int height, Span<byte> destination)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new InvalidDataException($"NV12 尺寸 {width}x{height} 必须是正的偶数");
        var rows = height + height / 2L;
        if (width * rows > destination.Length)
            throw new InvalidDataException($"NV12 缓冲区 {destination.Length} 字节，放不下 {width}x{height}");
        if (Data == IntPtr.Zero) throw new InvalidDataException("读回 NV12 时映射地址为空");
        if (RowPitch < width) throw new InvalidDataException($"读回 NV12 的行距 {RowPitch} 比宽度 {width} 还小");

        // D3D11 的 NV12 staging Texture2D 有 height 行 Y 和紧随其后的 height/2 行交错 UV，
        // 两者的行距都是 RowPitch。DepthPitch 用来跨越三维深度切片，在二维纹理上不能用来判断映射长度。
        var sourceLength = (rows - 1) * RowPitch + width;
        if (sourceLength > int.MaxValue)
            throw new InvalidDataException($"读回 NV12 的跨度超出可处理范围（行距 {RowPitch}，{width}x{height}）");
        var source = new ReadOnlySpan<byte>((void*)Data, (int)sourceLength);
        var pitch = (int)RowPitch;
        for (var row = 0; row < rows; row++)
            source.Slice(row * pitch, width).CopyTo(destination.Slice(row * width, width));
    }
}
