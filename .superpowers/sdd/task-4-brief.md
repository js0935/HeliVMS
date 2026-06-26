### Task 4: Remove Deprecated Handlers + Cleanup

**Files:**
- Modify: `Views/PlaybackView.xaml.cs`

- [ ] **Step 1: Remove deprecated handler methods**

Remove these entire method bodies (keep stubs that just log? No, just remove):
- `JumpStartBtn_Click` (lines 1987-1990)
- `JumpEndBtn_Click` (lines 1992-1995)
- `StopBtn_Click` (lines 1908-1910)
- `LoopSetABtn_Click` (lines 1944-1950)
- `LoopSetBBtn_Click` (lines 1952-1957)
- `LoopToggleBtn_Click` (lines 1959-1962)
- `LoopClearBtn_Click` (lines 1964-1968)
- `UpdateLoopVisual` (lines 1971-1985)
- `PlayBtn_Click` (lines 1896-1900)
- `PauseBtn_Click` (lines 1902-1906)
- `SpeedCombo_SelectionChanged` (lines 1918-1927)

Keep `StopAllBtn_Click` (line 1912). Remove `StopBtn_Click`.

Remove `_loopA`, `_loopB`, `_loopEnabled` fields (lines 60-62).

- [ ] **Step 2: Update FsTransportBtn_Click**

Remove "jumpStart" and "jumpEnd" cases. Update "play" case to use PlayPauseBtn:
```csharp
case "play":
case "pause":
    PlayPauseBtn_Click(sender, e);
    break;
```

- [ ] **Step 3: Clean up using directives**

Remove any unused using directives that were only used by removed code.

- [ ] **Step 4: Build and verify**

```powershell
dotnet build "D:\HeliVMS\HeliVMS\HeliVMS.csproj" 2>&1
```
Expected: 0 errors 0 warnings

---