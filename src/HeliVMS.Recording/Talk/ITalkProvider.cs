namespace HeliVMS.Recording.Talk;

/// <summary>
/// 對講供應商：把一個 <see cref="TalkRequest"/> 解析成對講目標，再組出 ffmpeg 指令。
/// </summary>
/// <remarks>
/// 不同廠牌的對講通道位址與封包方式不同（§14.1 #11），這個介面把它們收斂成同一個出口。
/// 介面本身刻意不碰行程或網路，所以每個方法都能以純函式測試；
/// 真正啟動子行程的是 <see cref="TalkService"/>。
/// </remarks>
public interface ITalkProvider
{
    /// <summary>供應商名稱（用於診斷、顯示與稽核）。</summary>
    string Name { get; }

    /// <summary>此供應商是否能處理該請求。</summary>
    bool CanHandle(TalkRequest request);

    /// <summary>解析對講目標（位址、傳輸方式）。</summary>
    TalkTarget Resolve(TalkRequest request);

    /// <summary>組出把音訊送到 <paramref name="target"/> 的 ffmpeg 指令。</summary>
    TalkCommand BuildCommand(TalkRequest request, TalkTarget target);
}
