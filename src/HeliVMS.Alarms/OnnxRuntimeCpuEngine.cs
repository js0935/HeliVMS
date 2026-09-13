using HeliVMS.Shared.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HeliVMS.Alarms;

/// <summary>
/// YOLOv8 COCO 物件偵測 ONNX 引擎（ONNX Runtime，純 CPU；§5.1 L1）。
/// 輸入 BGR24 幀 → letterbox 640×640（RGB 正規化）→ 推理 → 解碼 + NMS → 僅輸出感興趣類別。
/// </summary>
public sealed class OnnxRuntimeCpuEngine : IDetectionEngine
{
    private const int InputSize = 640;
    private const float ConfidenceThreshold = 0.35f;
    private const float NmsThreshold = 0.45f;

    // COCO 類別名稱（僅需感興趣索引；解碼時直接印名）
    private static readonly string[] CocoNames =
    {
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck",
    };

    private static readonly HashSet<int> TargetIndexes = new() { 0, 1, 2, 3, 5, 6, 7 };

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public OnnxRuntimeCpuEngine(string modelPath)
    {
        var options = new SessionOptions
        {
            EnableMemoryPattern = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        ModelPath = modelPath;
    }

    /// <summary>已載入模型路徑（診斷用）。</summary>
    public string ModelPath { get; } = "";

    /// <summary>執行推理並回傳目標類別（原座標 0..1 xywh）。</summary>
    public IReadOnlyList<Detection> Run(VideoFrame frame)
    {
        var inputTensor = new DenseTensor<float>(Prepare(frame), [1, 3, InputSize, InputSize]);
        var named = NamedOnnxValue.CreateFromTensor(_inputName, inputTensor);
        using var outputs = _session.Run(new[] { named });
        using var output = outputs.First();
        var tensor = output.AsTensor<float>().ToArray();

        var rows = tensor.Length / 8400;
        var best = new List<(float Score, int Cls, float X, float Y, float W, float H)>();
        for (var anchor = 0; anchor < 8400; anchor++)
        {
            var bestScore = ConfidenceThreshold;
            var bestCls = -1;
            for (var cls = 4; cls < rows; cls++)
            {
                var score = tensor[cls * 8400 + anchor];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCls = cls;
                }
            }

            if (bestCls >= 0)
            {
                var classId = bestCls - 4;   // 前 4 列為邊界
                if (classId < CocoNames.Length && TargetIndexes.Contains(classId))
                {
                    var cx = tensor[anchor];
                    var cy = tensor[8400 + anchor];
                    var bw = tensor[2 * 8400 + anchor];
                    var bh = tensor[3 * 8400 + anchor];
                    best.Add((bestScore, classId, cx / InputSize, cy / InputSize, bw / InputSize, bh / InputSize));
                }
            }
        }

        ApplyNms(best);
        return best
            .Select(d => new Detection(CocoNames[d.Cls], d.Score, d.X, d.Y, d.W, d.H))
            .ToList();
    }

    private static void ApplyNms(List<(float Score, int Cls, float X, float Y, float W, float H)> boxes)
    {
        var ordered = boxes
            .OrderByDescending(b => b.Score)
            .ThenBy(b => b.Cls)
            .ToList();
        boxes.Clear();
        for (var i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            var keep = true;
            for (var j = 0; j < i; j++)
            {
                var b = ordered[j];
                if (b.Cls != a.Cls)
                {
                    continue;
                }

                if (ComputeIoU(a, b) > NmsThreshold)
                {
                    keep = false;
                    break;
                }
            }

            if (keep)
            {
                boxes.Add(a);
            }
        }
    }

    private static float ComputeIoU(
        (float Score, int Cls, float X, float Y, float W, float H) a,
        (float Score, int Cls, float X, float Y, float W, float H) b)
    {
        var ax1 = a.X - a.W / 2;
        var ay1 = a.Y - a.H / 2;
        var ax2 = a.X + a.W / 2;
        var ay2 = a.Y + a.H / 2;
        var bx1 = b.X - b.W / 2;
        var by1 = b.Y - b.H / 2;
        var bx2 = b.X + b.W / 2;
        var by2 = b.Y + b.H / 2;

        var ix1 = Math.Max(ax1, bx1);
        var iy1 = Math.Max(ay1, by1);
        var ix2 = Math.Min(ax2, bx2);
        var iy2 = Math.Min(ay2, by2);
        var inter = Math.Max(0, ix2 - ix1) * Math.Max(0, iy2 - iy1);
        var uni = Math.Max(1e-6f, (ax2 - ax1) * (ay2 - ay1) + (bx2 - bx1) * (by2 - by1) - inter);
        return inter / uni;
    }

    /// <summary>BGR24 → letterbox 640×640、RGB、0-1、NCHW。</summary>
    internal static float[] Prepare(VideoFrame frame)
    {
        var scale = Math.Min((float)InputSize / frame.Width, (float)InputSize / frame.Height);
        var newW = (int)Math.Round(frame.Width * scale);
        var newH = (int)Math.Round(frame.Height * scale);
        var padX = (InputSize - newW) / 2;
        var padY = (InputSize - newH) / 2;

        var input = new float[3 * InputSize * InputSize];
        var srcStride = frame.Width * 3;

        for (var y = 0; y < newH; y++)
        {
            var srcY = (int)(y / scale);
            if (srcY >= frame.Height)
            {
                srcY = frame.Height - 1;
            }

            var srcRow = srcY * srcStride;
            for (var x = 0; x < newW; x++)
            {
                var srcX = (int)(x / scale);
                if (srcX >= frame.Width)
                {
                    srcX = frame.Width - 1;
                }

                var s = srcRow + srcX * 3;
                var b = frame.Pixels[s] / 255f;
                var g = frame.Pixels[s + 1] / 255f;
                var r = frame.Pixels[s + 2] / 255f;

                var oy = y + padY;
                var ox = x + padX;
                input[oy * InputSize + ox] = r;                            // R
                input[InputSize * InputSize + oy * InputSize + ox] = g;    // G
                input[2 * InputSize * InputSize + oy * InputSize + ox] = b; // B
            }
        }

        return input;
    }

    public void Dispose() => _session.Dispose();
}