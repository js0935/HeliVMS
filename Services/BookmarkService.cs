using System.IO;
using System.Text.Json;
using HeliVMS.Controls;
using Serilog;

namespace HeliVMS.Services;

public sealed class BookmarkService : IBookmarkService {
    private static readonly string _baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Bookmarks");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string GetFilePath(DateTime date) =>
        Path.Combine(_baseDir, $"bookmarks_{date:yyyyMMdd}.json");

    public List<PlaybackBookmark> LoadBookmarks(DateTime date) {
        try {
            var path = GetFilePath(date);
            if (!File.Exists(path)) { return []; }
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<List<PlaybackBookmark>>(json) ?? [];
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] BookmarkService load error: {Msg}", ex.Message);
            return [];
        }
    }

    public void SaveBookmark(PlaybackBookmark bookmark, DateTime date) {
        try {
            var dir = Path.GetDirectoryName(GetFilePath(date));
            if (dir is not null && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }

            var bookmarks = LoadBookmarks(date);
            bookmarks.RemoveAll(b => b.Id == bookmark.Id);
            bookmarks.Add(bookmark);
            WriteBookmarks(date, bookmarks);
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] BookmarkService save error: {Msg}", ex.Message);
        }
    }

    public void RemoveBookmark(string id, DateTime date) {
        try {
            var bookmarks = LoadBookmarks(date);
            if (bookmarks.RemoveAll(b => b.Id == id) > 0) {
                WriteBookmarks(date, bookmarks);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] BookmarkService remove error: {Msg}", ex.Message);
        }
    }

    public void MoveBookmark(string id, double newSeconds, DateTime date) {
        try {
            var bookmarks = LoadBookmarks(date);
            var bm = bookmarks.Find(b => b.Id == id);
            if (bm is not null) {
                bm.Seconds = newSeconds;
                WriteBookmarks(date, bookmarks);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] BookmarkService move error: {Msg}", ex.Message);
        }
    }

    public void RenameBookmark(string id, string newName, DateTime date) {
        try {
            var bookmarks = LoadBookmarks(date);
            var bm = bookmarks.Find(b => b.Id == id);
            if (bm is not null) {
                bm.Note = newName;
                WriteBookmarks(date, bookmarks);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] BookmarkService rename error: {Msg}", ex.Message);
        }
    }

    public void ClearBookmarks(DateTime date) {
        try {
            var path = GetFilePath(date);
            if (File.Exists(path)) {
                File.Delete(path);
                Log.Debug("[HeliVMS] Bookmarks cleared for {Date:yyyy-MM-dd}", date);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] BookmarkService clear error: {Msg}", ex.Message);
        }
    }

    private static void WriteBookmarks(DateTime date, List<PlaybackBookmark> bookmarks) {
        var path = GetFilePath(date);
        var json = JsonSerializer.Serialize(bookmarks, JsonOptions);
        File.WriteAllText(path, json, System.Text.Encoding.UTF8);
    }
}
