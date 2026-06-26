# Rewind Speed Formula Fix Report

## Bug
`SliderValueToSpeed` in `Views/PlaybackView.xaml.cs:1987` used `Math.Pow(2, offset)` where `offset = (sliderVal - 50.0) / 12.5`. When `sliderVal < 50`, a negative offset yields fractional values (e.g., `2^-4 = 0.06`) instead of negative speeds (e.g., `-16x`).

## Fix
Changed to `sign * Math.Pow(2, Math.Abs(offset))` to produce correct negative speeds for rewind.

## Verified Values
| sliderVal | Speed |
|-----------|-------|
| 0         | -16x  |
| 25        | -4x   |
| 50        | 1x    |
| 75        | 4x    |
| 100       | 16x   |

## Build Result
**建置成功。0 個警告 0 個錯誤**
