using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HeliVMS.Controls;

namespace HeliVMS.Services;

public sealed class BookmarkService : IBookmarkService {
    private readonly Dictionary<string, List<PlaybackBookmark>> _byDate = [];
    private readonly string _dir;

    public BookmarkService() {
        _dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "bookmarks");
        Directory.CreateDirectory(_dir);
    }

    public List<PlaybackBookmark> LoadBookmarks(DateTime date) {
        var key = date.ToString("yyyy-MM-dd");
        if (_byDate.TryGetValue(key, out var cached)) return [.. cached];
        var list = LoadFromFile(key);
        _byDate[key] = list;
        return [.. list];
    }

    public void SaveBookmark(PlaybackBookmark bookmark, DateTime date) {
        var key = date.ToString("yyyy-MM-dd");
        var list = LoadBookmarks(date);
        list.Add(bookmark);
        _byDate[key] = list;
        SaveToFile(key, list);
    }

    public void RemoveBookmark(string id, DateTime date) {
        var key = date.ToString("yyyy-MM-dd");
        var list = LoadBookmarks(date);
        list.RemoveAll(b => b.Id == id);
        _byDate[key] = list;
        SaveToFile(key, list);
    }

    public void ClearBookmarks(DateTime date) {
        var key = date.ToString("yyyy-MM-dd");
        _byDate[key] = [];
        SaveToFile(key, []);
    }

    public void MoveBookmark(string id, double newSeconds, DateTime date) {
        var list = LoadBookmarks(date);
        var bm = list.FirstOrDefault(b => b.Id == id);
        if (bm is not null) { bm.Seconds = newSeconds; SaveToFile(date.ToString("yyyy-MM-dd"), list); }
    }

    public void RenameBookmark(string id, string newName, DateTime date) {
        var list = LoadBookmarks(date);
        var bm = list.FirstOrDefault(b => b.Id == id);
        if (bm is not null) { bm.Note = newName; SaveToFile(date.ToString("yyyy-MM-dd"), list); }
    }

    private List<PlaybackBookmark> LoadFromFile(string key) {
        try {
            var path = Path.Combine(_dir, $"{key}.json");
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<PlaybackBookmark>>(json) ?? [];
        } catch { return []; }
    }

    private void SaveToFile(string key, List<PlaybackBookmark> list) {
        try {
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_dir, $"{key}.json"), json);
        } catch { }
    }
}
