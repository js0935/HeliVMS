using System;
using HeliVMS.Shared;
using Xunit;

public class SupervisorRestartPolicyTests
{
    [Fact]
    public void 初次應該允許重啟()
    {
        var p = new SupervisorRestartPolicy(cooldown: TimeSpan.FromSeconds(10));
        Assert.True(p.ShouldRestart());
    }

    [Fact]
    public void 冷卻時間內不允許重啟()
    {
        var p = new SupervisorRestartPolicy(cooldown: TimeSpan.FromSeconds(10), window: TimeSpan.FromMinutes(1), maxRestartsInWindow: 5);
        Assert.True(p.ShouldRestart());
        Assert.False(p.ShouldRestart());
    }
}
