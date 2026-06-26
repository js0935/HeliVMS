# 雙向速度滑桿 — 倒帶支援 + Nx 互動行為

**日期:** 2026-06-26
**狀態:** 審查中
**關聯:** 2026-06-26-nx-playback-panel-redesign.md

---

## 1. 速度公式

滑桿維持範圍 **0–100**，中點 **50**。

### 播放中 (`_isPlaying == true`)

| val | speed | 意義 |
|-----|-------|------|
| 0 | -16x | 最快倒帶 |
| 25 | -4x | 中速倒帶 |
| 50 | 1x | 正常播放 |
| 75 | 4x | 中速快轉 |
| 100 | 16x | 最快快轉 |

**公式:** `speed = Math.Pow(2, (val - 50) / 12.5)`，val=50 時 speed=1x

實作:
```csharp
private static double SliderValueToSpeed(double sliderVal, bool isPlaying) {
    if (!isPlaying) return SliderValueToSpeedPaused(sliderVal);
    if (sliderVal == 50.0) return 1.0;
    var offset = (sliderVal - 50.0) / 12.5; // -4 to +4
    return Math.Round(Math.Pow(2, offset), 2);
}
```

### 暫停中 (`_isPlaying == false`)

| val | speed | 意義 |
|-----|-------|------|
| 0 | -2x | 最快速倒帶（慢動作） |
| 25 | -1x | 慢動作倒帶 |
| 37.5 | -0.5x | 慢動作 |
| 43.75 | -0.25x | 極慢倒帶 |
| 50 | 0x | 完全暫停 |
| 56.25 | 0.25x | 極慢動作 |
| 62.5 | 0.5x | 慢動作 |
| 75 | 1x | 慢動作快轉 |
| 100 | 2x | 最快速慢動作 |

**公式:** 當 val=50 時 speed=0x；否則 `speed = sign(val-50) × 0.25 × 2^(|val-50|/16.667)`

實作:
```csharp
private static double SliderValueToSpeedPaused(double sliderVal) {
    if (sliderVal == 50.0) return 0.0;
    var sign = sliderVal > 50.0 ? 1.0 : -1.0;
    var offset = Math.Abs(sliderVal - 50.0) / 16.6667; // ~0 to 3
    return Math.Round(sign * 0.25 * Math.Pow(2, offset), 2);
}
```

---

## 2. 點擊暫時 vs 拖曳永久

### 狀態欄位

```csharp
private bool _isTemporarySpeed;
private double _defaultSpeed; // 回歸速度：播放中 = 1x，暫停中 = 0x
```

### 事件處理

| 事件 | 行為 |
|------|------|
| `Slider.PreviewMouseDown` | 記錄 `_isTemporarySpeed = true`、儲存滑鼠按下位置 |
| `Slider.PreviewMouseMove` (按下中) | 移動 > 5px → `_isTemporarySpeed = false` (進入拖曳) |
| `Slider.PreviewMouseUp` | `_isTemporarySpeed == true` → 滑桿回到中點(val=50) → 套用 `_defaultSpeed` |
| `Slider.MouseWheel` | Delta 每單位 → val ± 4、設 `_isTemporarySpeed = true`、啟動還原計時器 |
| `Slider.MouseLeave` | 若 `_isTemporarySpeed` → 還原中點速度 |

### 暫時速度還原流程

```
[使用者按下/滾輪] → _isTemporarySpeed = true
                    → _defaultSpeed = (_isPlaying ? 1.0 : 0.0)
                    → 改變滑桿 val → speed 改變
[放開/滑鼠離開] → val = 50
                 → 套用 _defaultSpeed
                 → _isTemporarySpeed = false
```

---

## 3. 狀態切換

| 事件 | 滑桿行為 |
|------|---------|
| `PlayPauseBtn_Click` (播放→暫停) | 若 val=50 → speed 從 1x→0x。若 val≠50 → 維持目前 val，speed 用暫停公式重新計算 |
| `PlayPauseBtn_Click` (暫停→播放) | 同上，speed 用播放公式重新計算 |
| `_coordinator.Play()` / `_coordinator.Pause()` 失敗 | 不改變滑桿位置，保留原狀態 |

---

## 4. 負速度顯示

```csharp
private static string SpeedToDisplayText(double speed) {
    if (speed == 0.0) return "0x";
    // 處理負號
    var prefix = speed < 0 ? "-" : "";
    var abs = Math.Abs(speed);
    return abs >= 1.0
        ? $"{prefix}{(int)abs}x"
        : $"{prefix}{abs:F2}".TrimEnd('0').TrimEnd('.') + "x";
}
```

**注意:** 修正 double-x bug（已於 Task 1 fix 中處理）。

**FsTransportBtn_Click "speed" 循環:** `1x → 2x → 4x → 8x → 16x → 1x`（只循環正速度）

---

## 5. OSC 速度顯示

OSD（螢幕顯示）在倒帶時需要顯示負值：
```
"-4x"  — 倒帶 4x
"4x"   — 快轉 4x
"0x"   — 暫停
```

`ShowSpeedOSDOnPlayers(rate)` 和 `UpdatePlayerSpeedBadges(rate)` 已支援負值（Task 1 fix 時已確認）。

---

## 6. 實作範圍

### 修改檔案

| 檔案 | 變更 |
|------|------|
| `Views/PlaybackView.xaml.cs` | 速度公式、點擊/拖曳/滾輪 handlers、_isTemporarySpeed 欄位、_defaultSpeed 欄位、SpeedToDisplayText |
| `Views/PlaybackView.xaml` | (無變更或極小 — Slider 已就位) |
| `Controls/NxTimeline.xaml.cs` | (無變更) |
| `Styles/Styles.xaml` | (無變更) |

### 不修改

- NxTimeline — 無關
- PlaybackView.xaml — Slider 已存在（Task 1）
- 運輸控制按鈕（Task 2）— 無關
- Styles.xaml — 無關

---

## 7. 風險與邊界情況

| 情況 | 處理 |
|------|------|
| 底層不支援負速度 | `_coordinator.SetSpeed(負值)` 若失敗 → 日誌警告，滑桿回中點 |
| 非常快速拖曳 | `ValueChanged` 每秒觸發多次，效能影響可忽略 |
| 滑鼠滾輪過快 | Delta 累加不設限，但 val 會被 clamp 在 0-100 |
| 切換到 LIVE 模式 | 速度強制為 1x（Live 模式固定正常速度） |
| SYNC 開啟時速度不一致 | 遵循 `_coordinator.SetSpeed()` 同步邏輯（現有機制） |

---

## 8. 測試驗收標準

1. [ ] 滑桿最左 (val=0) → speed=-16x（播放中）或 -2x（暫停中）
2. [ ] 滑桿中點 (val=50) → speed=1x（播放中）或 0x（暫停中）
3. [ ] 滑桿最右 (val=100) → speed=16x（播放中）或 2x（暫停中）
4. [ ] 拖曳滑桿 → 速度永久改變
5. [ ] 點擊滑桿（不拖曳）→ 暫時變速，放開後回歸預設
6. [ ] 滑鼠滾輪在滑桿上 → 暫時調整速度
7. [ ] 播放↔暫停切換 → 滑桿狀態正確更新
8. [ ] 速度顯示格式正確（"-4x"、"0.25x"、"0x"）
9. [ ] `dotnet build` → 0 errors 0 warnings
