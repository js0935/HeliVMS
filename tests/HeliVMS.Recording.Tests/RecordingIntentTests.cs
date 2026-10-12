using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 「這個頻道現在該不該錄」的規則本身。
///
/// 這些保證以前一個都守不到：<c>channels.recording_mode</c> 沒有任何行為消費者，
/// 「全時錄影」在畫面上選得到、也存得進資料庫，卻沒有任何一行程式碼讀它。
/// 結果是預設值（Always）等於「什麼都不錄」，而且沒有錯誤、沒有警告、log 也是乾淨的。
///
/// 這裡直接測純函式 <see cref="RecordingIntent"/>：不開 ffmpeg、不接攝影機、不碰資料庫，
/// 所以測試失敗一定是規則寫錯，不是環境問題。
/// </summary>
public sealed class RecordingIntentTests
{
    /// <summary>固定觀測時間，讓分鐘邊界的測試不會在午夜附近翻盤。</summary>
    private static readonly DateTime Now = new(2026, 3, 4, 10, 30, 0, DateTimeKind.Unspecified);

    /// <summary>涵蓋整週的位元遮罩（Sun=0 … Sat=6）。</summary>
    private const int AllDays = 0b1111111;

    /// <summary>
    /// 全時錄影必須真的會錄，不需要任何排程。
    ///
    /// 這是整組測試的根：<c>recording_mode</c> 的預設值就是 Always，
    /// 而舊的排程器只讀 <c>recording_schedule</c>。所以全新安裝、沒設任何排程的頻道
    /// 「設定顯示全時」但實際一段都沒錄。
    /// </summary>
    [Fact]
    public void 全時模式的頻道不需要任何排程就該錄()
        => Assert.True(RecordingIntent.ShouldRecord(Channel(RecordingMode.Always), [], Now));

    /// <summary>排程模式在時段內就錄。</summary>
    [Fact]
    public void 排程模式在時段內會錄()
    {
        var schedules = new[] { Schedule(start: 600, end: 660) };

        Assert.True(RecordingIntent.ShouldRecord(Channel(RecordingMode.Scheduled), schedules, Now));
    }

    /// <summary>排程模式在時段外不錄，否則「只在營業時間錄」這個設定形同虛設。</summary>
    [Fact]
    public void 排程模式在時段外不會錄()
    {
        var schedules = new[] { Schedule(start: 60, end: 120) };

        Assert.False(RecordingIntent.ShouldRecord(Channel(RecordingMode.Scheduled), schedules, Now));
    }

    /// <summary>停用的排程不錄——停用是操作員唯一的關閉手段。</summary>
    [Fact]
    public void 停用的排程不會讓排程模式開始錄影()
    {
        var schedules = new[] { Schedule(start: 600, end: 660, enabled: false) };

        Assert.False(RecordingIntent.ShouldRecord(Channel(RecordingMode.Scheduled), schedules, Now));
    }

    /// <summary>星期遮罩排除今天時不錄。</summary>
    [Fact]
    public void 星期遮罩排除今天時不會錄()
    {
        var todayExcluded = 0b1111111 & ~(1 << (int)Now.DayOfWeek);
        var schedules = new[] { Schedule(start: 600, end: 660, daysMask: todayExcluded) };

        Assert.False(RecordingIntent.ShouldRecord(Channel(RecordingMode.Scheduled), schedules, Now));
    }

    /// <summary>別的頻道的排程不該讓這個頻道開始錄。</summary>
    [Fact]
    public void 別的頻道的排程不會讓本頻道錄影()
    {
        var schedules = new[] { Schedule(start: 600, end: 660) with { ChannelId = 999 } };

        Assert.False(RecordingIntent.ShouldRecord(Channel(RecordingMode.Scheduled), schedules, Now));
    }

    /// <summary>
    /// 手動模式就算剛好有命中當下的排程也不自動錄。
    ///
    /// 「手動」的意思就是「操作員按下去才錄」。若排程蓋過它，使用者設了手動
    /// 卻每天照樣被錄滿磁碟，而且從設定畫面完全看不出是哪裡開的。
    /// </summary>
    [Fact]
    public void 手動模式即使有命中時段的排程也不會自動錄()
    {
        var schedules = new[] { Schedule(start: 600, end: 660) };

        Assert.False(RecordingIntent.ShouldRecord(Channel(RecordingMode.Manual), schedules, Now));
    }

    /// <summary>
    /// 事件模式不會被排程器自動開錄。
    ///
    /// 事件觸發要等偵測真的接上；在那之前假裝能錄，只會讓人以為事件錄影是好的，
    /// 等到真的需要回放時才發現沒有任何事件錄影。
    /// </summary>
    [Fact]
    public void 事件模式不會被排程器自動開錄()
    {
        var schedules = new[] { Schedule(start: 600, end: 660) };

        Assert.False(RecordingIntent.ShouldRecord(Channel(RecordingMode.Event), schedules, Now));
    }

    /// <summary>停用的頻道不錄。全時模式也不能跳過這個開關。</summary>
    [Fact]
    public void 停用的全時頻道不會錄()
        => Assert.False(RecordingIntent.ShouldRecord(Channel(RecordingMode.Always, enabled: false), [], Now));

    /// <summary>
    /// 沒有 RTSP 位址的頻道不錄。
    ///
    /// 開了也只會讓 ffmpeg 立刻失敗，而排程器每 30 秒重試一次，
    /// 等於每 30 秒生一個死掉的行程加一筆失敗記錄。
    /// </summary>
    [Fact]
    public void 沒有RTS位址的全時頻道不會錄()
        => Assert.False(RecordingIntent.ShouldRecord(Channel(RecordingMode.Always, rtsp: "  "), [], Now));

    /// <summary>
    /// 全時與排程要寫下不同的稽核來源。
    ///
    /// 混在一起的話，出事時分不出是「使用者設了全時所以本來就該錄」，
    /// 還是「排程把它開起來的」——而這正是最需要被追出來的差別。
    /// </summary>
    [Fact]
    public void 全時與排程寫下的稽核來源不同()
    {
        Assert.Equal(RecordingGateSources.Always, RecordingIntent.GateSourceFor(RecordingMode.Always));
        Assert.Equal(RecordingGateSources.Schedule, RecordingIntent.GateSourceFor(RecordingMode.Scheduled));
    }

    /// <summary>
    /// 該錄的頻道不會回報阻擋原因。
    /// </summary>
    /// <remarks>
    /// 這個對應關係是「為什麼沒錄」提示的正確性基礎：
    /// 若正在錄的頻道也被標成 ManualOnly，操作員會看到互相矛盾的
    /// 「●REC（僅手動錄影）」而無法信任畫面上的任何提示。
    /// </remarks>
    [Fact]
    public void 該錄的頻道不會回報阻擋原因()
        => Assert.Equal(
            RecordingBlockReason.None,
            RecordingIntent.BlockReasonFor(Channel(RecordingMode.Always), [], Now));

    /// <summary>排程外的排程頻道會說明是「排程外」而不是籠統的不錄。</summary>
    [Fact]
    public void 排程外的頻道會說明是排程外()
        => Assert.Equal(
            RecordingBlockReason.OutsideSchedule,
            RecordingIntent.BlockReasonFor(
                Channel(RecordingMode.Scheduled),
                new[] { Schedule(start: 60, end: 120) },
                Now));

    /// <summary>
    /// 停用優先於模式：已停用的頻道不管設什麼都該先被指出來。
    /// </summary>
    /// <remarks>
    /// 若先報「僅手動錄影」，操作員會照著去改錄影模式，改完發現還是不錄，
    /// 最後才想到頻道根本是關的——順序錯了會多浪費一趟查錯方向。
    /// </remarks>
    [Fact]
    public void 已停用的頻道優先回報停用而非模式()
        => Assert.Equal(
            RecordingBlockReason.ChannelDisabled,
            RecordingIntent.BlockReasonFor(Channel(RecordingMode.Manual, enabled: false), [], Now));

    /// <summary>沒有位址的頻道要報「未設定位址」，因為改模式也沒用。</summary>
    [Fact]
    public void 沒有位址的頻道會報未設定位址()
        => Assert.Equal(
            RecordingBlockReason.NoStreamUrl,
            RecordingIntent.BlockReasonFor(Channel(RecordingMode.Always, rtsp: "  "), [], Now));

    /// <summary>手動與事件模式各自有明確原因，避免被誤讀成排程沒生效。</summary>
    [Theory]
    [InlineData(RecordingMode.Manual, RecordingBlockReason.ManualOnly)]
    [InlineData(RecordingMode.Event, RecordingBlockReason.EventOnly)]
    public void 手動與事件模式會各自說明原因(RecordingMode mode, RecordingBlockReason expected)
        => Assert.Equal(expected, RecordingIntent.BlockReasonFor(Channel(mode), [], Now));

    /// <summary>
    /// 原因判定必須與實際開錄決策一致。
    /// </summary>
    /// <remarks>
    /// 這是整個功能的地基：若 BlockReasonFor 回「沒阻擋」而 ShouldRecord 回 false
    /// （或反過來），畫面上就會出現「沒有原因卻也沒錄影」——
    /// 正好是這個功能要消滅的那種狀況。
    /// </remarks>
    [Theory]
    [InlineData(RecordingMode.Always, true, "rtsp://example.invalid/live", true, RecordingBlockReason.None)]
    [InlineData(RecordingMode.Scheduled, true, "rtsp://example.invalid/live", false, RecordingBlockReason.OutsideSchedule)]
    [InlineData(RecordingMode.Manual, true, "rtsp://example.invalid/live", true, RecordingBlockReason.ManualOnly)]
    [InlineData(RecordingMode.Always, false, "rtsp://example.invalid/live", true, RecordingBlockReason.ChannelDisabled)]
    [InlineData(RecordingMode.Always, true, "  ", true, RecordingBlockReason.NoStreamUrl)]
    public void 阻擋原因與實際開錄決策必須一致(
        RecordingMode mode,
        bool enabled,
        string rtsp,
        bool scheduled,
        RecordingBlockReason expected)
    {
        var channel = Channel(mode, enabled: enabled, rtsp: rtsp);
        var schedules = scheduled
            ? new[] { Schedule(start: 60, end: 120) }
            : Array.Empty<RecordingScheduleRecord>();

        var shouldRecord = RecordingIntent.ShouldRecord(channel, schedules, Now);
        var reason = RecordingIntent.BlockReasonFor(channel, schedules, Now);

        Assert.Equal(shouldRecord, reason == RecordingBlockReason.None);
        Assert.Equal(expected, reason);
    }

    private static ChannelInfo Channel(
        RecordingMode mode,
        bool enabled = true,
        string rtsp = "rtsp://example.invalid/live")
        => new()
        {
            Id = 1,
            Name = "車道 1",
            MainStreamUrl = rtsp,
            RecordingMode = mode,
            Enabled = enabled,
        };

    private static RecordingScheduleRecord Schedule(
        int start,
        int end,
        bool enabled = true,
        int daysMask = AllDays)
        => new()
        {
            ChannelId = 1,
            DaysMask = daysMask,
            StartMinute = start,
            EndMinute = end,
            Enabled = enabled,
        };
}