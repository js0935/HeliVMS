using Xunit;

// Media 測試會以 stub 啟動外部進程（cmd stub 模擬 ffprobe／ffmpeg）。
// 組內並行使同時產生的進程數量暴增，在低規格 CI 上會因 CreateProcess 節流而偶發逾時。
// 改為依序執行，與進程總量解耦，從根本消除這類 flaky。
[assembly: CollectionBehavior(DisableTestParallelization = true)]