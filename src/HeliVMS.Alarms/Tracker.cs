namespace HeliVMS.Alarms;

/// <summary>追蹤中物件之狀態快照（§5.7）。座標為 0..1 正規化 YOLO xywh。</summary>
public sealed record TrackState(int TrackId, string Class, float X, float Y, float W, float H, int Misses);

/// <summary>
/// 單鏡目標追蹤（§5.7 原語，v2 核心差異）：逐幀對偵測結果以匈牙利最小成本指派
/// （cost＝1－IoU；僅同 Class 且 IoU 達門檻者允許配對）匹配到既有 track；匹配成功以 EMA 平滑 bbox；
/// 未匹配之既有 track 記一次失蹤，連續失蹤逾 maxMisses 幀即汰除；未匹配之新偵測建立新 track
/// （id 由 1 起、不再重用）。純數學、離線可測。
/// </summary>
public sealed class Tracker
{
    private readonly double _iouThreshold;
    private readonly int _maxMisses;
    private readonly double _smoothAlpha;
    private readonly List<StoredTrack> _tracks = new();
    private int _nextId = 1;

    private sealed class StoredTrack
    {
        public int Id;
        public string Class = "";
        public float X, Y, W, H;
        public int Misses;
    }

    public Tracker(double iouThreshold = 0.3, int maxMisses = 5, double smoothAlpha = 0.3)
    {
        _iouThreshold = iouThreshold;
        _maxMisses = maxMisses;
        _smoothAlpha = smoothAlpha;
    }

    /// <summary>目前追蹤中物件數（供測試／診斷）。</summary>
    public int TrackCount => _tracks.Count;

    public void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
    }

    public IReadOnlyList<TrackState> Update(IReadOnlyList<Detection> detections)
    {
        _tracks.RemoveAll(t => t.Misses > _maxMisses);
        var pairs = Assign(_tracks, detections, _iouThreshold);

        var matched = new bool[_tracks.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            var (trackIndex, detIndex) = pairs[i];
            matched[trackIndex] = true;
            var t = _tracks[trackIndex];
            var d = detections[detIndex];
            t.Class = d.Class;
            t.X += (float)(_smoothAlpha * (d.X - t.X));
            t.Y += (float)(_smoothAlpha * (d.Y - t.Y));
            t.W += (float)(_smoothAlpha * (d.W - t.W));
            t.H += (float)(_smoothAlpha * (d.H - t.H));
            t.Misses = 0;
        }

        for (int i = 0; i < _tracks.Count; i++)
        {
            if (!matched[i])
                _tracks[i].Misses++;
        }

        _tracks.RemoveAll(t => t.Misses > _maxMisses);

        var assignedDet = new bool[detections.Count];
        for (int i = 0; i < pairs.Count; i++)
            assignedDet[pairs[i].DetectionIndex] = true;

        for (int i = 0; i < detections.Count; i++)
        {
            if (!assignedDet[i])
            {
                var d = detections[i];
                _tracks.Add(new StoredTrack { Id = _nextId++, Class = d.Class, X = d.X, Y = d.Y, W = d.W, H = d.H });
            }
        }

        var result = new List<TrackState>(_tracks.Count);
        for (int i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            result.Add(new TrackState(t.Id, t.Class, t.X, t.Y, t.W, t.H, t.Misses));
        }
        return result;
    }

    private static List<(int TrackIndex, int DetectionIndex)> Assign(
        List<StoredTrack> tracks, IReadOnlyList<Detection> detections, double iouThreshold)
    {
        var result = new List<(int, int)>();
        int n = tracks.Count, m = detections.Count;
        if (n == 0 || m == 0)
            return result;

        int size = Math.Max(n, m);
        var cost = new double[size, size];
        const double inf = 1e9;
        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                cost[i, j] = 1.0;
                if (i < n && j < m)
                {
                    var t = tracks[i];
                    var d = detections[j];
                    if (t.Class != d.Class)
                    {
                        cost[i, j] = inf;
                        continue;
                    }
                    double iou = IoU(t.X, t.Y, t.W, t.H, d.X, d.Y, d.W, d.H);
                    cost[i, j] = iou >= iouThreshold ? 1.0 - iou : inf;
                }
                else if (i >= n)
                {
                    cost[i, j] = 0.0;
                }
            }
        }

        var raw = HungarianSolver.Solve(cost);
        for (int r = 0; r < raw.Length; r++)
        {
            int c = raw[r];
            if (c >= 0 && r < n && c < m && cost[r, c] < 1.0)
                result.Add((r, c));
        }
        return result;
    }

    private static double IoU(float ax, float ay, float aw, float ah, float bx, float by, float bw, float bh)
    {
        float ix = Math.Max(ax - aw / 2, bx - bw / 2);
        float iy = Math.Max(ay - ah / 2, by - bh / 2);
        float iw = Math.Min(ax + aw / 2, bx + bw / 2) - ix;
        float ih = Math.Min(ay + ah / 2, by + bh / 2) - iy;
        if (iw <= 0 || ih <= 0)
            return 0;
        float inter = iw * ih;
        float uni = aw * ah + bw * bh - inter;
        if (uni <= 0)
            return 0;
        return inter / uni;
    }
}

/// <summary>匈牙利（Kuhn–Munkres）最小成本指派求解器：對方陣（列＝track、欄＝detection）回傳
/// 陣列 assignment[列]＝配對欄索引（-1 表未配對）。0 基索引、虛擬欄置於最後（索引 n）。</summary>
internal static class HungarianSolver
{
    internal static int[] Solve(double[,] cost)
    {
        int n = cost.GetLength(0);
        if (n == 0)
            return Array.Empty<int>();

        int virt = n;
        int[] p = new int[n + 1];
        double[] u = new double[n + 1];
        double[] v = new double[n + 1];
        int[] way = new int[n + 1];
        const double inf = 1e18;
        for (int k = 0; k < n + 1; k++)
            p[k] = -1;

        for (int i = 0; i < n; i++)
        {
            p[virt] = i;
            int j0 = virt;
            double[] minv = new double[n + 1];
            for (int k = 0; k < n + 1; k++)
                minv[k] = inf;
            bool[] used = new bool[n + 1];

            do
            {
                used[j0] = true;
                int i0 = p[j0];
                int j1 = virt;
                double delta = inf;
                for (int j = 0; j < n; j++)
                {
                    if (used[j])
                        continue;
                    double cur = cost[i0, j] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }
                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }
                for (int j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }
                j0 = j1;
            } while (p[j0] != -1);

            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != virt);
        }

        int[] assignment = new int[n];
        for (int j = 0; j < n; j++)
            assignment[j] = -1;
        for (int j = 0; j < n; j++)
        {
            if (p[j] != -1)
                assignment[p[j]] = j;
        }
        return assignment;
    }
}