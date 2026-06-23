using HeliVMS.Controls;

namespace HeliVMS.Services;

public interface IBookmarkService {
    List<PlaybackBookmark> LoadBookmarks(DateTime date);
    void SaveBookmark(PlaybackBookmark bookmark, DateTime date);
    void RemoveBookmark(string id, DateTime date);
    void ClearBookmarks(DateTime date);
    void MoveBookmark(string id, double newSeconds, DateTime date);
    void RenameBookmark(string id, string newName, DateTime date);
}
