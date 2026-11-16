using System;
using Xunit;

namespace AuraToggle.Tests;

public class RyuoFrameTests
{
    /// <summary>
    /// The published worked example of a <c>GET spec</c> request: markers, length 67 and checksum
    /// 0xD0. A mismatch here means the screen would ignore every later command.
    /// </summary>
    [Fact]
    public void GetSpecMatchesTheWorkedExample()
    {
        const string hex = "5A0043474554207370656320310D0A436F6E74656E74547970653D6A736F6E0D0A" +
            "436F6E74656E744C656E6774683D300D0A5365714E756D6265723D310D0A0D0AD05A";

        Assert.True(RyuoFrame.TryEncode("GET", "spec", 1, "", out byte[] frame));
        Assert.Equal(hex, Convert.ToHexString(frame));
    }

    [Fact]
    public void BracketsAndZRoundTrip()
    {
        const string payload = "{\"items\":[\"Z\"]}";

        Assert.True(RyuoFrame.TryEncode("POST", "preset", 4, payload, out byte[] frame));
        Assert.Contains((byte)0x5B, frame);
        Assert.True(RyuoFrame.TryDecode(frame, out RyuoMessage message));
        Assert.Equal(payload, message.Payload);
        Assert.Equal(0, message.Ack);
        Assert.StartsWith("POST preset 1", message.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrameThatDoesNotFitIsRefused()
    {
        string payload = new string('A', RyuoFrame.ReportBytes);
        Assert.False(RyuoFrame.TryEncode("POST", "config", 1, payload, out _));
    }

    [Fact]
    public void LayoutKeepsSixKnownSlots()
    {
        var layout = new RyuoLayout
        {
            Items = new[] { "CPU Load", "nope", "Date&Time" },
        };

        Assert.Equal(new[] { "CPU Load", "", "Date&Time", "", "", "" }, layout.NormalItems());
        Assert.Equal(102, new RyuoLayout { Brightness = 100 }.WireBrightness);
        Assert.Equal(85, new RyuoLayout { Brightness = 85 }.WireBrightness);
    }

    [Fact]
    public void PlayModeRejectsAnythingElse()
    {
        string json = new RyuoLayout { PlayMode = "loop", Media = "a.mp4" }.ConfigJson("CPU", "GPU");
        Assert.Contains("\"playMode\":\"Single\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loop", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigAcceptsANameThatHasNotBeenRead()
    {
        string json = new RyuoLayout().ConfigJson(null, null);
        Assert.Contains("\"cpu\":\"\"", json, StringComparison.Ordinal);
        Assert.Contains("\"gpu\":\"\"", json, StringComparison.Ordinal);
    }
}
