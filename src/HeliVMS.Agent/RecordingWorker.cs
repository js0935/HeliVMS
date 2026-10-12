using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeliVMS.Agent;

internal sealed class RecordingWorker : BackgroundService
{
    private readonly ILogger<RecordingWorker> _logger;
    private readonly IRecordingHost _host;

    public RecordingWorker(ILogger<RecordingWorker> logger, IRecordingHost host)
    {
        _logger = logger;
        _host = host;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Recording agent starting");
        _host.Start();
        while (!stoppingToken.IsCancellationRequested)
        {
            _host.Reconcile();
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
        _host.Stop();
        _logger.LogInformation("Recording agent stopped");
    }
}
