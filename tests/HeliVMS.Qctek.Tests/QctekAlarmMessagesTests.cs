using HeliVMS.Qctek.Alarm;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 警報訊息組裝：欄位順序與清理規則。
/// </summary>
/// <remarks>
/// 這些字串直接決定 CMS 端顯示的事件時間與名稱——格式錯一個欄位，CMS 會把
/// 「年」讀成「秒」（實測過的反向錯位），事件時間全錯而兩邊都不會報錯。
/// </remarks>
public class QctekAlarmMessagesTests
{
    [Fact]
    public void HELLO格式逐字比對()
    {
        Assert.Equal("ALARM_HELLO 3 =QCV8001", QctekAlarmMessages.Hello(3, "QCV8001"));
    }

    [Fact]
    public void 訊息欄位順序與真機一致()
    {
        // 真機（QCV8001 alarm_to_cms → QCC QC_RM_Alarm 解析）：
        // ALARM_MSG <連線id> <相機號> <年> <月> <日> <時> <分> <秒>
        //   $Name:<名稱> Describe:<描述> <類型> $Pri:<優先級> $Live:-1 =<裝置>
        var msg = QctekAlarmMessages.Msg(
            connectionId: 1,
            cameraNumber: 5,
            localTime: new DateTime(2026, 11, 8, 9, 7, 6),
            name: "北門",
            describe: "motion",
            priority: 2,
            deviceInfo: "QCV8001",
            eventType: "motion");

        Assert.Equal(
            "ALARM_MSG 1 5 2026 11 8 9 7 6 $Name:北門 Describe:motion motion $Pri:2 $Live:-1 =QCV8001",
            msg);
    }

    [Fact]
    public void 名稱與描述裡的錢字號會被移除()
    {
        // CMS 用 strstr 掃「$」找自訂欄位：值裡出現 $ 會被當成新欄位開頭，
        // 後面的 $Pri: 反而變成名稱的一部分——事件名稱直接寫進報表，不能讓它帶偏解析。
        var msg = QctekAlarmMessages.Msg(
            1, 2, new DateTime(2026, 1, 1, 0, 0, 1),
            name: "A$Name:x", describe: "d$Pri:9", priority: 0, deviceInfo: "QCV8001");

        Assert.Contains("$Name:AName:x Describe:dPri:9", msg);
        Assert.Equal(3, msg.Count(c => c == '$')); // 只剩 $Name: $Pri: $Live: 三個欄位記號
    }

    [Fact]
    public void 名稱裡的換行不會弄壞單行協議()
    {
        var msg = QctekAlarmMessages.Msg(
            1, 2, new DateTime(2026, 1, 1, 0, 0, 1),
            name: "A\r\nB", describe: "x", priority: 0, deviceInfo: "QCV8001");

        Assert.Contains("$Name:A  B Describe:x", msg);
        Assert.DoesNotContain("\r", msg);
        Assert.DoesNotContain("\n", msg);
    }

    [Fact]
    public void 星期與月份用真機相同的一到十二月表示()
    {
        // 真機送的是日曆欄位（月 1..12），不是 .NET 的零基索引；
        // 這條測試釘住「直接寫 DateTime.Month」而不是任何加減一。
        var msg = QctekAlarmMessages.Msg(
            1, 2, new DateTime(2026, 12, 31, 23, 59, 58), "n", "d", 0, "i");
        Assert.Contains(" 2026 12 31 23 59 58 ", msg);
    }
}
