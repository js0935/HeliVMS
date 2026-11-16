namespace HeliVMS.Shared;

/// <summary>
/// IO 接點／模組通道在畫面上的名稱：一定要看得出「哪一顆模組（或哪一台攝影機）的哪個接點」。
/// </summary>
/// <remarks>
/// <para>
/// 需要這一層的理由：ADAM-6250 的通道名固定是 <c>DI 1</c>…<c>DI 8</c>／<c>DO 1</c>…，攝影機端子叫
/// <c>di_i1</c>。兩顆模組（或兩台同型攝影機）的同名接點，在警報規則的下拉、地圖設定頁的下拉與
/// 地圖圖釘上會變成兩個<b>看起來一模一樣</b>的選項——選錯等於驅動錯的繼電器，事後看規則清單也
/// 分不出來。把來源（模組名／攝影機名）放在最前面，是唯一不需要操作員記住內部編號的做法。
/// </para>
/// <para>
/// 名稱推導只放一份：三個畫面各自拼字串遲早漂移，而漂移的症狀是「同一個接點在規則裡叫 A、
/// 在地圖上叫 B」，兩邊都沒有錯誤訊息。
/// </para>
/// </remarks>
public static class IoContactLabel
{
    /// <summary>來源與接點之間的分隔字串（三個畫面共用，改這裡就全部一起改）。</summary>
    public const string Separator = " · ";

    /// <summary>
    /// 接點名稱：<c>{來源} · DI#3 名稱</c>；沒有來源時維持舊格式 <c>DI#3 名稱</c>。
    /// </summary>
    /// <remarks>
    /// 沒有來源是合法狀態（舊資料沒有 <c>io_channel_id</c>、端子的攝影機已被刪除），
    /// 那時退回舊格式而不是顯示空白：規則仍然指著一個真實存在的接點。
    /// </remarks>
    public static string Port(string kind, int number, string portName, string? source)
    {
        var basic = $"{kind}#{number} {portName}";
        return string.IsNullOrWhiteSpace(source)
            ? basic
            : $"{source.Trim()}{Separator}{basic}";
    }

    /// <summary>
    /// 模組通道名稱：<c>{模組名} · DI 1</c>；模組名空白時用 <c>#模組編號</c>。
    /// </summary>
    /// <remarks>
    /// 模組名空白只會出現在「沒有經過 <c>IoModuleEnroller</c> 直接寫進資料庫」的舊資料上；
    /// 顯示編號至少讓操作員知道自己選的是不同的兩顆，而不是同一個接點出現兩次。
    /// </remarks>
    public static string ModuleChannel(string channelName, string? moduleName, int deviceId)
    {
        var source = string.IsNullOrWhiteSpace(moduleName) ? $"#{deviceId}" : moduleName.Trim();
        return $"{source}{Separator}{channelName}";
    }
}
