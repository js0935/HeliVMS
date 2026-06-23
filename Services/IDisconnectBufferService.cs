namespace HeliVMS.Services;

public interface IDisconnectBufferService {
    void Start();
    void Stop();
    int BufferedCount { get; }
    int FlushedCount { get; }
}
