namespace HeliVMS.Storage;

public sealed class SegmentShardRegistry
{
    private readonly string _baseDir;
    private readonly SqliteStore _hotStore;

    public SegmentShardRegistry(SqliteStore hotStore)
    {
        _hotStore = hotStore ?? throw new ArgumentNullException(nameof(hotStore));
        _baseDir = Path.GetDirectoryName(Path.GetFullPath(hotStore.DatabasePath)) ?? Directory.GetCurrentDirectory();
    }

    public string GetHotDbPath() => _hotStore.DatabasePath;

    public string GetShardPath(int year, int month) => Path.Combine(_baseDir, SegmentShardPaths.GetShardFileName(year, month));

    public IReadOnlyList<string> ListShardPaths()
    {
        var list = new List<string>();
        if (!Directory.Exists(_baseDir)) return list;
        foreach (var file in Directory.GetFiles(_baseDir, "index-????????.db"))
        {
            var name = Path.GetFileName(file);
            if (SegmentShardPaths.TryParseShardFileName(name) is not null)
            {
                list.Add(Path.GetFullPath(file));
            }
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public SqliteStore OpenHot() => _hotStore;

    public SqliteStore OpenShard(int year, int month)
    {
        var path = GetShardPath(year, month);
        var store = new SqliteStore(path);
        store.Initialize();
        return store;
    }

    public SqliteStore OpenShard(string shardPath)
    {
        var store = new SqliteStore(shardPath);
        store.Initialize();
        return store;
    }
}
