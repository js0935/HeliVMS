namespace HeliVMS.Shared;

using System.Text.RegularExpressions;

/// <summary>
/// 建立含認證資訊之 RTSP 位址（ONVIF GetStreamUri 回傳的位址不含帳密，ffprobe／播放器需另行帶入）。
/// </summary>
public static class RtspUri
{
    /// <summary>
    /// 比對「scheme://user:pass@host」形式的 userinfo。用於遮蔽夾雜在任意文字中的位址：
    /// ffmpeg 會以 <c>Input #0, rtsp, from 'rtsp://user:pass@host/...'</c> 形式回顯輸入網址，
    /// 整段並非合法 URI，故 <see cref="Redact"/> 無法處理，必須逐段取代 userinfo。
    /// </summary>
    private static readonly Regex EmbeddedUserInfo = new(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)[^\s/?#]*@",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 將帳密嵌入 RTSP 位址；帳密會依 URL 語法跳脫。帳號空白或位址無法解析時原樣返回。
    /// </summary>
    public static string WithCredentials(string rtspUrl, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(rtspUrl) ||
            string.IsNullOrWhiteSpace(username) ||
            !Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri))
        {
            return rtspUrl;
        }

        // UriBuilder 會以跳脫形式寫出 UserName／Password，故含 @ : / 的帳密亦安全。
        var builder = new UriBuilder(uri)
        {
            UserName = username,
            Password = password ?? string.Empty,
        };

        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// 取出 RTSP 位址的主機名稱／IP；無法解析時回傳空字串。
    /// 用於由既有頻道的裸位址回推對應的設備憑證（舊資料 device_id 可能為 NULL）。
    /// </summary>
    public static string Host(string rtspUrl) =>
        !string.IsNullOrWhiteSpace(rtspUrl) &&
        Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri) &&
        !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : string.Empty;

    /// <summary>
    /// 遮蔽位址中的帳密，供錯誤訊息／日誌使用。位址不含 userinfo 或無法解析時原樣返回。
    /// 避免連線失敗時把明文密碼帶到 UI 或稽核記錄。
    /// </summary>
    public static string Redact(string rtspUrl)
    {
        if (string.IsNullOrWhiteSpace(rtspUrl) ||
            !Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.UserInfo))
        {
            return rtspUrl;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
        };

        // UriBuilder 會補回 @，需移除以免留下 "rtsp://@host" 這種形式。
        return builder.Uri.AbsoluteUri.Replace("//@", "//", StringComparison.Ordinal);
    }

    /// <summary>
    /// 遮蔽任意文字中所有位址的帳密（含 ffmpeg stderr、回應訊息等非純 URI 內容）。
    /// <see cref="Redact"/> 只能處理整串就是位址的情況；實際診斷輸出常內嵌多位址，
    /// 此方法逐段移除 userinfo，避免明文密碼進入診斷緩衝、例外訊息或稽核記錄。
    /// </summary>
    public static string RedactText(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? string.Empty : EmbeddedUserInfo.Replace(text, "${scheme}");
}
