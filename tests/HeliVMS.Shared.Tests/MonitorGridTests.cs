using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 監看格線排版（<see cref="MonitorGrid"/>）的行為契約。
/// </summary>
/// <remarks>
/// 原本「n 路就 n×n、格線撐滿監看區」的結果是每格長寬比等於整個監看區的比例，
/// 在非 16:9 的視窗或 4:3 攝影機上就會有大片黑邊。這裡鎖的是新行為：
/// 每格維持固定畫面比例、格線置中、列欄依區域形狀自動挑選。
/// </remarks>
public sealed class MonitorGridTests
{
    private const double Eps = 1e-6;

    [Fact]
    public void 單格時維持畫面比例並填滿可用區域()
    {
        var fit = MonitorGrid.Fit(1, 1600, 900);

        Assert.Equal(1, fit.Rows);
        Assert.Equal(1, fit.Capacity);
        Assert.Equal(1, fit.Columns);
        Assert.Equal(1600, fit.Width, 6);
        Assert.Equal(900, fit.Height, 6);
    }

    [Fact]
    public void 四路在十六比九區域排出二乘二()
    {
        var fit = MonitorGrid.Fit(4, 1600, 900);

        Assert.Equal(2, fit.Rows);
        Assert.Equal(2, fit.Columns);
        Assert.Equal(1600, fit.Width, 6);
        Assert.Equal(900, fit.Height, 6);
    }

    [Fact]
    public void 六路在橫向區域排出三欄兩列()
    {
        // 舊行為下 6 不是平方數、根本沒有這個選項；新行為要能依區域挑最緊的排列。
        var fit = MonitorGrid.Fit(6, 1600, 900);

        Assert.Equal(2, fit.Rows);
        Assert.Equal(3, fit.Columns);
        Assert.Equal(6, fit.Capacity);
    }

    [Fact]
    public void 六路在直立區域排出兩欄三列()
    {
        var fit = MonitorGrid.Fit(6, 900, 1200);

        Assert.Equal(3, fit.Rows);
        Assert.Equal(2, fit.Columns);
        Assert.Equal(6, fit.Capacity);
    }

    [Fact]
    public void 兩路在超寬區域排出單列兩欄()
    {
        var fit = MonitorGrid.Fit(2, 2560, 1080);

        Assert.Equal(1, fit.Rows);
        Assert.Equal(2, fit.Columns);
    }

    [Fact]
    public void 兩路在直立區域排出兩列單欄()
    {
        var fit = MonitorGrid.Fit(2, 900, 1600);

        Assert.Equal(2, fit.Rows);
        Assert.Equal(1, fit.Columns);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(36)]
    [InlineData(42)]
    [InlineData(56)]
    [InlineData(64)]
    public void 每格維持固定比例且外框不出界並貼齊一邊(int cells)
    {
        (double W, double H)[] areas = [(1600, 900), (900, 1600), (2560, 1080), (1280, 1024)];
        const double aspect = 16.0 / 9.0;

        foreach (var (areaW, areaH) in areas)
        {
            var fit = MonitorGrid.Fit(cells, areaW, areaH, aspect);

            Assert.True(fit.Capacity >= cells, $"capacity {fit.Capacity} < {cells}");
            Assert.True(fit.Width <= areaW + 1e-6, $"width {fit.Width} > {areaW}");
            Assert.True(fit.Height <= areaH + 1e-6, $"height {fit.Height} > {areaH}");

            var cellWidth = fit.Width / fit.Columns;
            var cellHeight = fit.Height / fit.Rows;
            Assert.Equal(aspect, cellWidth / cellHeight, 6);

            // 置中的外框一定貼齊可用區域的其中一邊，否則還能再放大。
            var touches = Math.Abs(fit.Width - areaW) < 1e-6 || Math.Abs(fit.Height - areaH) < 1e-6;
            Assert.True(touches, $"外框未貼齊區域：{fit.Width}×{fit.Height} in {areaW}×{areaH}");
        }
    }

    [Fact]
    public void 區域為零時回退成正方形且尺寸為零()
    {
        var fit = MonitorGrid.Fit(4, 0, 0);

        Assert.Equal(2, fit.Rows);
        Assert.Equal(2, fit.Columns);
        Assert.Equal(0, fit.Width);
        Assert.Equal(0, fit.Height);
    }

    [Fact]
    public void 非法比例回退成十六比九()
    {
        var fit = MonitorGrid.Fit(1, 1600, 900, 0);

        Assert.Equal(1600, fit.Width, 6);
        Assert.Equal(900, fit.Height, 6);
    }

    [Fact]
    public void 可指定四比三比例()
    {
        var fit = MonitorGrid.Fit(4, 1600, 1200, 4.0 / 3.0);

        Assert.Equal(2, fit.Rows);
        Assert.Equal(2, fit.Columns);
        Assert.Equal(1600, fit.Width, 6);
        Assert.Equal(1200, fit.Height, 6);
        Assert.Equal(4.0 / 3.0, fit.Width / fit.Columns / (fit.Height / fit.Rows), 6);
    }

    [Fact]
    public void 格數小於一視為單格()
    {
        var fit = MonitorGrid.Fit(0, 1600, 900);

        Assert.Equal(1, fit.Rows);
        Assert.Equal(1, fit.Columns);
    }

    [Fact]
    public void 六路在很高的直立區域仍取兩欄三列而非單欄六列()
    {
        // 單欄六列每格只比 2×3 大約 5%，不值得換成長條；容差機制應改取較均衡的 2×3。
        var fit = MonitorGrid.Fit(6, 900, 1600);

        Assert.Equal(3, fit.Rows);
        Assert.Equal(2, fit.Columns);
    }

    [Fact]
    public void 九路在超寬區域取三欄三列而非五欄兩列()
    {
        var fit = MonitorGrid.Fit(9, 3000, 1000);

        Assert.Equal(3, fit.Rows);
        Assert.Equal(3, fit.Columns);
    }

    [Theory]
    [InlineData(6, 2, 3)]
    [InlineData(12, 3, 4)]
    [InlineData(20, 4, 5)]
    [InlineData(30, 5, 6)]
    [InlineData(42, 6, 7)]
    [InlineData(56, 7, 8)]
    [InlineData(64, 8, 8)]
    public void 貼合十六比九的路數在橫向區域排出無空格的矩形(int cells, int rows, int cols)
    {
        // 佈局下拉新增的 6／12／20／30／42／56／64 路都是能整齊貼合寬螢幕的 m×n 矩形，
        // 在 16:9 監看區應該排出剛好的欄列、不留空格（不該退化成有空格或有黑邊的排列）。
        var fit = MonitorGrid.Fit(cells, 1600, 900);

        Assert.Equal(rows, fit.Rows);
        Assert.Equal(cols, fit.Columns);
        Assert.Equal(cells, fit.Capacity);
    }
}
