using HeliVMS.Qctek.Alarm;

namespace HeliVMS.Qctek.Tests;

/// <summary>NVR→CMS 警報訊息組裝（已還原格式的部分）。</summary>
public class QctekAlarmMessagesTests
{
    [Fact]
    public void 上線問候格式與QCV8001發送端逐字一致()
    {
        // 發送端反編譯字面：sprintf(buf, "ALARM_HELLO %d =%s", id, info)。
        Assert.Equal("ALARM_HELLO 3 =demo info", QctekAlarmMessages.Hello(3, "demo info"));
    }

    [Fact]
    public void 存活通知沿用問候的形狀()
    {
        Assert.Equal("ALARM_KEEP_ALIVE 3 =demo info", QctekAlarmMessages.KeepAlive(3, "demo info"));
    }
}
