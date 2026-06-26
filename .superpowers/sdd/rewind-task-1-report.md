# Task 1 Report: Formula + Display Updates for Bidirectional Speed Slider

## Implementation

1. **`SliderValueToSpeed`** — changed from `static` to instance method; when `_isPlaying` is true, uses `2^((val-50)/12.5)` centered at 50→1x; when paused, delegates to `SliderValueToSpeedPaused`.

2. **`SliderValueToSpeedPaused`** — new static helper: 50→0x, left side → negative speeds (rewind), right side → positive speeds (forward), using `sign * 0.25 * 2^(|val-50|/16.6667)`.

3. **`SpeedToDisplayText`** — replaced `SliderToDisplayText`; handles negatives (`-2x`, `-0.25x`), zero (`0x`), and absolute value >=1 display.

4. **`ShowSpeedOSDOnPlayers` / `UpdatePlayerSpeedBadges`** — now call `SpeedToDisplayText(rate)`.

5. **`SpeedSlider_ValueChanged`** — uses `SpeedToDisplayText` instead of `SliderToDisplayText`.

6. **Initial speed badge ordering fix** — moved `_isPlaying = true` before the badge-initialization call (line 1432) so the instance method sees the correct state.

## Build

```
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj"
→ 建置成功。0 個警告 0 個錯誤
```

## Self-Review

- All call sites (`line 1432`, `line 1978`, `line 3414`) use the instance method — unchanged syntax, all valid.
- `SpeedToDisplayText` replaces three inline formatting blocks with a single correct implementation.
- Paused-at-center slider returns 0x; playing-at-center returns 1x.
- No hardcoded color codes or formatting violations.

## Concerns

None.
