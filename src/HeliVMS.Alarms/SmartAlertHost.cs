using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 一筆事件的聚合判定結果。
/// </summary>
/// <param name="Aggregated">
/// 該事件是否被<b>任一</b>聚合規則命中。命中時原始通知必須被抑制（否則門檻形同虛設），
/// 但事件本身仍留在事件中心當證據。
/// </param>
/// <param name="Triggers">本次「首次跨過門檻」的聚合結果，可能不只一筆。</param>
/// <remarks>
/// 為什麼需要這個型別而不是回傳 <c>AlertTrigger?</c>：兩者都要回 null 的情況意義相反——
/// 「沒有聚合規則命中，所以照常通知」與「命中了但門檻還沒到，所以這筆不通知」。
/// 早期版本只回 <c>AlertTrigger?</c>，呼叫端因此寫成
/// <c>if (trigger is null) return;</c>，結果<b>沒有規則命中的事件也一併被吞掉</b>：
/// 現場症狀是設定了聚合規則之後，所有一般警報通知都消失了，而且沒有錯誤、
/// 沒有 log、事件中心還看得到每一筆，看起來像通知功能壞掉而不是規則生效。
/// </remarks>
public sealed record SmartAlertOutcome(
    bool Aggregated,
    IReadOnlyList<AlertTrigger> Triggers)
{
    /// <summary>沒有任何聚合規則命中（照常通知）。</summary>
    public static readonly SmartAlertOutcome Unaggregated =
        new(false, Array.Empty<AlertTrigger>());

    /// <summary>被聚合規則命中，但這次沒有跨過門檻。</summary>
    public static readonly SmartAlertOutcome Suppressed =
        new(true, Array.Empty<AlertTrigger>());
}

/// <summary>
/// 智慧警報聚合（M54，§14.7 #8）在 App 端的執行主機。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這個類別：<see cref="SmartAlertEvaluator"/> 的聚合演算法與測試早就存在，
/// 但沒有任何呼叫端。設定頁可以存一條「同頻道 5 分鐘內 8 次移動才告警」的規則，
/// 介面不會報錯，而現場會收到每一次移動——規則看起來生效了，其實沒有。
/// 這個主機讓規則真的生效，並把聚合結果另外插成一筆事件。</para>
/// <para>
/// 刻意不<b>抑制</b>原始事件：原始事件仍是事件中心的證據鏈，
/// 只有<b>通知</b>走聚合。這樣操作員仍看得到每一次移動，
/// 但外部保全不會被同一件事洗版——而洗版正是這條規則要解決的問題。</para>
/// <para>
/// 窗內次數以資料庫為準而非記憶體：App 重啟後記憶體歸零，
/// 若用它算次數，操作員重啟 NVR 就能洗掉已達門檻的告警。
/// 記憶體只用來記「這個窗是否已經告過警」。</para>
/// <para>
/// 刻意放在 <c>HeliVMS.Alarms</c> 而非 App 專案：這是純邏輯＋資料庫，
/// 沒有任何 WPF 相依。放在 App 專案裡唯一的作用是讓它<b>沒有測試專案可以參考</b>，
/// 而這正是「有規則卻沒生效」這類問題能留到今天的原因。
/// </para>
/// </remarks>
public sealed class SmartAlertHost
{
    /// <summary>聚合事件的 EventType；與既有類型不衝突，事件中心可據此篩選。</summary>
    public const string SmartAlertEventType = "smart_alert";

    private readonly AlertRuleRepository _rules;
    private readonly AlarmEventRepository _events;
    private readonly SmartAlertGate _gate = new();

    public SmartAlertHost(SqliteStore store)
    {
        _rules = new AlertRuleRepository(store);
        _events = new AlarmEventRepository(store);
    }

    /// <summary>聚合規則首次跨過門檻時觸發，供 UI／通知使用。</summary>
    public event EventHandler<AlertTrigger>? Triggered;

    /// <summary>
    /// 收一筆已寫入的原始事件，回傳它的聚合判定。
    /// </summary>
    /// <remarks>
    /// 呼叫端應在原始事件已寫入資料庫之後才呼叫本方法——窗內計數要讀得到自己。
    /// </remarks>
    public SmartAlertOutcome Observe(AlarmEventRecord record) => Observe(record, nowUtc: record.StartUtc);

    /// <summary>補算時可指定窗的右端（例如把事件時間當作 now）。</summary>
    internal SmartAlertOutcome Observe(AlarmEventRecord record, DateTime nowUtc)
    {
        List<AlertRule> rules;
        try
        {
            rules = _rules.ListEnabled().Where(r => r.FrameMinutes > 0).ToList();
        }
        catch
        {
            // 規則讀不到不該讓警報流程整條中斷；退化成「照常通知每一筆」。
            return SmartAlertOutcome.Unaggregated;
        }

        var matched = false;
        List<AlertTrigger>? triggers = null;

        foreach (var rule in rules)
        {
            if (!SmartAlertEvaluator.Matches(rule, record))
            {
                continue;
            }

            matched = true;

            // 窗長是<b>每一條規則自己的</b> FrameMinutes。這裡曾經用 host 建構子的
            // 預設 5 分鐘覆蓋規則值，結果設定頁寫「60 分鐘內 8 次」實際上按 5 分鐘算，
            // 而且介面不會有任何線索指出設定沒生效。
            var windowMinutes = rule.FrameMinutes;
            var fromUtc = nowUtc.AddMinutes(-windowMinutes);

            var count = CountInWindow(rule, record.ChannelId, fromUtc, nowUtc);
            var decision = _gate.Observe(rule.Id, count, rule.MinEventsInWindow, nowUtc, windowMinutes);
            if (!decision.FirstCrossing)
            {
                continue;
            }

            var detail = string.Format(
                CultureInfo.InvariantCulture,
                "智慧警報「{0}」：{1} 分鐘內第 {2} 次 {3}（門檻 {4}）",
                rule.Name,
                windowMinutes,
                count,
                record.EventType,
                rule.MinEventsInWindow);

            long summaryId;
            try
            {
                summaryId = _events.Insert(
                    record.ChannelId,
                    SmartAlertEventType,
                    record.StartUtc,
                    snapshotPath: record.SnapshotPath,
                    detail: detail);
            }
            catch
            {
                // 聚合事件插不進去不代表原始事件該被吞掉：繼續用剩下的規則判定，
                // 但這條規則已經確定跨過門檻，所以 Aggregated 仍是 true。
                continue;
            }

            var trigger = new AlertTrigger(
                rule.Id,
                rule.Name,
                rule.EventType,
                rule.ChannelId,
                count,
                fromUtc,
                nowUtc,
                Reached: true);

            triggers ??= new List<AlertTrigger>();
            triggers.Add(trigger);
            Triggered?.Invoke(this, trigger);
        }

        if (matched)
        {
            return triggers is null
                ? SmartAlertOutcome.Suppressed
                : new SmartAlertOutcome(true, triggers);
        }

        return SmartAlertOutcome.Unaggregated;
    }

    /// <summary>窗內命中次數（含本次事件）；讀不到資料時回傳 1，永遠不高估門檻已達。</summary>
    private int CountInWindow(AlertRule rule, int channelId, DateTime fromUtc, DateTime toUtc)
    {
        try
        {
            return _events
                .ListByRange(channelId, fromUtc, toUtc)
                .Count(e => SmartAlertEvaluator.Matches(rule, e));
        }
        catch
        {
            // 讀失敗時只認定「有這一笔」：寧可少告警，也不要在讀不到資料時誤報已達門檻。
            return 1;
        }
    }

}
