using MediaBoard.Core.Activity;
using MediaBoard.Core.Probing;

namespace MediaBoard.Core.Tests.Probing;

public class FfprobeDurationProbeTests
{
    // Trimmed from `ffprobe -v quiet -print_format json -show_format -show_streams` on an MP4.
    private const string FormatDurationJson = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "duration": "5400.040000" },
            { "index": 1, "codec_type": "audio", "codec_name": "aac" }
          ],
          "format": { "filename": "movie.mp4", "nb_streams": 2, "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "5400.064000", "size": "4123456789" }
        }
        """;

    // Matroska files often carry no format duration; the stream tags have it.
    private const string StreamTagJson = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "hevc", "tags": { "DURATION": "01:30:15.123456789", "NUMBER_OF_FRAMES": "129780" } },
            { "index": 1, "codec_type": "audio", "codec_name": "ac3", "tags": { "language": "eng", "DURATION": "01:30:14.000000000" } }
          ],
          "format": { "filename": "movie.mkv", "nb_streams": 2, "format_name": "matroska,webm", "size": "9876543210" }
        }
        """;

    [Fact]
    public void Ffprobe_ParsesFormatDuration()
    {
        Assert.Equal(5400.064, FfprobeDurationProbe.ParseDuration(FormatDurationJson)!.Value, 6);
    }

    [Fact]
    public void Ffprobe_FallsBackToStreamTagDuration()
    {
        Assert.Equal(90 * 60 + 15.123456789, FfprobeDurationProbe.ParseDuration(StreamTagJson)!.Value, 6);
    }

    [Fact]
    public void Ffprobe_FallsBackToDurationEngTag()
    {
        const string json = """{ "streams": [ { "tags": { "DURATION-eng": "00:22:30.500000000" } } ], "format": { "duration": "N/A" } }""";
        Assert.Equal(22 * 60 + 30.5, FfprobeDurationProbe.ParseDuration(json)!.Value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "format": { "duration": "0.000000" } }""")]
    [InlineData("""{ "format": { "duration": "N/A" }, "streams": [ { "tags": { "DURATION": "garbage" } } ] }""")]
    public void Ffprobe_NoUsableDuration_IsNull(string json)
    {
        Assert.Null(FfprobeDurationProbe.ParseDuration(json));
    }

    [Fact]
    public async Task Ffprobe_Missing_ReturnsNullAndLogsOnce()
    {
        var hub = new ActivityHub();
        var missing = Path.Combine(Path.GetTempPath(), "mediaboard-no-such-dir", "ffprobe.exe");
        var probe = new FfprobeDurationProbe(() => missing, hub);

        Assert.Null(await probe.GetDurationSecondsAsync("a.mkv", CancellationToken.None));
        Assert.Null(await probe.GetDurationSecondsAsync("b.mkv", CancellationToken.None));

        var entry = Assert.Single(hub.Recent);
        Assert.Equal("Durations unavailable: ffprobe not found", entry.Message);
    }
}
