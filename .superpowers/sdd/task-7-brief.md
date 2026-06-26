### Task 7: Final Build & Verify

- [ ] **Step 1: Full solution build**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

- [ ] **Step 2: Verify all handlers referenced in XAML exist in code-behind**

Check each removed/added handler:
- PlayPauseBtn_Click ✓
- SpeedSlider_ValueChanged ✓
- LiveBtn_Checked, LiveBtn_Unchecked ✓
- SyncBtn_Checked, SyncBtn_Unchecked ✓
- ClndBtn_Click, ClndCalendar_SelectedDatesChanged, ClndCalendar_Today_Click ✓
- ThmbBtn_Checked, ThmbBtn_Unchecked ✓
- Removed: JumpStartBtn_Click, JumpEndBtn_Click, StopBtn_Click, Loop*, PlayBtn_Click, PauseBtn_Click, SpeedCombo_SelectionChanged ✓

- [ ] **Step 3: Verify no hardcoded colors**

Check all new XAML elements for `Background="White"` or similar — all must use `{DynamicResource ...}`.

- [ ] **Step 4: Commit**

```powershell
git add -A
git commit -m "PlaybackView: Nx Witness 風格播放面板改造 — Speed Slider/LIVE/SYNC/CLND/THMB + 傳輸列簡化"
```