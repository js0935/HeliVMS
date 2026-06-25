# 功能選單 Nx Witness 風格改造 — 設計筆記

## 狀態
討論中（尚未完成設計，也未實作）

## 現狀摘要
MainWindow 導覽架構：
- 48px 左側窄側欄（6 顆導覽鈕 + 3 顆工具鈕），僅圖示無文字標籤
- SubMenuDrawer：Settings 11 項平鋪、Device 5 項平鋪
- ContextMenu：側欄按鈕右鍵（Live/Device/EMap/Settings/User）
- 底部 status bar
- 無頂部選單列

## 已辨識的缺口（與 Nx Witness 比較）

1. **無頂部選單列**（File / View / Tools / Help 等）
2. **子選單分類不夠精細** — Settings 11 項全平鋪、Device 5 項，缺少分組標題 / 折疊分類
3. **右鍵 ContextMenu 覆蓋面不足** — 僅側欄按鈕有，主內容區 / 空白處缺少全域右鍵
4. **側欄按鈕缺少文字標籤** — hover 只靠 ToolTip

## 待辦（下次討論用）
- [ ] 釐清優先補哪一塊
- [ ] 是否需要頂部 menu bar
- [ ] SubMenuDrawer 分組 / 折疊分類方案
- [ ] 全域 ContextMenu 項目
- [ ] 側欄按鈕文字標籤設計（hover expand? 永遠顯示?）
