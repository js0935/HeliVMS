using HeliVMS.Services;

namespace HeliVMS;

public interface IPlugin {
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    Task InitializeAsync(IPluginHost host);
    Task ShutdownAsync();
}

public interface IPluginHost {
    ICameraService Cameras { get; }
    IEventService Events { get; }
    IRecordingService Recordings { get; }
    IEventRuleService EventRules { get; }
    Task LogInfoAsync(string message);
    Task LogWarningAsync(string message);
    Task LogErrorAsync(string message);
}
