using System;

namespace HeliVMS.Shared;

/// <summary>
/// ????????????????????????????????
/// </summary>
public class SupervisorRestartPolicy
{
    private readonly TimeSpan _cooldown;
    private readonly int _maxRestartsInWindow;
    private readonly TimeSpan _window;
    private DateTime _lastRestart = DateTime.MinValue;
    private int _restartCount;
    private DateTime _windowStart = DateTime.UtcNow;

    public SupervisorRestartPolicy(TimeSpan? cooldown = null, int maxRestartsInWindow = 10, TimeSpan? window = null)
    {
        _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
        _maxRestartsInWindow = maxRestartsInWindow;
        _window = window ?? TimeSpan.FromMinutes(5);
    }

    public bool ShouldRestart()
    {
        var now = DateTime.UtcNow;
        if (now - _lastRestart < _cooldown) return false;
        if (now - _windowStart > _window)
        {
            _windowStart = now;
            _restartCount = 0;
        }
        if (_restartCount >= _maxRestartsInWindow) return false;
        _restartCount++;
        _lastRestart = now;
        return true;
    }

    public void RecordRestart() { }
}
