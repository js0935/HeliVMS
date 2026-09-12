---
軟體屬名：禾秝軟體開發團隊
代碼撰寫：洪俊士
版本：v1.0.0
---

# HeliVms 品牌視覺規範（Logo / 圖示 / 啟動畫面）

## 1. 設計語言

- **主題**：專業監控鏡頭意象——「深藍夜色中的鏡頭光圈＋拍攝刻度」
- **風格**：Fluent 深色、扁平、冷色科技感（與 §8.1 深色主題一致）
- **主色**（Design Tokens，對應 §8.2）：
  | 令牌 | 用途 | 值 |
  |---|---|---|
  | `Brand.BgDeep` | 基底深藍 | `#0B1B2E` |
  | `Brand.BgGrad` | 漸層端 | `#1C3A63` |
  | `Brand.Ring` | 鏡頭環（淡藍白） | `#D6E8FF` |
  | `Brand.Accent` | 內圈藍 | `#2660AA` |
  | `Brand.SplashBg` | 啟動背景漸層 | `#090F1E` → `#102240` |
  | `Brand.TextDim` | 次要文字 | `#8296B4` |
- **字型**：HeliVms 字樣 → `Segoe UI Semibold`／中文 → `Microsoft JhengHei`

## 2. Logo 元件定義（app_logo.png，256×256）

```
圓角方底（R=36）深藍漸層＋淡藍細邊
  頂部三條「拍攝角度支架」短線（圓頭）
  中央鏡頭：
    外鏡環 淡藍白環（粗9）
    鏡片   近乎黑深藍 實心圓
    內圈   藍色細環（Rem 3）
    高光   右上兩點白色鏡面反光
  底部 HeliVms 字樣
```

## 3. 程式 Icon（app.ico）

- 內嵌 **7 種尺寸**：16 / 24 / 32 / 48 / 64 / 128 / 256（Vista+ PNG 內嵌 ICO 格式）
- 用於：可執行檔 `ApplicationIcon`、視窗左上 `Window.Icon`、工作列、系統匣、安裝包
- 整合要點：
  - `HeliVms.App.csproj`：`<ApplicationIcon>..\..\Assets\app.ico</ApplicationIcon>`
  - `MainWindow.Icon` 指向 `Assets\app.ico`；系統匣（NotifyIcon）使用 32/48 尺寸

## 4. 啟動畫面（Splash，960×540）

- 版面（由上而下）：
  1. 中央 Logo（260×260，主視覺）
  2. `HeliVms` 主標（正黑體 Bold 40px，白）
  3. `智慧 32 路 NVR/VMS 錄影系統` 副標（22px，藍 `#78AFFF`）
  4. `禾秝軟體開發團隊`（15px，淡灰）— 品牌署名
- 技術規格：
  - 大小 **960×540**；`AllowsTransparency=True`、`ShowInTaskbar=False`、`WindowStyle=None`、置中
  - 使用該圖以 `ImageBrush` 滿版（或 `splash.png` 視窗自適應縮放）
  - 顯示時限：**≤3 秒**；主窗 Loaded 後淡出（FadeOut 200ms）再關閉，避免殘影
  - 啟動平行化：服務連線、授權驗證（§19）、DB 開啟可在 Splash 期間背景完成

## 5. 檔案清單（Assets/）

| 檔案 | 用途 | 尺寸 |
|---|---|---|
| `app_logo.png` | 主 Logo（原圖） | 256×256 |
| `app.ico` | 程式 Icon（多尺寸） | 16–256 |
| `splash.png` | 啟動畫面 | 960×540 |

> 重產方式：`Tools/BrandGen/`（C#，net10.0-windows + System.Drawing.Common 9.0.4），於該目錄執行 `dotnet run -c Release` 即可重新產生三檔（輸出至 `Assets/`）。

## 6. 驗收標準

- [ ] `app.ico` 於工作列／視窗標題／about box 顯示清晰（16px 不糊）
- [ ] Splash ≤3 秒淡出，無白閃、無殘影
- [ ] 中文標題以 Microsoft JhengHei 正常渲染（無方塊）
- [ ] 與深色主題佈局和諧（色票一致）