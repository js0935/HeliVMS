namespace HeliVMS.Media.Tests;

/// <summary>
/// 會啟動真實外部行程（ffmpeg）的測試集合：不與其他集合並行執行。
/// </summary>
/// <remarks>
/// 與 Recording 的 <c>RecordingIoCollection</c> 同一理由：xunit 預設讓不同集合並行，
/// 在低規格 runner 上同時啟動多個行程會造成 CreateProcess 節流／逾時。行程相關的測試
/// 集中到這個集合，才不會拖累與它無關的測試。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaIoCollection
{
    public const string Name = "media-io";
}
