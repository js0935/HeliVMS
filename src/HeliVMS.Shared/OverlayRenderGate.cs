namespace HeliVMS.Shared;

/// <summary>
/// 決定某格的 AI 疊加是否需要重建視覺樹：<b>只在偵測世代或顯示縮放真的變了時才重畫</b>。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要它：疊加原本在每個影格事件都 <c>Children.Clear()</c> 再為每個偵測框 new 一個
/// <c>Rectangle</c>/<c>TextBlock</c>。但 AI 結果本身每 200–800ms 才更新一次（見
/// <c>UpdateAiPolicy</c> 的分層取樣），而影格是 15fps——也就是說 90% 以上的重畫
/// 產生的視覺樹跟上一幀完全一樣，純粹在丟棄並重建 WPF 的疊加視覺樹。
/// 16 格時這是每幀數百個一次性視覺元素。</para>
/// <para>
/// 縮放也要納入判斷：視窗大小改變時偵測框的位置必須跟著縮放，不能因為「偵測沒變」
/// 就讓框線停在舊尺寸上。</para>
/// <para>
/// 與 <see cref="LatestFrameSlots{T}"/> 同樣的理由放在 <c>HeliVMS.Shared</c>：這條規則
/// 要能在 net10.0 測試專案裡用行為斷言驗證，證明「沒有變化就不重建」真的有被守住。
/// </para>
/// </remarks>
public sealed class OverlayRenderGate
{
    /// <summary>縮放比較容差：視窗拖曳時 ActualWidth 會有浮點雜訊，差這麼一點不該觸發重畫。</summary>
    private const double ScaleEpsilon = 0.0001;

    private readonly int[] _drawnGeneration;
    private readonly double[] _drawnScale;
    private readonly bool[] _hasDrawn;

    public OverlayRenderGate(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        _drawnGeneration = new int[slotCount];
        _drawnScale = new double[slotCount];
        _hasDrawn = new bool[slotCount];
    }

    /// <summary>該格是否已經畫過（診斷／測試用）。</summary>
    public bool HasDrawn(int cell)
    {
        Validate(cell);
        return _hasDrawn[cell];
    }

    /// <summary>該格上次繪製時記錄的世代。</summary>
    public int DrawnGeneration(int cell)
    {
        Validate(cell);
        return _drawnGeneration[cell];
    }

    /// <summary>
    /// 該格沒有東西可畫（AI 關閉、無偵測、尚無影格）時呼叫。
    /// </summary>
    /// <returns><c>true</c>＝仍需清空畫面；<c>false</c>＝已經是空的且狀態沒變，不必再動。</returns>
    public bool ShouldClear(int cell, int generation)
    {
        Validate(cell);
        if (_hasDrawn[cell] && _drawnGeneration[cell] == generation)
        {
            return false;
        }

        _hasDrawn[cell] = true;
        _drawnGeneration[cell] = generation;
        _drawnScale[cell] = 0;
        return true;
    }

    /// <summary>該格有偵測要畫時呼叫。</summary>
    /// <returns><c>true</c>＝需要重建疊加視覺樹；<c>false</c>＝與上次完全相同，略過。</returns>
    public bool ShouldRender(int cell, int generation, double scale)
    {
        Validate(cell);
        if (_hasDrawn[cell] &&
            _drawnGeneration[cell] == generation &&
            Math.Abs(_drawnScale[cell] - scale) < ScaleEpsilon)
        {
            return false;
        }

        _hasDrawn[cell] = true;
        _drawnGeneration[cell] = generation;
        _drawnScale[cell] = scale;
        return true;
    }

    /// <summary>全部重置（監看中斷或重建佈局時呼叫，下次一律重畫）。</summary>
    public void Reset()
    {
        Array.Clear(_hasDrawn);
        Array.Clear(_drawnGeneration);
        Array.Clear(_drawnScale);
    }

    private void Validate(int cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, _drawnGeneration.Length);
    }
}