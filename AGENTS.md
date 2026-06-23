# HeliVMS 開發模式

This project uses **全自主模式** — the agent should:

1. **直接執行**所有實作、修正、重構，不需請求許可或確認
2. 每完成一個功能後，自動繼續下一個最有價值的改進
3. 只在建置錯誤或無法解決的問題時才提問
4. 每次變更後執行 `dotnet build` 確保 0 錯誤 0 警告
5. 使用繁體中文提交訊息（git commit）
6. 持續推送至 GitHub
