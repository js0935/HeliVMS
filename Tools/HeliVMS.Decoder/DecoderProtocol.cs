using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace HeliVMS.Decoder;

[JsonSerializable(typeof(OpenPayload))]
[JsonSerializable(typeof(SeekPayload))]
[JsonSerializable(typeof(RatePayload))]
[JsonSerializable(typeof(FpsPayload))]
[JsonSerializable(typeof(PositionPayload))]
[JsonSerializable(typeof(StatusPayload))]
[JsonSerializable(typeof(ErrorPayload))]
[JsonSerializable(typeof(FrameInfoPayload))]
internal sealed partial class DecoderProtocolContext : JsonSerializerContext {
}

internal static class DecoderProtocolSerializer {
    public static readonly JsonSerializerContext Context = DecoderProtocolContext.Default;
}

/// <summary>Named Pipe 訊息類型</summary>
internal enum DecoderMessageType
{
    // 主處理序 → 解碼器命令
    CmdOpen = 1,
    CmdSeek = 2,
    CmdSetRate = 3,
    CmdPause = 4,
    CmdResume = 5,
    CmdStop = 6,
    CmdExit = 7,
    CmdSetTargetFps = 8,

    // 解碼器 → 主處理序事件
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
    [JsonPropertyName("targetH")] public int TargetDecodeHeight { get; set; }
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
internal readonly struct FrameInfoHeader(long pts, int width, int height, int size)
{
    public readonly long Pts = pts;
    public readonly int Width = width;
    public readonly int Height = height;
    public readonly int Size = size;
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
