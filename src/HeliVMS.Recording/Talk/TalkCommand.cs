namespace HeliVMS.Recording.Talk;

/// <summary>
/// 要交給 ffmpeg 執行的對講指令（純資料，方便在測試中逐一斷言參數）。
/// </summary>
/// <remarks>
/// <see cref="TargetUrl"/> 只給日誌與診斷用；實際送出的是 <see cref="Arguments"/> 內的那一份，
/// 兩者刻意分開，讓呼叫端在寫稽核時能取得去識別化後的位址。
/// </remarks>
public sealed record TalkCommand(string FileName, IReadOnlyList<string> Arguments, string TargetUrl);
