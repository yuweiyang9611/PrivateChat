using PrivateChat.Api.Features.Drive;
using PrivateChat.Api.Infrastructure.Media;

namespace PrivateChat.Api.Tests;

public class MediaProbeTests
{
    private static readonly TimeZoneInfo Shanghai = MediaProbe.Zone("Asia/Shanghai");

    [Fact]
    public void Exif_date_without_offset_uses_family_time_zone()
    {
        var t = MediaProbe.ParseExifDate("2026:10:03 14:22:05", null, Shanghai);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 22, 5, TimeSpan.FromHours(8)), t);
    }

    [Theory]
    [InlineData("+09:00", 9)]
    [InlineData("-05:00", -5)]
    [InlineData("+00:00", 0)]
    public void Exif_offset_wins_over_family_zone(string offset, int hours)
    {
        var t = MediaProbe.ParseExifDate("2026:10:03 14:22:05", offset, Shanghai);
        Assert.Equal(TimeSpan.FromHours(hours), t!.Value.Offset);
        Assert.Equal(14, t.Value.Hour);
    }

    [Theory]
    [InlineData("+25:00")]
    [InlineData("-14:30")]
    public void Out_of_range_exif_offset_falls_back_to_family_zone(string offset)
    {
        var t = MediaProbe.ParseExifDate("2026:10:03 14:22:05", offset, Shanghai);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 22, 5, TimeSpan.FromHours(8)), t);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("not a date")]
    [InlineData("1970:01:01 00:00:00")]
    [InlineData("2099:01:01 00:00:00")]
    public void Bad_or_unset_exif_dates_are_ignored(string? raw)
        => Assert.Null(MediaProbe.ParseExifDate(raw, null, Shanghai));

    private const string IphoneHevc = """
        {"streams":[
          {"codec_type":"video","codec_name":"hevc","pix_fmt":"yuv420p10le","width":1920,"height":1080,"duration":"5.2",
           "side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]},
          {"codec_type":"audio","codec_name":"aac"}],
         "format":{"duration":"5.233","tags":{"creation_time":"2026-10-03T06:22:05.000000Z","com.apple.quicktime.creationdate":"2026-10-03T14:22:05+0800"}}}
        """;

    [Fact]
    public void Ffprobe_rotation_swaps_dimensions_and_prefers_apple_creation_date()
    {
        var p = MediaProbe.ParseFfprobe(IphoneHevc)!;
        Assert.Equal((1080, 1920), (p.Width, p.Height));
        Assert.Equal(-90, p.Rotation);
        Assert.Equal("hevc", p.Codec);
        Assert.Equal(5.233, p.DurationSeconds);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 22, 5, TimeSpan.FromHours(8)), p.CreatedAt);
        Assert.True(MediaProbe.NeedsTranscode(p, "video/quicktime"));
    }

    [Fact]
    public void Ffprobe_ignores_cover_art_streams()
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"mjpeg","width":300,"height":300,"disposition":{"attached_pic":1}},
                        {"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":1280,"height":720,"tags":{"rotate":"90"}}],
             "format":{"duration":"10"}}
            """;
        var p = MediaProbe.ParseFfprobe(json)!;
        Assert.Equal(("h264", 720, 1280), (p.Codec, p.Width, p.Height));
    }

    [Fact]
    public void Audio_only_has_no_video_probe() => Assert.Null(MediaProbe.ParseFfprobe("""{"streams":[{"codec_type":"audio"}],"format":{}}"""));

    [Theory]
    [InlineData("h264", "yuv420p", 1920, 1080, "video/mp4", false)]
    [InlineData("h264", "yuv420p", 1080, 1920, "video/quicktime", false)]
    [InlineData("h264", "yuv420p", 3840, 2160, "video/mp4", true)]
    [InlineData("hevc", "yuv420p", 1280, 720, "video/mp4", true)]
    [InlineData("h264", "yuv420p10le", 1280, 720, "video/mp4", true)]
    [InlineData("vp9", "yuv420p", 1280, 720, "video/webm", true)]
    [InlineData("h264", "yuv420p", 1280, 720, "video/x-matroska", true)]
    public void Transcode_only_what_browsers_cannot_play(string codec, string pix, int w, int h, string mime, bool expected)
        => Assert.Equal(expected, MediaProbe.NeedsTranscode(new VideoProbe(w, h, 10, codec, pix, 0, null), mime));

    [Theory]
    [InlineData("照片.jpg", false, "照片 (2).jpg")]
    [InlineData("照片 (1).jpg", false, "照片 (1) (1).jpg")]
    [InlineData("旅行", true, "旅行 (2)")]
    [InlineData("archive.tar.gz", false, "archive.tar (1).gz")]
    public void Next_free_name_numbers_conflicts(string name, bool folder, string expected)
    {
        var taken = new HashSet<string> { "照片.jpg", "照片 (1).jpg", "旅行", "旅行 (1)", "archive.tar.gz" };
        Assert.Equal(expected, DriveNames.NextFree(name, folder, taken));
        Assert.Equal("新的.jpg", DriveNames.NextFree("新的.jpg", false, taken));
    }

    [Theory]
    [InlineData("  报告.pdf  ", "报告.pdf")]
    [InlineData("a/b\\c:d.txt", "a_b_c_d.txt")]
    [InlineData("..", "未命名")]
    [InlineData("", "未命名")]
    public void Upload_names_are_sanitized(string input, string expected) => Assert.Equal(expected, DriveNames.Sanitize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("tab\there")]
    public void Typed_names_are_validated(string input)
        => Assert.Throws<PrivateChat.Api.Infrastructure.ApiException>(() => DriveNames.Validate(input));
}
