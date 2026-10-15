namespace HeliVMS.Devices.Onvif;

/// <summary>批次韌體升級的一台目標設備（不含檔案，檔案由整個批次共用）。</summary>
public sealed record FirmwareUpgradeTarget(
    int DeviceId,
    string Name,
    string DeviceXAddr,
    string? Username,
    string? Password);

/// <summary>批次檢查版本的單台結果。<see cref="Version"/> 失敗時為空字串。</summary>
public sealed record FirmwareCheckResult(
    FirmwareUpgradeTarget Target,
    string Version,
    string? Error)
{
    public bool Success => Error is null;
}

/// <summary>批次升級的單台結果。<see cref="PreviousVersion"/> 為升級前讀到的版本（讀不到為空字串）。</summary>
public sealed record FirmwareUpgradeResult(
    FirmwareUpgradeTarget Target,
    bool Success,
    string PreviousVersion,
    string? Error,
    bool Rebooted);

/// <summary>
/// 批次檢查與推送韌體（§7.4「批次檢查版本 → 推送映像 → 升級／重啟驗證」）。
///
/// <para>
/// 逐台**循序**處理：一次只對一台設備動破壞性命令，避免同時刷多台造成整個機房一起失聯，
/// 也讓進度與結果有明確先後。單台失敗**不中止整批**——一台刷失敗不該讓其餘設備停在半路，
/// 操作員要能一次看到哪幾台成功、哪幾台要重試。
/// </para>
///
/// <para>
/// 因為這是破壞性且不可逆的操作，呼叫端必須先取得管理員授權並做明確確認；本類別只負責
/// 執行與回報。裝置服務可注入（測試以假 <see cref="HttpMessageHandler"/> 取代），
/// 預設則以目標的 XAddr 建立真實 <see cref="OnvifDeviceService"/>。
/// </para>
/// </summary>
public sealed class FirmwareUpgradeBatch
{
    private readonly Func<FirmwareUpgradeTarget, OnvifDeviceService> _serviceFactory;

    public FirmwareUpgradeBatch(
        Func<FirmwareUpgradeTarget, OnvifDeviceService>? serviceFactory = null)
    {
        _serviceFactory = serviceFactory
            ?? (target => new OnvifDeviceService(target.DeviceXAddr, target.Username, target.Password));
    }

    /// <summary>
    /// 逐台讀取目前韌體版本（唯讀）。單台失敗記錄錯誤後繼續下一台；
    /// 呼叫端主動取消則往上拋（<see cref="OperationCanceledException"/>）。
    /// </summary>
    public async Task<IReadOnlyList<FirmwareCheckResult>> CheckAsync(
        IReadOnlyList<FirmwareUpgradeTarget> targets,
        IProgress<FirmwareCheckResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var results = new List<FirmwareCheckResult>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FirmwareCheckResult result;
            try
            {
                using var service = _serviceFactory(target);
                var info = await service.GetInfoAsync(cancellationToken);
                result = new FirmwareCheckResult(target, info.FirmwareVersion, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new FirmwareCheckResult(target, string.Empty, ex.Message);
            }

            results.Add(result);
            progress?.Report(result);
        }

        return results;
    }

    /// <summary>
    /// 對每台選定設備推送同一份韌體：先讀舊版本 → <c>StartFirmwareUpgrade</c> →
    /// （可選）<c>SystemReboot</c>。單台失敗記錄錯誤後繼續；取消往上拋。
    ///
    /// <para>
    /// 升級是否真的把版本換掉，取決於設備在重啟後才更新 <c>FirmwareVersion</c>；
    /// 本方法不等待重啟完成再回讀（重啟可能數十秒），故只回報「命令已送出」與升級前版本。
    /// </para>
    /// <para>
    /// <b>讀舊版失敗就不推送</b>：連唯讀都答不出來的設備代表不健康或位址／認證有誤，
    /// 此時推韌體很可能把設備刷失聯，所以直接記為失敗並跳過，而不是硬送。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<FirmwareUpgradeResult>> UpgradeAsync(
        IReadOnlyList<FirmwareUpgradeTarget> targets,
        byte[] firmware,
        bool rebootAfter,
        IProgress<FirmwareUpgradeResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ValidateFirmware(firmware);

        var results = new List<FirmwareUpgradeResult>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FirmwareUpgradeResult result;
            try
            {
                using var service = _serviceFactory(target);

                // 先讀舊版再刷：升級後回讀多半還顯示舊版（要重啟才生效），
                // 先記下來，回報與稽核才有「從哪一版升上去」的依據。
                var info = await service.GetInfoAsync(cancellationToken);
                await service.StartFirmwareUpgradeAsync(firmware, cancellationToken);

                var rebooted = false;
                if (rebootAfter)
                {
                    await service.RebootAsync(cancellationToken);
                    rebooted = true;
                }

                result = new FirmwareUpgradeResult(target, true, info.FirmwareVersion, null, rebooted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new FirmwareUpgradeResult(target, false, string.Empty, ex.Message, false);
            }

            results.Add(result);
            progress?.Report(result);
        }

        return results;
    }

    private static void ValidateFirmware(byte[] firmware)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        if (firmware.Length == 0)
        {
            throw new ArgumentException("韌體檔案為空。", nameof(firmware));
        }

        if (firmware.Length > OnvifDeviceService.MaxFirmwareBytes)
        {
            throw new ArgumentException(
                $"韌體檔案過大（{firmware.Length} 位元組，上限 {OnvifDeviceService.MaxFirmwareBytes}）。",
                nameof(firmware));
        }
    }
}
