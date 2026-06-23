using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace HeliVMS.Services;

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

/// <summary>
/// Named Pipe 通訊協定 — 主程式與 HeliVMS.Decoder.exe 之間的訊息類型。
/// 訊息格式：8 位元組標頭 (MsgType:int + PayloadLen:int) + JSON 承載 (可選)。
/// 幀資料 (EvtFrameData) 不經 JSON 序列化，直接以 PooledBuffer 傳送二進位 BGR24。
/// </summary>
internal enum DecoderMessageType {
    // === 命令（主程式 → 解碼器）===
    CmdOpen = 1,         // 開啟檔案並開始解碼
    CmdSeek = 2,         // 跳至指定時間點（微秒）
    CmdSetRate = 3,      // 設定播放速率
    CmdPause = 4,        // 暫停解碼
    CmdResume = 5,       // 恢復解碼
    CmdStop = 6,         // 停止解碼
    CmdExit = 7,         // 優雅結束程序
    CmdSetTargetFps = 8, // 設定目標輸出幀率

    // === 事件（解碼器 → 主程式）===
    EvtFrameInfo = 101,  // 幀資訊（寬、高、PTS、大小）
    EvtFrameData = 102,  // 二進位幀資料（BGR24）
    EvtPosition = 103,   // 播放位置變更
    EvtStatus = 104,     // 播放/暫停狀態
    EvtEof = 105,        // 檔案播放完畢
    EvtError = 106,      // 錯誤訊息
}

/// <summary>CmdOpen 的承載：指定檔案路徑、起始時間及目標解碼高度</summary>
internal class OpenPayload {
    [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
    [JsonPropertyName("seekUs")] public long SeekMicroseconds { get; set; }
    [JsonPropertyName("targetH")] public int TargetDecodeHeight { get; set; } // 0 = 原始解析度
}

/// <summary>CmdSeek 的承載：跳至指定微秒位置</summary>
internal class SeekPayload {
    [JsonPropertyName("us")] public long Microseconds { get; set; }
}

/// <summary>CmdSetRate 的承載：播放速率倍率 (0.25 ~ 32)</summary>
internal class RatePayload {
    [JsonPropertyName("rate")] public double Rate { get; set; }
}

/// <summary>CmdSetTargetFps 的承載：目標輸出幀率上限</summary>
internal class FpsPayload {
    [JsonPropertyName("fps")] public double Fps { get; set; }
}

/// <summary>EvtFrameInfo 的二進位標頭（MemoryMarshal.Read 直接解析）</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct FrameInfoHeader(long pts, int width, int height, int size) {
    public readonly long Pts = pts;    // 該幀 PTS（微秒）
    public readonly int Width = width;   // 幀寬度
    public readonly int Height = height;  // 幀高度
    public readonly int Size = size;    // 後續幀資料大小
}

/// <summary>EvtFrameInfo 的 JSON 替代格式（當二進位版本不適用時）</summary>
internal class FrameInfoPayload {
    [JsonPropertyName("pts")] public long Pts { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }
    [JsonPropertyName("size")] public int Size { get; set; }
}

/// <summary>EvtPosition：播放位置與總時長</summary>
internal class PositionPayload {
    [JsonPropertyName("pts")] public long Pts { get; set; }
    [JsonPropertyName("dur")] public long Duration { get; set; }
}

/// <summary>EvtStatus：播放/暫停狀態</summary>
internal class StatusPayload {
    [JsonPropertyName("playing")] public bool Playing { get; set; }
}

/// <summary>EvtError：解碼器錯誤訊息</summary>
internal class ErrorPayload {
    [JsonPropertyName("msg")] public string Message { get; set; } = "";
}
