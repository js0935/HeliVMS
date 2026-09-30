# 已停用工具（`_deprecated`）

本目錄僅保留歷史可追溯性，**不得用於簽發正式授權**。

## 內容

| 工具 | 產出格式 | 狀態 |
| --- | --- | --- |
| `LicenseKeyGen` | `Aaaa-Bbbb-Cccc-Dddd`（16 hex，HMAC-SHA256 對稱） | 已停用（§19.5） |

## 為何停用

`LicenseKeyGen` 使用 HMAC 對稱簽章。驗證端必須持有與簽發端**完全相同**的
`HmacSecret`（硬寫在 `LicenseKeyGen/MainWindow.xaml.cs`），因此任何取得產品檔案
的人都能自簽授權金鑰，無法防止偽造。

實務上也從未真正可用：`LicenseKeyGen` 的註解指其 secret 需與
`HeliVMS.Services.LicenseService` 一致，但該類別在本 repo 並不存在，
產品端從未實作此對稱驗證路徑。該工具產出的金鑰套用進產品端必然驗證失敗。

此外，該工具僅取 HMAC 前 1 byte（256 種值）與 4 位 hex 設備前綴作為綁定，
機器碼碰撞機率高，且到期時間以「2025 為基準的年數偏移」編碼，只能表示極少數年份。

## 現行簽發方式

改用 RSA-2048 非對稱簽章（私鑰僅存廠商，產品端只內嵌公鑰）：

```powershell
# CLI 簽發
dotnet run --project Tools/HeliVMS.LicenseProducer -- --help
```

- CLI：`Tools/HeliVMS.LicenseProducer`
- GUI：`Tools/LicenseKeyGenUI`（輸出 `HELVMS-v2.{payload}.{signature}`）

規格見 `docs/ARCHITECTURE.md` §19。