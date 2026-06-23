using System.Text.Json;
using System.Text.Json.Serialization;
using HeliVMS.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;

namespace HeliVMS.Services;

public sealed class WebApiHostService : IDisposable {
    private readonly ICameraService _cameras;
    private readonly IEventService _events;
    private readonly IRecordingService _recording;
    private WebApplication? _app;

    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private const int DefaultPort = 5000;

    public int Port { get; set; } = DefaultPort;

    public WebApiHostService(ICameraService cameras, IEventService events, IRecordingService recording) {
        _cameras = cameras;
        _events = events;
        _recording = recording;
    }

    public void Start() {
        if (_app is not null) return;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            EnvironmentName = Environments.Production,
        });

        builder.WebHost.UseUrls($"http://+:{Port}");
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c => {
            c.SwaggerDoc("v1", new() { Title = "HeliVMS API", Version = "v1" });
        });

        _app = builder.Build();

        _app.UseSwagger();
        _app.UseSwaggerUI();

        MapEndpoints(_app);

        _app.RunAsync();
        Serilog.Log.Information("[WebAPI] REST API started on port {Port}", Port);
    }

    public void Stop() {
        _app?.StopAsync().GetAwaiter().GetResult();
        _app = null;
    }

    private void MapEndpoints(WebApplication app) {
        app.MapGet("/api/v1/cameras", () => {
            var all = _cameras.GetAllCameras();
            return Results.Json(all.Select(c => new {
                c.Id, c.Name, c.IpAddress, c.Port, c.Group,
                c.IsConnected, c.IsRecordingEnabled, c.IsMotionDetectionEnabled,
                c.HasPTZ, c.Manufacturer, c.Model,
                c.RtspUrl, c.RtspUrlSub,
                LastConnectedAt = c.LastConnectedAt,
                LastDisconnectedAt = c.LastDisconnectedAt,
            }), JsonOpts);
        });

        app.MapGet("/api/v1/cameras/{id}", (string id) => {
            var cam = _cameras.GetCameraById(id);
            return cam is not null ? Results.Json(cam, JsonOpts) : Results.NotFound();
        });

        app.MapGet("/api/v1/streams/{id}/live", (string id) => {
            var cam = _cameras.GetCameraById(id);
            if (cam is null) return Results.NotFound();
            return Results.Json(new {
                cam.Id, cam.Name,
                MainStream = cam.RtspUrl,
                SubStream = cam.RtspUrlSub,
                OnvifMain = cam.OnvifResolvedUrl,
                OnvifSub = cam.OnvifResolvedUrlSub,
            }, JsonOpts);
        });

        app.MapGet("/api/v1/events", ([Microsoft.AspNetCore.Mvc.FromQuery] string? category,
                                       [Microsoft.AspNetCore.Mvc.FromQuery] string? severity,
                                       [Microsoft.AspNetCore.Mvc.FromQuery] int count = 100) => {
            var results = _events.QueryEvents(category, severity, null, count);
            return Results.Json(results, JsonOpts);
        });

        app.MapGet("/api/v1/health", () => {
            var all = _cameras.GetAllCameras();
            var online = all.Count(c => c.IsConnected);
            var recordings = _recording.GetActiveRecordings().Count;
            return Results.Json(new {
                TotalCameras = all.Count,
                OnlineCameras = online,
                ActiveRecordings = recordings,
                Status = online == all.Count ? "healthy" : "degraded",
                Timestamp = DateTime.Now,
            }, JsonOpts);
        });

        app.MapGet("/", () => Results.Redirect("/swagger"));
    }

    public void Dispose() {
        Stop();
    }
}
