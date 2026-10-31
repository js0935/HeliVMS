using HeliVMS.Recording;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 回放前收檔的組合器（<see cref="PlaybackRecordingFinalizer"/>）。
///
/// <para>
/// 桌面端有兩條獨立的錄影路徑：監看牆與排程。排程會跳過「已被監看畫面錄影」的頻道，
/// 所以回放前兩邊都要問；只問一邊的結果是同一個「回放」按鈕，因為鏡頭剛好在牆上
/// 而看到不同長度的錄影，而畫面上完全看不出差別。
/// </para>
/// <para>
/// 另一半是失敗容忍：收檔只是「讓最新的一段早點出現」，它不該讓回放開不起來。
/// 任何一個來源擲例外都必須被吞掉、留下一行日誌（否則現場無從查起），
/// 另一個來源收到的段照樣算數。
/// </para>
/// </summary>
public sealed class PlaybackRecordingFinalizerTests
{
    [Fact]
    public async Task 兩個錄影來源的收檔段數會加總且參數原封不動傳下去()
    {
        int? seenChannel = null;
        TimeSpan? seenTimeout = null;
        var finalizer = new PlaybackRecordingFinalizer(
            (channelId, timeout) =>
            {
                seenChannel = channelId;
                seenTimeout = timeout;
                return Task.FromResult(1);
            },
            (_, _) => Task.FromResult(2));

        Assert.Equal(3, await finalizer.FinalizeAsync(7, TimeSpan.FromSeconds(2)));
        Assert.Equal(7, seenChannel);
        Assert.Equal(TimeSpan.FromSeconds(2), seenTimeout);
    }

    [Fact]
    public async Task 一個來源失敗不會吞掉另一個來源收到的段且會留下日誌()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"helivms-playback-test-{Guid.NewGuid():N}.log");
        try
        {
            var secondCalled = false;
            var finalizer = new PlaybackRecordingFinalizer(
                logPath,
                (_, _) => throw new InvalidOperationException("模擬排程壞掉"),
                (_, _) =>
                {
                    secondCalled = true;
                    return Task.FromResult(2);
                });

            Assert.Equal(2, await finalizer.FinalizeAsync(null, TimeSpan.FromSeconds(1)));
            Assert.True(secondCalled, "一個來源失敗不能讓另一個來源的收檔白做。");

            // 沒有這一行日誌，「按了回放卻看不到最新的一段」在現場完全無法查起。
            Assert.True(File.Exists(logPath), "收檔失敗必須留下日誌。");
            Assert.Contains("回放前收檔失敗", File.ReadAllText(logPath));
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task 沒有任何錄影來源時回報零段()
    {
        var finalizer = new PlaybackRecordingFinalizer();

        Assert.Equal(0, await finalizer.FinalizeAsync(null, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task 來源被取消時回報零段而不是讓例外冒出來()
    {
        // 收檔只是「讓最新的一段早點出現」：來源取消／逾時不該讓回放視窗崩掉。
        var finalizer = new PlaybackRecordingFinalizer(
            (_, _) => Task.FromCanceled<int>(new CancellationToken(canceled: true)));

        Assert.Equal(0, await finalizer.FinalizeAsync(null, TimeSpan.FromSeconds(1)));
    }
}
