namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 網段掃描：多個位址 → 找到的 ONVIF 設備清單。
/// </summary>
/// <remarks>
/// <para>
/// 與單一位址搜尋的差別只有「同時掃幾個」：探測本身由呼叫端注入（見
/// <see cref="DiscoveryClient.ProbeAddressAsync"/>），因此測試不必碰真實網路，
/// 也能釘住「同時最多幾個」與「單一 IP 失敗不影響整段」這兩件事。
/// </para>
/// <para>
/// 掃描**並行**執行（現場是整段網段，循序掃完可能要幾十分鐘），但同時進行的連線數有上限：
/// 每台攝影機的 HTTP 服務連線數有限，一次湧入上百條只會讓正常的監看與錄影被擠掉。
/// 個別位址失敗（連不上、逾時、非 ONVIF）一律視為「這台沒有」，不讓一個壞位址中斷整段掃描。
/// 呼叫端主動取消則往上拋。
/// </para>
/// </remarks>
public static class DiscoverySweep
{
    /// <summary>預設同時探測數。</summary>
    public const int DefaultConcurrency = 16;

    /// <summary>同時探測數上限（再高只是把頻寬與攝影機的連線數吃光）。</summary>
    public const int MaxConcurrency = 32;

    /// <summary>
    /// 依序探測 <paramref name="addresses"/>，回傳找到的設備（依位址順序，方便對照掃描範圍）。
    /// </summary>
    /// <param name="addresses">待探測的主機（見 <see cref="DiscoveryAddressRange"/>）。</param>
    /// <param name="probe">單一位址的探測；回傳 null 表示該位址沒有設備。</param>
    /// <param name="concurrency">同時探測數（上限 <see cref="MaxConcurrency"/>）。</param>
    /// <param name="progress">每完成一個位址回報一次（已完成, 總數）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public static async Task<IReadOnlyList<DiscoveredDevice>> RunAsync(
        IReadOnlyList<string> addresses,
        Func<string, CancellationToken, Task<DiscoveredDevice?>> probe,
        int concurrency = DefaultConcurrency,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(probe);

        if (addresses.Count == 0)
        {
            return [];
        }

        var slots = new DiscoveredDevice?[addresses.Count];
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identityLock = new object();
        var done = 0;
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, MaxConcurrency));

        var tasks = new List<Task>(addresses.Count);
        for (var i = 0; i < addresses.Count; i++)
        {
            var index = i;
            var address = addresses[index];
            tasks.Add(Task.Run(
                async () =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // SemaphoreSlim.WaitAsync 有空位時走同步快路徑，可能先拿到空位才輪到檢查權杖；
                        // 取消之後仍會有一連串「進得來、立刻拋」的探測。多這一行，取消就是不再開始
                        // 新的探測——掃描一次 200 個位址時，這一連串白工也是幾百個排程與例外。
                        cancellationToken.ThrowIfCancellationRequested();

                        DiscoveredDevice? device = null;
                        try
                        {
                            device = await probe(address, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // 連不上、逾時、憑證被拒、非 ONVIF 回應：這個位址就是沒有設備。
                            device = null;
                        }

                        if (device is not null && Claim(identities, identityLock, device))
                        {
                            slots[index] = device;
                        }
                    }
                    finally
                    {
                        gate.Release();
                        progress?.Report((Interlocked.Increment(ref done), addresses.Count));
                    }
                },
                cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. slots.OfType<DiscoveredDevice>()];
    }

    /// <summary>
    /// 同一台設備可能被兩個位址找到（主機名與 IP、或設備有多個網路介面）：
    /// 以 ONVIF 的裝置 UUID 為身分，取不到時退回 XAddr。
    /// </summary>
    private static bool Claim(
        HashSet<string> identities,
        object identityLock,
        DiscoveredDevice device)
    {
        var key = device.EndpointAddress.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)
            ? device.EndpointAddress
            : device.HttpXAddr ?? device.EndpointAddress;

        lock (identityLock)
        {
            return identities.Add(key);
        }
    }
}
