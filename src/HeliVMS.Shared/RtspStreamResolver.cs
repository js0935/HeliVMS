namespace HeliVMS.Shared;

/// <summary>
/// 決定要交給 ffmpeg／ffprobe 的「實際」串流位址。
/// <para>
/// 憑證一律存放於 <c>devices.password_encrypted</c>（DPAPI），資料庫中的
/// <c>channels.main_rtsp</c> 保持不含帳密的裸位址，只有在真的要拉流的當下才組合。
/// 如此可避免明文密碼落在 sqlite 備份、日誌或稽核記錄中。
/// </para>
/// <para>
/// 本類別刻意做成純函式（只依賴委派，不碰資料庫），使這條安全關鍵路徑得以直接單元測試；
/// 實際取用端請見 <c>ChannelManager.ResolveStreamUrl</c>。
/// </para>
/// </summary>
public static class RtspStreamResolver
{
    /// <summary>由 RTSP 主機名稱／IP 回查對應的設備 ID；查無則回傳 <c>null</c>。</summary>
    public delegate int? FindDeviceIdByHost(string host);

    /// <summary>取出指定設備的 RTSP 帳密。</summary>
    public delegate (string Username, string Password) GetCredentials(int deviceId);

    /// <summary>
    /// 組合實際拉流位址。
    /// </summary>
    /// <param name="mainStreamUrl">頻道儲存的裸位址（不應含帳密）。</param>
    /// <param name="deviceId">頻道綁定的設備 ID；舊資料可能為 <c>null</c>。</param>
    /// <param name="findDeviceIdByHost">主機回退查詢，用於 <paramref name="deviceId"/> 為 <c>null</c> 的舊資料。</param>
    /// <param name="getCredentials">取出設備帳密。</param>
    /// <returns>含帳密的位址；無法取得憑證時原樣回傳裸位址。</returns>
    public static string Resolve(
        string? mainStreamUrl,
        int? deviceId,
        FindDeviceIdByHost findDeviceIdByHost,
        GetCredentials getCredentials)
    {
        if (string.IsNullOrWhiteSpace(mainStreamUrl))
        {
            return mainStreamUrl ?? string.Empty;
        }

        var id = deviceId;
        if (id is null)
        {
            // 舊資料的 channels.device_id 可能為 NULL（加入時未綁定設備），
            // 此時以位址主機回推設備，讓既有頻道不必重新加入即可取得憑證。
            var host = RtspUri.Host(mainStreamUrl);
            if (string.IsNullOrEmpty(host))
            {
                return mainStreamUrl;
            }

            id = findDeviceIdByHost?.Invoke(host);
            if (id is null)
            {
                return mainStreamUrl;
            }
        }

        try
        {
            var (username, password) = getCredentials(id.Value);

            // 密碼為空等同沒有可用憑證。若仍交給 WithCredentials，會產生
            // "rtsp://user@host" 這種半截位址：對需要密碼的攝影機並沒有幫助，
            // 卻讓診斷輸出看起來像有帶憑證，誤導遮蔽與稽核的假設。
            // 這裡維持「要嘛完整憑證、要嘛裸位址」的不變式。
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                return mainStreamUrl;
            }

            return RtspUri.WithCredentials(mainStreamUrl, username, password);
        }
        catch (Exception)
        {
            // 憑證讀取失敗時以裸位址嘗試，讓 RtspClient 自行回報連線錯誤，不中斷其他頻道。
            return mainStreamUrl;
        }
    }
}
