using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RemoteHost.Rtc;

namespace RemoteHost.Views;

/// <summary>
/// Drives a simple file-transfer list UI (offer/accept/reject/progress)
/// against any IFileTransferSession - works the same whether the session is
/// the host side or the controller side, since both expose that interface.
/// </summary>
public sealed class FileTransferPresenter
{
    private readonly IFileTransferSession _session;
    private readonly StackPanel _panel;
    private readonly Dictionary<string, Row> _rows = new();

    public FileTransferPresenter(IFileTransferSession session, StackPanel panel)
    {
        _session = session;
        _panel = panel;
        session.OnIncomingFileOffer += o => _panel.Dispatcher.Invoke(() => AddIncoming(o));
        session.OnFileProgress += (id, done, total) => _panel.Dispatcher.Invoke(() => UpdateProgress(id, done, total));
        session.OnFileReceiveComplete += (id, path) => _panel.Dispatcher.Invoke(() => MarkDone(id, $"Saved to {path}"));
        session.OnFileSendComplete += id => _panel.Dispatcher.Invoke(() => MarkDone(id, "Sent"));
        session.OnFileCancelled += (id, remote) =>
            _panel.Dispatcher.Invoke(() => MarkDone(id, remote ? "Declined by the other side" : "Cancelled"));
    }

    public void PickAndSendFile()
    {
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog() != true) return;
        var id = _session.OfferFile(dlg.FileName);
        if (id is null) return;
        AddRow(id, Path.GetFileName(dlg.FileName), new FileInfo(dlg.FileName).Length, isIncoming: false);
    }

    private void AddIncoming(IncomingFileOffer offer)
    {
        var row = AddRow(offer.Id, offer.Name, offer.Size, isIncoming: true);
        row.AcceptButton!.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog { FileName = offer.Name };
            if (dlg.ShowDialog() == true)
            {
                _session.AcceptFileOffer(offer.Id, dlg.FileName, offer.Size);
                row.ActionsPanel!.Visibility = Visibility.Collapsed;
                row.ProgressBar!.Visibility = Visibility.Visible;
            }
            else
            {
                _session.RejectFileOffer(offer.Id);
                MarkDone(offer.Id, "Declined");
            }
        };
        row.RejectButton!.Click += (_, _) =>
        {
            _session.RejectFileOffer(offer.Id);
            MarkDone(offer.Id, "Declined");
        };
    }

    private Row AddRow(string id, string name, long size, bool isIncoming)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var arrow = isIncoming ? "↓" : "↑";
        container.Children.Add(new TextBlock
        {
            Text = $"{arrow} {name} ({FormatBytes(size)})",
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new Row { Container = container };

        if (isIncoming)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            row.AcceptButton = new Button { Content = "Accept", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
            row.RejectButton = new Button { Content = "Decline", Padding = new Thickness(8, 2, 8, 2) };
            actions.Children.Add(row.AcceptButton);
            actions.Children.Add(row.RejectButton);
            container.Children.Add(actions);
            row.ActionsPanel = actions;
        }

        row.ProgressBar = new ProgressBar { Height = 8, Margin = new Thickness(0, 4, 0, 0), Maximum = size, Visibility = Visibility.Collapsed };
        container.Children.Add(row.ProgressBar);

        row.StatusText = new TextBlock { Foreground = System.Windows.Media.Brushes.Gray, Visibility = Visibility.Collapsed };
        container.Children.Add(row.StatusText);

        _rows[id] = row;
        _panel.Children.Add(container);
        if (!isIncoming) row.ProgressBar.Visibility = Visibility.Visible;
        return row;
    }

    private void UpdateProgress(string id, long done, long total)
    {
        if (!_rows.TryGetValue(id, out var row) || row.ProgressBar is null) return;
        row.ProgressBar.Maximum = total;
        row.ProgressBar.Value = done;
        row.ProgressBar.Visibility = Visibility.Visible;
    }

    private void MarkDone(string id, string status)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        if (row.ProgressBar != null) row.ProgressBar.Visibility = Visibility.Collapsed;
        if (row.ActionsPanel != null) row.ActionsPanel.Visibility = Visibility.Collapsed;
        if (row.StatusText != null)
        {
            row.StatusText.Text = status;
            row.StatusText.Visibility = Visibility.Visible;
        }
    }

    private static string FormatBytes(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024.0:F1} KB";
        if (n < 1024 * 1024 * 1024) return $"{n / (1024.0 * 1024):F1} MB";
        return $"{n / (1024.0 * 1024 * 1024):F2} GB";
    }

    private sealed class Row
    {
        public StackPanel Container = null!;
        public StackPanel? ActionsPanel;
        public Button? AcceptButton;
        public Button? RejectButton;
        public ProgressBar? ProgressBar;
        public TextBlock? StatusText;
    }
}
