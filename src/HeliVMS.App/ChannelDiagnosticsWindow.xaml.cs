using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HeliVMS.Media;
using HeliVMS.Recording;

namespace HeliVMS.App;

/// <summary>
/// 單一頻道的連線診斷視窗：把「為什麼這台連不上／畫面不動」變成可回答的問題。
/// </summary>
/// <remarks>
/// <para>
/// 這個視窗存在的理由是 <see cref="RtspClient.LastDiagnostic"/>：ffmpeg 早就在寫
/// 最後幾行輸出（並遮蔽帳密），註解也寫明「供 UI 診斷顯示」，但沒有任何 UI 讀過它。
/// 沒有它，操作員唯一能做的事是回頭去猜位址、猜帳密、猜防火牆。</para>
/// <para>
/// 版面刻意是「結論先行」：最上面一行先回答能不能看，下面才是原始輸出。
/// 多數人只需要第一行就走了。</para>
/// </remarks>
public partial class ChannelDiagnosticsWindow : Window
{
    private readonly string _report;

    public ChannelDiagnosticsWindow(
        string channelName,
        int channelId,
        RtspState state,
        string url,
        DateTime lastFrameUtc,
        string diagnostic,
        string? reconnectReason,
        RecordingStallInfo? stall,
        ObservedBitrate? bitrate = null)
    {
        InitializeComponent();

        ApplyI18n();

        var sb = new StringBuilder();
        sb.AppendLine(string.Format(Localizer.T("Diag.Channel"), channelName, channelId));
        sb.AppendLine(string.Format(Localizer.T("Diag.State"), state));
        sb.AppendLine(string.Format(Localizer.T("Diag.Url"), url));
        sb.AppendLine(string.Format(
            Localizer.T("Diag.LastFrame"),
            lastFrameUtc == DateTime.MinValue
                ? Localizer.T("Diag.Never")
                : lastFrameUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        sb.Append(reconnectReason is null
            ? Localizer.T("Diag.ReasonNone")
            : string.Format(Localizer.T("Diag.Reason"), reconnectReason));

        // 實際碼率：回答「這台是不是把頻寬吃掉了」。磁碟比預期快滿時，
        // 操作員需要知道該降哪一台，而不必人工去翻區段檔大小。
        if (bitrate is { } b && b.SampleCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine(string.Format(
                Localizer.T("Diag.Bitrate"),
                FormatBps(b.ObservedBps),
                b.SampleCount,
                b.IsReliable ? Localizer.T("Diag.BitrateReliable") : Localizer.T("Diag.BitrateThin")));
        }

        if (stall is { } s)
        {
            sb.AppendLine();
            sb.Append(string.Format(Localizer.T("Diag.Stall"), s.ConsecutiveFailures));
        }

        DetailText.Text = sb.ToString();
        LogText.Text = string.IsNullOrWhiteSpace(diagnostic) ? Localizer.T("Diag.Empty") : diagnostic;

        // 結論先行。錄影停滯排在最嚴重：畫面完全正常，磁碟上卻沒有檔案。
        if (stall is not null)
        {
            VerdictText.Text = string.Format(Localizer.T("Diag.Stall"), stall.ConsecutiveFailures);
            VerdictBorder.BorderBrush = Brushes.IndianRed;
        }
        else if (state == RtspState.Streaming)
        {
            VerdictText.Text = state.ToString();
            VerdictBorder.BorderBrush = Brushes.SeaGreen;
        }
        else
        {
            VerdictText.Text = reconnectReason ?? state.ToString();
            VerdictBorder.BorderBrush = Brushes.DarkOrange;
        }

        _report = string.Join(
            Environment.NewLine,
            VerdictText.Text,
            string.Empty,
            DetailText.Text,
            string.Empty,
            LogText.Text);
    }

    /// <summary>
    /// 套用目前語系到診斷視窗的固定文字（M57 §14.7）。XAML 已無硬編中文，文字集中於此，
    /// 由 WpfI18nContractTests 的棘輪釘住三語齊備。
    /// </summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Diag.Title");
        CopyButton.Content = Localizer.T("Diag.Copy");
        CloseButton.Content = Localizer.T("Diag.Close");
        FooterText.Text = Localizer.T("Diag.LogHeader");

        if (LogText.Parent is GroupBox gb)
        {
            gb.Header = Localizer.T("Diag.LogHeader");
        }
    }

    /// <summary>碼率顯示成人類看得懂的單位；磁碟容量在現場是用 Mbps 講的。</summary>
    private static string FormatBps(long bps)
    {
        if (bps <= 0)
        {
            return "-";
        }

        return bps >= 1_000_000
            ? $"{bps / 1_000_000.0:0.0} Mbps"
            : $"{bps / 1000.0:0} kbps";
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_report);
            FooterText.Text = Localizer.T("Diag.Copied");
        }
        catch (Exception ex)
        {
            // 剪貼簿可能被其他行程鎖住；這是便利功能，不該讓診斷視窗因而關不掉。
            FooterText.Text = ex.Message;
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}