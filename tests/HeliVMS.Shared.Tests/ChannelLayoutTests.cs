using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 監看格位配置（<see cref="ChannelLayout"/>）的行為契約。
/// </summary>
/// <remarks>
/// 監看格位＝頻道在清單中的索引（<c>ReconcileAsync</c> 依序派格），所以「指定第幾格」＝重排。
/// 這裡鎖的是只有真的設定過才會遇到的狀況：指定格位、兩台搶同一格、格號超出頻道數。
/// </remarks>
public sealed class ChannelLayoutTests
{
    [Fact]
    public void 未指定格位時維持原本順序()
    {
        var ordered = ChannelLayout.ReorderByCell(new[] { 10, 20, 30 }, _ => (int?)null);

        Assert.Equal(new[] { 10, 20, 30 }, ordered);
    }

    [Fact]
    public void 指定格位會把頻道排到該索引()
    {
        // 30 指定第 0 格、10 指定第 2 格；20 未指定，補中間。
        static int? Cell(int id) => id switch { 30 => 0, 10 => 2, _ => null };

        var ordered = ChannelLayout.ReorderByCell(new[] { 10, 20, 30 }, Cell);

        Assert.Equal(new[] { 30, 20, 10 }, ordered);
    }

    [Fact]
    public void 兩台指定同一格時先到先得且不遺漏()
    {
        // 10 與 20 都指定第 0 格：先出現的 10 取得該格，20 退回自動補位（第 1 格）。
        static int? Cell(int id) => id is 10 or 20 ? 0 : null;

        var ordered = ChannelLayout.ReorderByCell(new[] { 10, 20, 30 }, Cell);

        Assert.Equal(new[] { 10, 20, 30 }, ordered);
    }

    [Theory]
    [InlineData(32, 32)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public void 可選格數等於授權路數(int licensed, int expected)
    {
        Assert.Equal(expected, ChannelLayout.SelectableCells(licensed));
    }

    [Fact]
    public void 授權超過最大格數時封頂在監看區格數()
    {
        // 買到 128 路也只有 64 格可顯示，選到不存在的格等於沒設。
        Assert.Equal(ChannelLayout.MaxCells, ChannelLayout.SelectableCells(128));
    }

    [Fact]
    public void 已被佔用的格位會列出且排除正在編輯的頻道自己()
    {
        // 2 自己佔第 3 格；若沒排除自己，編輯 2 時它反而會看到自己的格位被標成佔用。
        var channels = new (int Id, int? Cell)[] { (1, 0), (2, 3), (4, 5) };

        var taken = ChannelLayout.TakenCells(channels, exceptId: 2);

        Assert.Equal(new[] { 0, 5 }, taken.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 兩台同佔一格時編輯其一仍視另一台的格為佔用()
    {
        // 舊資料留下的同格衝突：編輯 2 時第 0 格仍被 1 佔用，一格一機的約束要繼續成立。
        var channels = new (int Id, int? Cell)[] { (1, 0), (2, 0) };

        Assert.Equal(new[] { 0 }, ChannelLayout.TakenCells(channels, exceptId: 2).ToArray());
        Assert.Equal(new[] { 0 }, ChannelLayout.TakenCells(channels, exceptId: 1).ToArray());
    }

    [Fact]
    public void 未指定格位的頻道不算佔用()
    {
        var taken = ChannelLayout.TakenCells(new (int Id, int? Cell)[] { (1, null), (2, null) }, exceptId: null);

        Assert.Empty(taken);
    }

    [Fact]
    public void 指定格位超出頻道數時不遺漏任何頻道()
    {
        // 只有 3 台卻指定第 99 格：不可能落在那裡，只能退回自動補位，但一台都不能消失。
        static int? Cell(int id) => id == 10 ? 99 : null;

        var ordered = ChannelLayout.ReorderByCell(new[] { 10, 20, 30 }, Cell);

        Assert.Equal(3, ordered.Count);
        Assert.Equal(new[] { 10, 20, 30 }, ordered.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 空清單時新頻道落在第一格()
    {
        Assert.Equal(0, ChannelLayout.NextFreeCell(Array.Empty<int?>()));
    }

    [Fact]
    public void 名稱依格子連續遞增()
    {
        // 第一台落在第 0 格（＝攝影機01）；寫入 display_cell 後，下一台補到第 1 格（＝攝影機02），依此類推。
        Assert.Equal(0, ChannelLayout.NextFreeCell(Array.Empty<int?>()));
        Assert.Equal(1, ChannelLayout.NextFreeCell(new int?[] { 0 }));
        Assert.Equal(2, ChannelLayout.NextFreeCell(new int?[] { 0, 1 }));
    }

    [Fact]
    public void 指定格位空出的縫隙會被新頻道補上()
    {
        // 既有 [第0格, 第2格]：第1格空著，新頻道補進第1格（＝攝影機02，而非第3格）。
        Assert.Equal(1, ChannelLayout.NextFreeCell(new int?[] { 0, 2 }));
    }

    [Fact]
    public void 未指定格位的舊頻道視為排在前面()
    {
        // 舊資料沒有 display_cell（null），等同依序自動補位；新頻道排在它們之後。
        Assert.Equal(2, ChannelLayout.NextFreeCell(new int?[] { null, null }));
    }

    [Fact]
    public void 往前搬移時其餘順序遞補()
    {
        // 把第 4 台拖到第 2 格：B、C 往前遞補，D 插進第 2 格。
        var moved = ChannelLayout.Move(new[] { "A", "B", "C", "D" }, from: 3, to: 1);

        Assert.Equal(new[] { "A", "D", "B", "C" }, moved);
    }

    [Fact]
    public void 往後搬移時其餘順序遞補()
    {
        var moved = ChannelLayout.Move(new[] { "A", "B", "C", "D" }, from: 0, to: 2);

        Assert.Equal(new[] { "B", "C", "A", "D" }, moved);
    }

    [Fact]
    public void 原地或不合法索引的搬移不改變順序()
    {
        var items = new[] { "A", "B", "C" };

        Assert.Equal(items, ChannelLayout.Move(items, 1, 1));
        Assert.Equal(items, ChannelLayout.Move(items, -1, 0));
        Assert.Equal(items, ChannelLayout.Move(items, 0, 5));
        Assert.Equal(items, ChannelLayout.Move(items, 3, 0));
    }

    [Fact]
    public void 群組頁簽只挑出成員並依群組內順序排列()
    {
        // 全部頻道 [10,20,30]，群組成員順序 [30,10]：只留這兩台，且照群組順序（30 在前）。
        var members = ChannelLayout.SelectInOrder(
            new[] { 10, 20, 30 },
            x => x,
            new[] { 30, 10 });

        Assert.Equal(new[] { 30, 10 }, members);
    }

    [Fact]
    public void 群組成員已被刪除時直接略過而非留下空格()
    {
        // 群組記著 [10,99,20] 但 99 已被刪；頁簽應顯示 [10,20]，不能出現指向不存在頻道的空格。
        var members = ChannelLayout.SelectInOrder(
            new[] { 10, 20 },
            x => x,
            new[] { 10, 99, 20 });

        Assert.Equal(new[] { 10, 20 }, members);
    }

    [Fact]
    public void 空群組回傳空序列()
    {
        var members = ChannelLayout.SelectInOrder(
            new[] { 10, 20 },
            x => x,
            Array.Empty<int>());

        Assert.Empty(members);
    }
}
