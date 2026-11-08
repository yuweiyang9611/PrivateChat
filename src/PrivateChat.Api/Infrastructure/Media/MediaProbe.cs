using System.Globalization;
using System.Text.Json;
using ImageMagick;

namespace PrivateChat.Api.Infrastructure.Media;

public sealed record MediaInfo(int? Width, int? Height, double? DurationSeconds, DateTimeOffset? TakenAt);

/// <summary>ffprobe 的视频结果：显示宽高（已按旋转纠正）、编码、像素格式、旋转角。</summary>
public sealed record VideoProbe(int Width, int Height, double? DurationSeconds, string? Codec, string? PixelFormat, int Rotation, DateTimeOffset? CreatedAt);

/// <summary>媒体元数据解析（纯函数部分，便于单测）。</summary>
public static class MediaProbe
{
    /// <summary>
    /// 图片：读头部（不解码像素）取宽高与 EXIF 拍摄时间。EXIF 时间没有时区时，按家庭所在时区解释
    /// （OffsetTimeOriginal 有就用它）。EXIF 方向 5–8 表示旋转 90°，宽高互换成显示尺寸。
    /// </summary>
    public static MediaInfo ProbeImage(string path, TimeZoneInfo familyZone)
    {
        using var image = new MagickImage();
        image.Ping(path);
        var w = (int)image.Width;
        var h = (int)image.Height;
        if (image.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom)
            (w, h) = (h, w);
        DateTimeOffset? taken = null;
        var exif = image.GetExifProfile();
        if (exif is not null)
        {
            var raw = exif.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif.GetValue(ExifTag.DateTimeDigitized)?.Value;
            var offset = exif.GetValue(ExifTag.OffsetTimeOriginal)?.Value;
            taken = ParseExifDate(raw, offset, familyZone);
        }
        return new MediaInfo(w, h, null, taken);
    }

    /// <summary>"2026:10:03 14:22:05" + 可选 "+08:00"。不合法或明显不合理（相机没设时间的 0000 年、未来）返回 null。</summary>
    public static DateTimeOffset? ParseExifDate(string? raw, string? offset, TimeZoneInfo familyZone)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTime.TryParseExact(raw.Trim().TrimEnd('\0'), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        if (local.Year < 1971) return null;
        DateTimeOffset result;
        // "+08:00" / "-05:00"：符号单独处理（TimeSpan 的 hh:mm 格式不接受符号）
        var o = offset?.Trim().TrimEnd('\0');
        // 合法时区偏移不超过 ±14 小时（DateTimeOffset 也只接受这个范围），乱写的按家庭时区算
        if (!string.IsNullOrEmpty(o) && o[0] is '+' or '-' && TimeSpan.TryParseExact(o[1..], @"hh\:mm", CultureInfo.InvariantCulture, out var span)
            && span <= TimeSpan.FromHours(14))
            result = new DateTimeOffset(local, o[0] == '-' ? -span : span);
        else
            result = new DateTimeOffset(local, familyZone.GetUtcOffset(local));
        return result > DateTimeOffset.UtcNow.AddDays(2) ? null : result;
    }

    /// <summary>解析 <c>ffprobe -print_format json -show_format -show_streams</c> 的输出。</summary>
    public static VideoProbe? ParseFfprobe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams)) return null;
        JsonElement? video = null;
        foreach (var s in streams.EnumerateArray())
            if (s.TryGetProperty("codec_type", out var t) && t.GetString() == "video" && !IsAttachedPicture(s)) { video = s; break; }
        if (video is not { } v) return null;

        var w = v.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
        var h = v.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
        var rotation = 0;
        if (v.TryGetProperty("tags", out var vt) && vt.TryGetProperty("rotate", out var rot) && int.TryParse(rot.GetString(), out var r1)) rotation = r1;
        if (v.TryGetProperty("side_data_list", out var sdl))
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r2) && r2.TryGetInt32(out var rv)) rotation = rv;
        if (Math.Abs(rotation) % 180 == 90) (w, h) = (h, w);

        double? duration = null;
        DateTimeOffset? created = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)) duration = dv;
            if (format.TryGetProperty("tags", out var ft))
            {
                // iPhone 的 com.apple.quicktime.creationdate 带本地时区，比 creation_time（UTC）更准
                foreach (var key in new[] { "com.apple.quicktime.creationdate", "creation_time" })
                    if (ft.TryGetProperty(key, out var c) && DateTimeOffset.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cv) && cv.Year > 1971)
                    {
                        created = cv;
                        break;
                    }
            }
        }
        if (duration is null && v.TryGetProperty("duration", out var vd) && double.TryParse(vd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var vdv)) duration = vdv;
        var codec = v.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
        var pix = v.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
        return new VideoProbe(w, h, duration, codec, pix, rotation, created);
    }

    private static bool IsAttachedPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;

    /// <summary>浏览器都能直接播：H.264 + 8 bit 4:2:0、MP4 / MOV 容器、长边不超过 1920。否则转 720p。</summary>
    public static bool NeedsTranscode(VideoProbe p, string mime)
    {
        var container = mime is "video/mp4" or "video/quicktime" or "video/x-m4v";
        var codec = p.Codec is "h264";
        var pixel = p.PixelFormat is null or "yuv420p" or "yuvj420p";
        var size = Math.Max(p.Width, p.Height) <= 1920;
        return !(container && codec && pixel && size);
    }

    public static TimeZoneInfo Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
