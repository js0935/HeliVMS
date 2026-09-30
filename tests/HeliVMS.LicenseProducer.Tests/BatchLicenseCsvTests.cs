using System.Security.Cryptography;
using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;
using HeliVMS.LicenseProducer;

namespace HeliVMS.LicenseProducer.Tests;

/// <summary>
/// 批次簽發的 CSV 輸入（§19.6「批次簽發：匯入 CSV（設備碼/等級/max/到期）一次簽發多張」）。
/// </summary>
/// <remarks>
/// 重點不在「能讀 CSV」，而在**壞掉時的行為**：批次常是數百列，簽到一半才停會留下半套
/// 已發出去的授權，故解析器本身不碰金鑰、且全有或全無，這裡逐條釘死。
/// </remarks>
public class BatchLicenseCsvTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
    private const string MachineA = "0123456789ABCDEF0123456789ABCDEF";
    private const string MachineB = "FEDCBA9876543210FEDCBA9876543210";

    [Fact]
    public void Parse_ValidFile_YieldsOneRowPerLine()
    {
        var csv = $"""
            machine,tier,cameras,expires,customer
            {MachineA},進階版,,2027-09-13,台南廠
            {MachineB},專業版,8,2027-01-01,高雄廠
            """;

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("台南廠", result.Rows[0].Customer);
        Assert.Equal("進階版", result.Rows[0].TierName);
        Assert.Equal(32, result.Rows[0].Cameras);
        Assert.Equal(new DateTime(2027, 9, 13, 0, 0, 0, DateTimeKind.Utc), result.Rows[0].ExpiresUtc);
        Assert.Equal(2, result.Rows[0].Line);
        Assert.Equal(8, result.Rows[1].Cameras);
        Assert.True(result.Rows[1].CamerasExplicit);
    }

    [Fact]
    public void Parse_BlankExpires_IsPerpetual()
    {
        var result = BatchLicenseCsv.Parse(
            $"machine,tier,expires\n{MachineA},企業版,\n", Now);

        Assert.True(result.Success);
        Assert.Null(result.Rows[0].ExpiresUtc);
    }

    [Fact]
    public void Parse_BlankFeatures_UsesTierFeatures()
    {
        var result = BatchLicenseCsv.Parse($"machine,tier\n{MachineA},基本版\n", Now);

        Assert.True(result.Success);
        Assert.Null(result.Rows[0].Features);
        Assert.Equal(4, result.Rows[0].Cameras);
    }

    [Fact]
    public void Parse_ExplicitFeatures_OverrideTier()
    {
        var csv = $"machine,tier,features\n{MachineA},基本版,\"core,ai,gis\"\n";

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
        Assert.Equal(["core", "ai", "gis"], result.Rows[0].Features!);
    }

    [Fact]
    public void Parse_FeaturesSplitOnSemicolonToo()
    {
        // CSV 裡逗號要引號，廠商更可能直接用分號或空白分隔。
        var csv = $"machine,tier,features\n{MachineA},基本版,core;ai;gis\n";

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
        Assert.Equal(["core", "ai", "gis"], result.Rows[0].Features!);
    }

    [Fact]
    public void Parse_QuotedCustomerWithComma_IsOneField()
    {
        var csv = $"machine,tier,customer\n{MachineA},基本版,\"台南廠, A 棟\"\n";

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
        Assert.Equal("台南廠, A 棟", result.Rows[0].Customer);
    }

    [Fact]
    public void Parse_EscapedQuoteInCustomer()
    {
        var csv = "machine,tier,customer\n" + MachineA + ",基本版,\"台南\"\"廠\"\n";

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
        Assert.Equal("台南\"廠", result.Rows[0].Customer);
    }

    [Fact]
    public void Parse_BomCrlfAndCommentLines_AreTolerated()
    {
        // Excel 另存 CSV 一定帶 BOM 與 CRLF，前面還常有匯出說明。
        var csv = $"\uFEFF# 2026Q1 訂單\r\nmachine,tier\r\n{MachineA},基本版\r\n\r\n{MachineB},專業版\r\n";

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Parse_MachineIsUppercased()
    {
        var csv = $"machine,tier\n{MachineA.ToLowerInvariant()},基本版\n";

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
        Assert.Equal(MachineA, result.Rows[0].Machine);
    }

    [Fact]
    public void Parse_StarMeansAnyMachine()
    {
        var result = BatchLicenseCsv.Parse("machine,tier\n*,客製版\n", Now);

        Assert.True(result.Success);
        Assert.True(result.Rows[0].AnyMachine);
        Assert.Equal(string.Empty, result.Rows[0].Machine);
    }

    [Fact]
    public void Parse_BlankMachine_IsRejected()
    {
        // 留空等於發一張誰都能用的卡，寧可拒簽。
        var result = BatchLicenseCsv.Parse("machine,tier\n,基本版\n", Now);

        Assert.False(result.Success);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Errors, e => e.Column == "machine");
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("0123456789ABCDEF0123456789ABCDEG")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0")]
    public void Parse_InvalidMachine_IsRejected(string machine)
    {
        var result = BatchLicenseCsv.Parse($"machine,tier\n{machine},基本版\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "machine");
    }

    [Fact]
    public void Parse_UnknownTier_IsRejectedWithAvailableNames()
    {
        var result = BatchLicenseCsv.Parse($"machine,tier\n{MachineA},豪華版\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "tier" && e.Message.Contains("基本版", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_NoTierAndNoCameras_IsRejected()
    {
        var result = BatchLicenseCsv.Parse($"machine\n{MachineA}\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "tier");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("1025")]
    [InlineData("abc")]
    public void Parse_BadCameras_IsRejected(string cameras)
    {
        var result = BatchLicenseCsv.Parse($"machine,cameras\n{MachineA},{cameras}\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "cameras");
    }

    [Fact]
    public void Parse_CamerasAtMaxIsAllowed()
    {
        var result = BatchLicenseCsv.Parse($"machine,cameras\n{MachineA},1024\n", Now);

        Assert.True(result.Success);
        Assert.Equal(1024, result.Rows[0].Cameras);
    }

    [Fact]
    public void Parse_UnknownColumn_IsRejected()
    {
        // camaers 若被默默忽略，就會照等級預設值簽 4 路出去，而且沒有任何錯誤。
        var result = BatchLicenseCsv.Parse($"machine,camaers\n{MachineA},16\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "camaers");
    }

    [Fact]
    public void Parse_MissingRequiredColumn_IsRejected()
    {
        var result = BatchLicenseCsv.Parse($"tier,cameras\n基本版,4\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("machine", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DuplicateColumn_IsRejected()
    {
        var result = BatchLicenseCsv.Parse($"machine,machine\n{MachineA},{MachineB}\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("欄名重複", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ExtraColumnInRow_IsRejected()
    {
        // 多出來的欄位若被忽略，資料會錯位到別的欄位去簽。
        var result = BatchLicenseCsv.Parse($"machine,tier\n{MachineA},基本版,多一格\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("欄數", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_MissingValueInShortRow_IsRejectedWhenMachineAbsent()
    {
        var result = BatchLicenseCsv.Parse($"machine,tier\n,{LicenseTiers.FeatureCore}\n", Now);

        Assert.False(result.Success);
    }

    [Fact]
    public void Parse_ExpiredDate_IsRejected()
    {
        // 簽出去的卡一裝就失效，多半是年份打錯。
        var result = BatchLicenseCsv.Parse($"machine,tier,expires\n{MachineA},基本版,2025-01-01\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "expires" && e.Message.Contains("過期", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2027-13-45")]
    [InlineData("下個月")]
    public void Parse_GarbageDate_IsRejected(string expires)
    {
        var result = BatchLicenseCsv.Parse($"machine,tier,expires\n{MachineA},基本版,{expires}\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "expires");
    }

    [Fact]
    public void Parse_IsoDateTimeIsAccepted()
    {
        var csv = $"machine,tier,expires\n{MachineA},基本版,2027-03-01T12:00:00Z\n";

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
        Assert.Equal(new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc), result.Rows[0].ExpiresUtc);
    }

    [Fact]
    public void Parse_DuplicateMachine_IsRejectedWithBothLines()
    {
        var csv = $"""
            machine,tier
            {MachineA},基本版
            {MachineA.ToLowerInvariant()},專業版
            """;

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "machine" && e.Message.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DuplicateLicenseId_IsRejected()
    {
        var csv = $"""
            machine,tier,id
            {MachineA},基本版,DUP-1
            {MachineB},基本版,DUP-1
            """;

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "id");
    }

    [Fact]
    public void Parse_IdWithWhitespace_IsRejected()
    {
        var result = BatchLicenseCsv.Parse($"machine,tier,id\n{MachineA},基本版,\"A B\"\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Column == "id");
    }

    [Fact]
    public void Parse_EmptyInput_IsRejected()
    {
        var result = BatchLicenseCsv.Parse(string.Empty, Now);

        Assert.False(result.Success);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Parse_HeaderOnly_IsRejected()
    {
        var result = BatchLicenseCsv.Parse("machine,tier\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("沒有資料列", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_UnclosedQuote_IsRejected()
    {
        var result = BatchLicenseCsv.Parse($"machine,tier,customer\n{MachineA},基本版,\"台南廠\n", Now);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("引號", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_OneBadRowAmongMany_ReportsAllAndSignsNothing()
    {
        // 全有或全無：一列壞掉就整批不簽，且要把所有問題一次講清楚。
        var csv = $"""
            machine,tier,expires
            {MachineA},基本版,2027-01-01
            BAD-MACHINE,基本版,2027-01-01
            {MachineB},豪華版,2027-01-01
            {MachineA},基本版,2025-01-01
            """;

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.False(result.Success);
        Assert.Empty(result.Rows);
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Line == 3 && e.Column == "machine");
        Assert.Contains(result.Errors, e => e.Line == 4 && e.Column == "tier");
        Assert.Contains(result.Errors, e => e.Line == 5 && e.Column == "expires");
    }

    [Fact]
    public void Parse_ErrorsAreSortedByLine()
    {
        var csv = $"""
            machine,tier,expires
            {MachineA},基本版,2025-01-01
            NOPE,基本版,2027-01-01
            """;

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.False(result.Success);
        Assert.Equal(result.Errors.Select(e => e.Line).OrderBy(l => l), result.Errors.Select(e => e.Line));
    }

    [Fact]
    public void Parse_ArbitraryMachineDoesNotTripDuplicateCheck()
    {
        var csv = $"""
            machine,tier
            *,基本版
            *,專業版
            """;

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.True(result.Success);
    }

    [Fact]
    public void Issue_Error_ShouldBeZero()
    {
        // 錯誤訊息要指得出是哪一列，否則 500 列的 CSV 得自己數行號。
        var csv = $"machine,tier\n{MachineA},基本版\nBAD,基本版\n";

        var result = BatchLicenseCsv.Parse(csv, Now);

        Assert.False(result.Success);
        Assert.Equal("第 3 行 [machine]：設備碼必須是 32 碼十六進位（§19.1），或明確填 * 表示不綁定機器；留空會發出一張任何機器都能用的授權，故視為錯誤。",
            result.Errors[0].ToString());
    }

    [Theory]
    [InlineData("台南廠", "台南廠")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    [InlineData("a/b\\c", "a_b_c")]
    public void FileStem_StripsPathSeparators(string customer, string expected)
    {
        var row = MakeRow(customer: customer);

        Assert.Equal(expected, row.FileStem);
    }

    [Fact]
    public void FileStem_FallsBackToIdWhenNoCustomer()
    {
        var row = MakeRow(customer: string.Empty, licenseId: "LIC-42");

        Assert.Equal("LIC-42", row.FileStem);
    }

    [Fact]
    public void FileStem_FallsBackToLineWhenNothingUsable()
    {
        var row = MakeRow(customer: string.Empty);

        Assert.Equal("license-0007", row.FileStem);
    }

    private static BatchLicenseRow MakeRow(string customer, string? licenseId = null)
        => new(
            7,
            customer,
            MachineA,
            false,
            null,
            "基本版",
            4,
            false,
            null,
            licenseId,
            null,
            null,
            null);
}
