using HeliVMS.Models;

namespace HeliVMS.Services;

public interface ILicenseService
{
    LicenseInfo CurrentLicense { get; }
    void Load();
    bool Activate(string licenseKey);
    void Remove();
    int GetMaxCameras();
    string ExportLicense();
    bool ImportLicense(string filePath);
}
