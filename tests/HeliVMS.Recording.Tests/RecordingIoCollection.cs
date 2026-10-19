namespace HeliVMS.Recording.Tests;

/// <summary>
/// 會起真實 ffmpeg 行程／寫真實檔案的測試類別所屬的集合（不與其他集合並行）。
/// </summary>
/// <remarks>
/// 見 AGENTS.md 的行程 landmine：同一台機器上並行跑多個解碼行程會讓「時間／幀數」類斷言
/// 不準，也讓 CI runner 的負載變成隨機因素。Rtc.Tests 的 rtc-io 是同一招；這裡是
/// Recording 專案自己的版本，因為 xunit 的 CollectionDefinition 無法跨 assembly 共用。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RecordingIoCollection
{
    /// <summary>集合名稱。</summary>
    public const string Name = "recording-io";
}
