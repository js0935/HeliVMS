namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 會碰真實 OS 資源的測試集合：loopback UDP 埠、真實 ffmpeg 子行程、真實 RTP 串流。
/// <para>
/// 這些類別掛上 <c>[Collection("rtc-io")]</c>，而本定義讓它們<b>不與任何其他集合並行</b>。
/// </para>
/// <para>
/// 理由不是潔癖，是一個具體的生產症狀：<c>第一個RTP封包到達前不會回應</c> 只在 CI 上
/// 間歇失敗（本地重跑 12 次全綠），而它的斷言本質是「在 300 毫秒內<b>不該</b>有完成」
/// ——時序敏感。同一個 assembly 裡還有會真的起 ffmpeg、真的發 RTP 的類別
/// （LivePublisherTests、FfmpegEncoderProbeTests、PublisherIntegrationTests、
/// WhepLoopbackTests），而 xunit 預設以「類別」為單位並行。這些測試會同時改變兩件事：
/// loopback 目的埠可能被回收後指派給別的測試，而真實 ffmpeg 行程與 thread pool 壓力會
/// 讓 <c>Task.Delay(300)</c> 不準時，兩邊都足以讓這個斷言誤判。
/// </para>
/// <para>
/// 這跟 AGENTS.md 已記的 UDP 埠 landmine 是同一個根因（當年是靠「先綁住真實 socket」
/// 解決的，但那只保護自己的 socket，擋不住別人的封包打上來）。
/// 把資源敏感的那一組序列化，是讓這條時間斷言變得可信的前提。
/// </para>
/// <para>
/// <b>誠實說明：</b>這是緩解措施，不是已證實的修法。`第一個RTP封包到達前不會回應` 的
/// CI 失敗至今無法在本地重現（單獨 15 次、整個 suite 4 次全綠），而失敗訊息只說
/// 「Values are the same instance」，分不出是產品提前回 201 還是協商路徑出錯。
/// 同一個 commit 裡因此也把該斷言的失敗訊息改成會列出 Task 狀態與例外，
/// 下一次真的發生時才能從日誌直接判斷該修哪一處。
/// </para>
/// <para>
/// 沒有把整個 assembly 的並行關掉：剩下那幾個純計算的類別
/// （RtpHeaderTests、WhepSessionStoreTests、IceCandidateRewriterTests、
/// LiveEncodeOptionsTests、RedactingErrorBufferTests）不碰 socket、不起行程，
/// 仍然照跑並行。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RtcIoCollection
{
    public const string Name = "rtc-io";
}
