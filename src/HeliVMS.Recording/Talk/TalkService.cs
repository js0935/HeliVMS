using System.Diagnostics;
using HeliVMS.Shared;

namespace HeliVMS.Recording.Talk;

/// <summary>
/// 啟動一次對講：解析供應商 → 組指令 → 啟動 ffmpeg 子行程，回傳可等候／可中止的 <see cref="TalkSession"/>。
/// </summary>
/// <remarks>
/// 行程啟動刻意走 <see cref="IProcessFactory"/>（與錄影、解碼共用同一個縫），
/// 對講因此能在單元測試中以假行程驗證，不需要真的 ffmpeg 或真的攝影機。
/// 每個對講子行程都會登錄到 <see cref="FfmpegProcessRegistry"/>，健康視窗才看得到它。
/// </remarks>
public sealed class TalkService
{
    private readonly TalkProviderRegistry _providers;
    private readonly IProcessFactory _processFactory;
    private readonly FfmpegProcessRegistry _registry;

    /// <summary>建立對講服務；未提供的相依使用預設值。</summary>
    public TalkService(
        TalkProviderRegistry? providers = null,
        IProcessFactory? processFactory = null,
        FfmpegProcessRegistry? registry = null)
    {
        _providers = providers ?? new TalkProviderRegistry();
        _processFactory = processFactory ?? new DefaultProcessFactory();
        _registry = registry ?? FfmpegProcessRegistry.Shared;
    }

    /// <summary>
    /// 啟動一次對講。找不到可處理的供應商時擲出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public TalkSession Start(TalkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = _providers.Resolve(request)
            ?? throw new InvalidOperationException(
                $"找不到可用的對講供應商：頻道 {request.ChannelId} 沒有 RTSP 位址，也沒有指定的對講位址。");

        var target = provider.Resolve(request);
        var command = provider.BuildCommand(request, target);

        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        var process = _processFactory.Start(psi);

        // 對講是一次性的短命子行程（播完音訊即結束），分類為 Other 而非錄影／解碼；
        // 登錄只是為了讓健康視窗看得到總量，逾時失控時有據可查。
        var job = _registry.Register(FfmpegJobKind.Other, $"talk/ch{request.ChannelId:000}");
        return new TalkSession(process, job, target);
    }
}
