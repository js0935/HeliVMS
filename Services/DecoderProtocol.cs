using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace HeliVMS.Services;

internal enum DecoderMessageType
{
    CmdOpen = 1,
    CmdSeek = 2,
    CmdSetRate = 3,
    CmdPause = 4,
    CmdResume = 5,
    CmdStop = 6,
    CmdExit = 7,
    CmdSetTargetFps = 8,

    EvtFrameInfo = 101,
    EvtFrameData = 102,
    EvtPosition = 103,
    EvtStatus = 104,
    EvtEof = 105,
    EvtError = 106,
}

internal class OpenPayload
{
    [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
    [JsonPropertyName("seekUs")] public long SeekMicroseconds { get; set; }
}

internal class SeekPayload
{
    [JsonPropertyName("us")] public long Microseconds { get; set; }
}

internal class RatePayload
{
    [JsonPropertyName("rate")] public double Rate { get; set; }
}

internal class FpsPayload
{
    [JsonPropertyName("fps")] public double Fps { get; set; }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct FrameInfoHeader
{
    public readonly long Pts;
    public readonly int Width;
    public readonly int Height;
    public readonly int Size;

    public FrameInfoHeader(long pts, int width, int height, int size)
    {
        Pts = pts;
        Width = width;
        Height = height;
        Size = size;
    }
}

internal class FrameInfoPayload
{
    [JsonPropertyName("pts")] public long Pts { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }
    [JsonPropertyName("size")] public int Size { get; set; }
}

internal class PositionPayload
{
    [JsonPropertyName("pts")] public long Pts { get; set; }
    [JsonPropertyName("dur")] public long Duration { get; set; }
}

internal class StatusPayload
{
    [JsonPropertyName("playing")] public bool Playing { get; set; }
}

internal class ErrorPayload
{
    [JsonPropertyName("msg")] public string Message { get; set; } = "";
}
