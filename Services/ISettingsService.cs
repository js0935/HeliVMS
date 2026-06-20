using HeliVMS.Models;

namespace HeliVMS.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Save();
    void Reload();
}
