using System.Runtime.InteropServices;
using Lightcone.Playback;
using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>Runs only where libmpv can be loaded (CI installs it on Linux; release builds bundle it).</summary>
public sealed class MpvFactAttribute : FactAttribute
{
    public MpvFactAttribute()
    {
        if (!MpvLibrary.TryLoad(out var error))
        {
            Skip = error ?? "libmpv not available";
        }
    }
}

public class PlaybackTests
{
    // Simulated Linux / macOS paths are built with the host's separator; compare them with '/'.
    private static string N(string path) => path.Replace('\\', '/');

    [Fact]
    public void LooksInTheBundleBeforeTheSystem()
    {
        var linux = MpvLibrary.Candidates("/opt/Lightcone", OSPlatform.Linux, null).Select(N).ToList();
        Assert.Equal("/opt/Lightcone/lib/libmpv.so.2", linux[0]);
        Assert.Contains("libmpv.so.2", linux);
        Assert.True(linux.IndexOf("/opt/Lightcone/lib/libmpv.so.2") < linux.IndexOf("libmpv.so.2"));

        var mac = MpvLibrary.Candidates("/Applications/Lightcone.app/Contents/MacOS", OSPlatform.OSX, null).Select(N).ToList();
        Assert.Equal("/Applications/Lightcone.app/Contents/Frameworks/libmpv.2.dylib", mac[0]);
        Assert.Contains("/opt/homebrew/lib/libmpv.2.dylib", mac);

        var win = MpvLibrary.Candidates(@"C:\Lightcone", OSPlatform.Windows, null).ToList();
        Assert.EndsWith("libmpv-2.dll", win[0]);

        Assert.Equal("/x/libmpv.so", MpvLibrary.Candidates("/opt", OSPlatform.Linux, "/x/libmpv.so").First());
    }

    [Theory]
    [InlineData("auto", "linux", true, "vaapi,auto-copy-safe")]
    [InlineData("auto", "linux", false, "vaapi-copy,auto-copy-safe")]
    [InlineData(null, "macos", false, "videotoolbox,auto-copy-safe")]
    [InlineData("auto", "windows", false, "auto-copy-safe")]
    [InlineData("copy", "linux", true, "auto-copy-safe")]
    [InlineData("copy", "macos", false, "videotoolbox-copy")]
    [InlineData("off", "linux", true, "no")]
    public void PicksHwdec(string? setting, string os, bool egl, string expected) =>
        Assert.Equal(expected, HwdecPolicy.Resolve(setting, os, egl));

    [Fact]
    public void ParsesTrackList()
    {
        const string json = """
            [{"id":1,"type":"video","codec":"hevc","selected":true},
             {"id":1,"type":"audio","lang":"eng","codec":"eac3","demux-channel-count":6,"default":true,"selected":true},
             {"id":2,"type":"audio","lang":"jpn","codec":"aac","demux-channel-count":2,"title":"Commentary"},
             {"id":1,"type":"sub","lang":"eng","codec":"hdmv_pgs_subtitle","forced":true},
             {"id":2,"type":"sub","codec":"subrip","external":true},
             {"type":"sub"}]
            """;
        var tracks = MediaTrack.ParseList(json);
        Assert.Equal(5, tracks.Count);
        Assert.Equal("English · E-AC-3 5.1", tracks[1].Label);
        Assert.Equal("Japanese · AAC stereo · Commentary", tracks[2].Label);
        Assert.Equal("English · PGS · Forced", tracks[3].Label);
        Assert.Equal("Subtitles · SRT · External", tracks[4].Label);
        Assert.True(tracks[1].Selected);
        Assert.Empty(MediaTrack.ParseList("not json"));
        Assert.Empty(MediaTrack.ParseList(null));
    }

    [Theory]
    [InlineData(10.0, 3600.0, null)]          // not started
    [InlineData(600.0, 3600.0, 600.0)]
    [InlineData(3300.0, 3600.0, null)]        // watched
    [InlineData(600.0, null, 600.0)]        // live/unknown length
    public void DecidesWhatToRemember(double pos, double? dur, double? expected) =>
        Assert.Equal(expected, ResumePolicy.PositionToSave(pos, dur));

    [MpvFact]
    public void PlaysAndRendersAFrameHeadless()
    {
        using var player = new MpvPlayer(new Dictionary<string, string> { ["ao"] = "null", ["hwdec"] = "no" });
        using var rc = MpvRenderContext.CreateSoftware(player);
        player.Load("av://lavfi:testsrc=duration=5:size=320x240:rate=10");
        Assert.NotNull(player.WaitFor(MpvEventId.FileLoaded, TimeSpan.FromSeconds(10)));
        Assert.Equal("320", player.GetString("width") ?? player.GetString("video-params/w"));
        Assert.Contains(player.Tracks, t => t.Kind == TrackKind.Video);

        var buf = new byte[160 * 120 * 4];
        var until = DateTime.UtcNow.AddSeconds(10);
        var drawn = false;
        while (!drawn && DateTime.UtcNow < until)
        {
            player.Poll(0.05);
            if (rc.TakeFrame())
            {
                rc.RenderSoftware(buf, 160, 120);
                drawn = buf.Any(b => b != 0);
            }
        }
        Assert.True(drawn, "no frame rendered");

        player.Seek(2);
        Assert.NotNull(player.WaitFor(MpvEventId.PlaybackRestart, TimeSpan.FromSeconds(10)));
        Assert.InRange(player.Position ?? -1, 1.5, 3.5);
    }
}
