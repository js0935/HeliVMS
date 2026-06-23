using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IExportService {
    Task<string> ExportAsync(ExportRequest request, IProgress<double>? progress = null);
    string GetDefaultExportPath();
}
