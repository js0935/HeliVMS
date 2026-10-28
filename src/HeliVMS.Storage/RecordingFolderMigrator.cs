namespace HeliVMS.Storage;

/// <summary>一次資料夾搬移的統計。</summary>
public sealed record RecordingFolderMigrationResult(int MovedFolders, int MergedFolders, int UpdatedSegments, int SkippedFolders)
{
    public bool Changed => MovedFolders > 0 || MergedFolders > 0 || UpdatedSegments > 0;
}

/// <summary>
/// 把既有錄影資料夾改成「以頻道名稱命名」（M311）。
///
/// <para>
/// 早期的錄影寫在 <c>HeliVMSRecordings\ch001\…</c>；改成以頻道名稱命名後，舊資料夾若放著不動，
/// 同一路錄影就會散在兩個目錄（舊的看得到、新錄的在另一個），操作員在磁碟上根本認不出來。
/// 因此啟動時主動把舊資料夾搬到對應命名，並回寫 <c>segments.file_path</c>——回放、備援、保留清理
/// 全都只認資料庫裡的路徑，路徑不回寫等於把既有錄影變成孤兒。
/// </para>
/// <para>
/// 全程**盡力而為**：資料夾被佔用（正在錄影）、目標已存在、來源已不存在等情況一律跳過該筆而不中斷，
/// 下次呼叫再處理。搬移是 <see cref="Directory.Move(string, string)"/>（同磁碟為改名，瞬間完成），
/// 只有目標目錄已存在時才逐項合併。方法本身可重複執行：名稱已正確的資料夾不會被碰。
/// </para>
/// <para>
/// 掃描只讀「舊制 <c>ch…</c> 目錄」的區段列（<see cref="SegmentRepository.ListLegacyFolderPaths"/>）：
/// 已命名為頻道名稱的列在 SQL 就濾掉，因此搬移完成後的每次啟動只會掃到零列，
/// 不會為了「確認沒事」把千萬列全部載進記憶體。
/// </para>
/// </summary>
public static class RecordingFolderMigrator
{
    /// <summary>
    /// 依目前頻道名稱把舊命名資料夾搬正並回寫索引。回傳本次實際搬動／更新的數量。
    /// </summary>
    public static RecordingFolderMigrationResult Run(SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var channels = new ChannelRepository(store).List();
        var desired = RecordingFolderNaming.Resolve(channels);
        if (desired.Count == 0)
        {
            return new RecordingFolderMigrationResult(0, 0, 0, 0);
        }

        var segments = new SegmentRepository(store).ListLegacyFolderPaths();

        // 依「目前的頻道資料夾」分組。每個資料夾理論上只屬於一個頻道（舊制 ch{id} 或新制名稱皆唯一），
        // 但仍以防禦性的方式處理：若同一資料夾內的區段算出的目標名稱不一致，就整組跳過，不亂搬。
        var byFolder = new Dictionary<string, List<(long Id, string FilePath)>>(StringComparer.OrdinalIgnoreCase);
        var folderChannel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderAmbiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seg in segments)
        {
            if (!desired.TryGetValue(seg.ChannelId, out var targetName))
            {
                continue;
            }

            if (!TryGetChannelFolder(seg.FilePath, out var oldFolder))
            {
                continue;
            }

            if (folderChannel.TryGetValue(oldFolder, out var existing) && existing != seg.ChannelId)
            {
                folderAmbiguous.Add(oldFolder);
            }
            else
            {
                folderChannel[oldFolder] = seg.ChannelId;
            }

            if (!byFolder.TryGetValue(oldFolder, out var list))
            {
                list = [];
                byFolder[oldFolder] = list;
                folderTarget[oldFolder] = targetName;
            }

            list.Add((seg.Id, seg.FilePath));
        }

        var moved = 0;
        var merged = 0;
        var updated = 0;
        var skipped = 0;
        var pathUpdates = new List<(long Id, string NewPath)>();

        foreach (var (oldFolder, items) in byFolder)
        {
            if (folderAmbiguous.Contains(oldFolder))
            {
                skipped++;
                continue;
            }

            var parent = Path.GetDirectoryName(oldFolder);
            if (string.IsNullOrEmpty(parent))
            {
                skipped++;
                continue;
            }

            var targetName = folderTarget[oldFolder];
            var newFolder = Path.Combine(parent, targetName);
            if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 名稱已正確
            }

            var outcome = MoveOrMerge(oldFolder, newFolder);
            if (outcome == MoveOutcome.Moved)
            {
                moved++;
            }
            else if (outcome == MoveOutcome.Merged)
            {
                merged++;
            }
            else if (outcome == MoveOutcome.Skipped)
            {
                skipped++;
                continue; // 搬不成，路徑不可改，否則會指向不存在的位置
            }

            foreach (var (id, filePath) in items)
            {
                var suffix = Path.GetRelativePath(oldFolder, filePath);
                // Preserve structure after channel folder (may include HH/)
                pathUpdates.Add((id, Path.Combine(newFolder, suffix)));
            }
        }

        if (pathUpdates.Count > 0)
        {
            var repo = new SegmentRepository(store);
            store.InTransaction(() =>
            {
                foreach (var (id, newPath) in pathUpdates)
                {
                    repo.UpdateFilePath(id, newPath);
                }
            });
            updated = pathUpdates.Count;

            new AuditLogRepository(store).Record(
                "system",
                "recording.folder.migrate",
                AuditCategories.Config,
                targetType: "segment",
                detail: $"資料夾以頻道名稱命名：搬移 {moved}、合併 {merged}、回寫 {updated} 筆路徑");
        }

        return new RecordingFolderMigrationResult(moved, merged, updated, skipped);
    }

    private enum MoveOutcome
    {
        Moved,
        Merged,
        AlreadyThere,
        Skipped,
    }

    private static MoveOutcome MoveOrMerge(string oldFolder, string newFolder)
    {
        if (!Directory.Exists(oldFolder))
        {
            // 來源不在：可能是上一次已搬過（新目錄在），也可能檔案早已遺失。前者仍要回寫路徑。
            return Directory.Exists(newFolder) ? MoveOutcome.AlreadyThere : MoveOutcome.Skipped;
        }

        if (!Directory.Exists(newFolder))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFolder)!);
                Directory.Move(oldFolder, newFolder);
                return MoveOutcome.Moved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 檔案被佔用（正在錄影）或權限不足：交由下一次處理。
                return MoveOutcome.Skipped;
            }
        }

        try
        {
            var collision = MergeDirectory(oldFolder, newFolder);
            if (collision)
            {
                return MoveOutcome.Skipped;
            }

            Directory.Delete(oldFolder, recursive: true);
            return MoveOutcome.Merged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MoveOutcome.Skipped;
        }
    }

    /// <summary>把 <paramref name="source"/> 底下的檔案搬到 <paramref name="destination"/>；同名者保留既有。</summary>
    private static bool MergeDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (!File.Exists(target))
            {
                File.Move(file, target);
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 由區段檔路徑取回「頻道資料夾」的絕對路徑：結構為 <c>{root}\{頻道資料夾}\{yyyyMMdd}\{HH}\seg-*.mp4</c> 或舊 <c>{root}\{頻道資料夾}\{yyyyMMdd}\seg-*.mp4</c>，
    /// 取到頻道資料夾。
    /// </summary>
    private static bool TryGetChannelFolder(string filePath, out string folder)
    {
        folder = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(filePath);
            var segDir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(segDir))
            {
                return false;
            }

            var dayDir = Path.GetDirectoryName(segDir);
            if (string.IsNullOrEmpty(dayDir))
            {
                return false;
            }

            // Check if segDir is HH (2 digits)
            var segDirName = Path.GetFileName(segDir);
            if (!string.IsNullOrEmpty(segDirName) && segDirName.Length == 2 && int.TryParse(segDirName, out _))
            {
                var channelFolder2 = Path.GetDirectoryName(dayDir);
                if (string.IsNullOrEmpty(channelFolder2))
                {
                    return false;
                }
                folder = channelFolder2;
                return true;
            }

            var channelFolder = dayDir;
            folder = channelFolder;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
