using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace HeliVMS.Services;

public sealed class PluginLoadContext : AssemblyLoadContext {
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName name) {
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string name) {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}

public sealed class PluginHost : IPluginHost {
    public ICameraService Cameras { get; }
    public IEventService Events { get; }
    public IRecordingService Recordings { get; }
    public IEventRuleService EventRules { get; }

    public PluginHost(ICameraService cameras, IEventService events,
                      IRecordingService recordings, IEventRuleService eventRules) {
        Cameras = cameras;
        Events = events;
        Recordings = recordings;
        EventRules = eventRules;
    }

    public Task LogInfoAsync(string message) {
        Events.LogInfo("Plugin", "PluginHost", message);
        return Task.CompletedTask;
    }

    public Task LogWarningAsync(string message) {
        Events.LogWarning("Plugin", "PluginHost", message);
        return Task.CompletedTask;
    }

    public Task LogErrorAsync(string message) {
        Events.LogError("Plugin", "PluginHost", message);
        return Task.CompletedTask;
    }
}

public sealed class PluginLoaderService : IDisposable {
    private readonly List<(IPlugin Plugin, PluginLoadContext Context)> _loaded = [];
    private readonly IPluginHost _host;
    private readonly string _pluginDir;

    public IReadOnlyList<IPlugin> Plugins => _loaded.Select(x => x.Plugin).ToList();

    public PluginLoaderService(ICameraService cameras, IEventService events,
                                IRecordingService recordings, IEventRuleService eventRules) {
        _host = new PluginHost(cameras, events, recordings, eventRules);
        _pluginDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
    }

    public void LoadAll() {
        if (!Directory.Exists(_pluginDir)) {
            Directory.CreateDirectory(_pluginDir);
            Serilog.Log.Information("[Plugins] Plugin directory created at {Dir}", _pluginDir);
            return;
        }

        foreach (var dll in Directory.GetFiles(_pluginDir, "*.dll")) {
            try {
                var ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadFromAssemblyPath(dll);
                var pluginTypes = asm.GetExportedTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);

                foreach (var type in pluginTypes) {
                    if (Activator.CreateInstance(type) is IPlugin plugin) {
                        plugin.InitializeAsync(_host).GetAwaiter().GetResult();
                        _loaded.Add((plugin, ctx));
                        Serilog.Log.Information("[Plugins] Loaded plugin {Name} v{Version}", plugin.Name, plugin.Version);
                    }
                }
            } catch (Exception ex) {
                Serilog.Log.Warning(ex, "[Plugins] Failed to load {Dll}", dll);
            }
        }

        Serilog.Log.Information("[Plugins] Loaded {Count} plugin(s)", _loaded.Count);
    }

    public void UnloadAll() {
        foreach (var (plugin, _) in _loaded) {
            try {
                plugin.ShutdownAsync().GetAwaiter().GetResult();
                Serilog.Log.Information("[Plugins] Unloaded plugin {Name}", plugin.Name);
            } catch (Exception ex) {
                Serilog.Log.Warning(ex, "[Plugins] Error unloading plugin {Name}", plugin.Name);
            }
        }
        _loaded.Clear();
    }

    public void Dispose() {
        UnloadAll();
    }
}
