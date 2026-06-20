// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public class UserService : IUserService
{
    private static readonly string _dataPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "users.json");

    private List<User> _users = new();
    private readonly object _lock = new();
    private readonly IEventService _eventLog;

    public UserService(IEventService eventLog)
    {
        _eventLog = eventLog;
        LoadFromDisk();
        EnsureDefaultUsers();
    }

    public List<User> GetAllUsers()
    {
        lock (_lock)
        {
            var clones = new List<User>(_users.Count);
            for (int i = 0; i < _users.Count; i++)
            {
                clones.Add(CloneUser(_users[i]));
            }
            return clones;
        }
    }

    public User? GetUserById(string id)
    {
        lock (_lock)
        {
            for (int i = 0; i < _users.Count; i++)
            {
                if (_users[i].Id == id) { return _users[i]; }
            }
            return null;
        }
    }

    public User? GetUserByUsername(string username)
    {
        lock (_lock)
        {
            for (int i = 0; i < _users.Count; i++)
            {
                if (_users[i].Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    return _users[i];
                }
            }
            return null;
        }
    }

    public User? ValidateUser(string username, string passwordHash)
    {
        lock (_lock)
        {
            for (int i = 0; i < _users.Count; i++)
            {
                var u = _users[i];
                if (u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
                    && u.PasswordHash == passwordHash && u.IsEnabled)
                {
                    return u;
                }
            }
            return null;
        }
    }

    public bool CreateUser(User user, string password)
    {
        lock (_lock)
        {
            bool taken = false;
            for (int i = 0; i < _users.Count; i++)
            {
                if (_users[i].Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase))
                { taken = true; break; }
            }
            if (taken) return false;

            user.Id = Guid.NewGuid().ToString();
            user.PasswordHash = AuthenticationService.HashPasswordPBKDF2(password);
            user.CreatedAt = DateTime.Now;
            _users.Add(user);
            SaveToDisk();
        }
        _eventLog.LogInfo(EventCategories.Create, "UserService", $"已建立使用者{user.Username}", $"角色: {user.Role}");
        return true;
    }

    public bool UpdateUser(User user)
    {
        string? oldName = null;
        lock (_lock)
        {
            var idx = _users.FindIndex(u => u.Id == user.Id);
            if (idx < 0) return false;
            oldName = _users[idx].Username;
            _users[idx] = user;
            SaveToDisk();
        }
        _eventLog.LogInfo(EventCategories.Update, "UserService", $"已更新使用者{oldName ?? user.Username}");
        return true;
    }

    public bool DeleteUser(string id)
    {
        string? name = null;
        lock (_lock)
        {
            for (int i = 0; i < _users.Count; i++)
            {
                if (_users[i].Id != id) { continue; }
                name = _users[i].Username;
                _users.RemoveAt(i);
                SaveToDisk();
                break;
            }
        }
        _eventLog.LogInfo(EventCategories.Delete, "UserService", $"已刪除使用者{name ?? id}");
        return true;
    }

    public bool IsUsernameTaken(string username)
    {
        lock (_lock)
        {
            for (int i = 0; i < _users.Count; i++)
            {
                if (_users[i].Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_dataPath)) return;
            var json = File.ReadAllText(_dataPath);
            var raw = JsonSerializer.Deserialize<List<User>>(json) ?? new();
            var filtered = new List<User>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                if (!string.IsNullOrEmpty(raw[i].Username))
                {
                    filtered.Add(raw[i]);
                }
            }
            _users = filtered;

            if (_users.Count != raw.Count)
                SaveToDisk();
        }
        catch { _users = new(); }
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dataPath);
            if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var valid = new List<User>(_users.Count);
            for (int i = 0; i < _users.Count; i++)
            {
                if (!string.IsNullOrEmpty(_users[i].Username))
                {
                    valid.Add(_users[i]);
                }
            }
            var json = JsonSerializer.Serialize(valid, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_dataPath, json);
        }
        catch { }
    }

    private void EnsureDefaultUsers()
    {
        lock (_lock)
        {
            var defaults = new[]
            {
                new { Username = "admin",  Password = "admin", DisplayName = "Administrator", Role = UserRole.Admin },
                new { Username = "user",   Password = "user",  DisplayName = "Operator",  Role = UserRole.Operator },
                new { Username = "guest",  Password = "guest", DisplayName = "Guest",        Role = UserRole.Viewer },
            };

            bool changed = false;
            foreach (var d in defaults)
            {
                bool exists = false;
                for (int i = 0; i < _users.Count; i++)
                {
                    if (_users[i].Username.Equals(d.Username, StringComparison.OrdinalIgnoreCase))
                    { exists = true; break; }
                }
                if (exists) { continue; }

                _users.Add(new User
                {
                    Id = Guid.NewGuid().ToString(),
                    Username = d.Username,
                    PasswordHash = AuthenticationService.HashPasswordPBKDF2(d.Password),
                    DisplayName = d.DisplayName,
                    Role = d.Role,
                    IsEnabled = true,
                    CreatedAt = DateTime.Now
                });
                changed = true;
            }

            if (changed) { SaveToDisk(); }
        }
    }

    private static User CloneUser(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        PasswordHash = u.PasswordHash,
        DisplayName = u.DisplayName,
        Role = u.Role,
        Email = u.Email,
        IsEnabled = u.IsEnabled,
        IsAutoLogin = u.IsAutoLogin,
        CreatedAt = u.CreatedAt,
        LastLoginAt = u.LastLoginAt,
        TwoFactorSecret = u.TwoFactorSecret,
        IsTwoFactorEnabled = u.IsTwoFactorEnabled,
        Permissions = new List<string>(u.Permissions)
    };
}
