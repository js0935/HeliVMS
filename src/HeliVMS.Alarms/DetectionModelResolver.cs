using System.IO;

namespace HeliVMS.Alarms;

/// <summary>依序找尋 YOLOv8 ONNX 模型檔（§5.2）：環境變數 → 資料根目錄 models\。</summary>
public static class DetectionModelResolver
{
    public const string DefaultFileName = "yolov8n.onnx";

    /// <summary>回傳第一個存在的模型檔路徑；找不到回 null。</summary>
    public static string? TryResolve()
    {
        var candidates = new List<string>();

        var env = Environment.GetEnvironmentVariable("HELIVMS_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            candidates.Add(env);
        }

        var dataRoot = Environment.GetEnvironmentVariable("HELIVMS_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HeliVMS");
        candidates.Add(Path.Combine(dataRoot, "models", DefaultFileName));
        candidates.Add(Path.Combine("C:", "HeliVMSData", "models", DefaultFileName));

        return candidates.FirstOrDefault(File.Exists);
    }
}