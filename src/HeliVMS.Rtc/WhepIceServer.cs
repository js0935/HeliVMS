namespace HeliVMS.Rtc;

/// <summary>一個 TURN 伺服器及其認證憑證。</summary>
/// <param name="Url">TURN URL（<c>turn:</c>／<c>turns:</c>），例如 <c>turn:turn.example.com:3478</c>。</param>
/// <param name="Username">使用者名稱；<c>null</c> 表示這台 TURN 不需要認證。</param>
/// <param name="Credential">密碼或 TURN REST 動態產生的短期密碼。</param>
/// <remarks>
/// 用 record struct 而不是字串：認證資訊是最需要被單元測試與被遮蔽的東西，
/// 讓它散落在一個「用分號隔開的字串」裡只會讓錯誤難以發現。
/// </remarks>
public readonly record struct WhepIceServer(string Url, string? Username, string? Credential);