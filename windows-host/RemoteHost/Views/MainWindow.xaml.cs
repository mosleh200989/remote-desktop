using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteHost.Capture;
using RemoteHost.Config;

namespace RemoteHost.Views;

public partial class MainWindow : Window
{
    private readonly HostConfig _config;
    private readonly HostService _hostService;
    private readonly DispatcherTimer _countdownTimer;
    private ActiveIndicatorWindow? _indicator;
    private long _pairingExpiresAt;
    private DateTime _sessionStartedAt;
    private bool _suppressMonitorSelectionEvent;
    private FileTransferPresenter? _filePresenter;

    public MainWindow(HostConfig config)
    {
        InitializeComponent();
        _config = config;
        _hostService = new HostService(config);

        DeviceIdText.Text = FormatDeviceId(config.DeviceId);
        Title = $"Remote Desktop Host - {config.DeviceName}";

        PopulateMonitors();

        _hostService.OnConnectedChanged += connected => Dispatcher.Invoke(() => SetConnected(connected));
        _hostService.OnStatus += msg => Dispatcher.Invoke(() => AppendLog(msg));
        _hostService.OnPairingCode += (code, expiresAt) => Dispatcher.Invoke(() => ShowPairingCode(code, expiresAt));
        _hostService.OnPairingRequest += req => Dispatcher.Invoke(() => AddPendingRequest(req));
        _hostService.OnPairingRequestExpired += id => Dispatcher.Invoke(() => RemovePendingRequest(id));
        _hostService.OnSessionStarted += (sessionId, email) => Dispatcher.Invoke(() => ShowActiveSession(email));
        _hostService.OnSessionEnded += reason => Dispatcher.Invoke(() => HideActiveSession(reason));

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
        _countdownTimer.Start();

        Closing += async (_, _) => await _hostService.DisposeAsync();

        _ = _hostService.StartAsync();
    }

    private static string FormatDeviceId(string raw) =>
        string.Join(" ", Enumerable.Range(0, (raw.Length + 2) / 3).Select(i => raw.Substring(i * 3, Math.Min(3, raw.Length - i * 3))));

    private void PopulateMonitors()
    {
        var monitors = ScreenCapture.EnumerateMonitors();
        _suppressMonitorSelectionEvent = true;
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.DisplayMemberPath = "DeviceName";
        var selected = monitors.FirstOrDefault(m => m.AdapterIndex == _config.MonitorAdapterIndex && m.OutputIndex == _config.MonitorOutputIndex)
                       ?? monitors.FirstOrDefault();
        MonitorCombo.SelectedItem = selected;
        _suppressMonitorSelectionEvent = false;
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMonitorSelectionEvent) return;
        if (MonitorCombo.SelectedItem is MonitorInfo mon)
        {
            _config.MonitorAdapterIndex = mon.AdapterIndex;
            _config.MonitorOutputIndex = mon.OutputIndex;
            _config.Save();
        }
    }

    private void SetConnected(bool connected)
    {
        StatusDot.Fill = connected ? Brushes.Green : Brushes.Gray;
        StatusText.Text = connected ? "Connected to server" : "Disconnected";
    }

    private void AppendLog(string message)
    {
        LogList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (LogList.Items.Count > 300) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void ShowPairingCode(string code, long expiresAt)
    {
        PairingCodeText.Text = code;
        _pairingExpiresAt = expiresAt;
        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (_pairingExpiresAt == 0) return;
        var remaining = _pairingExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remaining <= 0)
        {
            PairingExpiryText.Text = "Expired - generate a new code";
            PairingCodeText.Text = "—";
            _pairingExpiresAt = 0;
        }
        else
        {
            PairingExpiryText.Text = $"Expires in {remaining / 1000}s";
        }
    }

    private void NewCodeButton_Click(object sender, RoutedEventArgs e) => _hostService.RequestNewPairingCode();

    private void AddPendingRequest(PendingRequest req)
    {
        NoPendingText.Visibility = Visibility.Collapsed;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), Tag = req.SessionRequestId };
        panel.Children.Add(new TextBlock { Text = req.ControllerName, VerticalAlignment = VerticalAlignment.Center, Width = 220 });
        var accept = new Button { Content = "Accept", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), Background = Brushes.SeaGreen, Foreground = Brushes.White };
        var reject = new Button { Content = "Reject", Padding = new Thickness(10, 4, 10, 4) };
        accept.Click += (_, _) => { _hostService.RespondToPairingRequest(req.SessionRequestId, true); RemovePendingRequest(req.SessionRequestId); };
        reject.Click += (_, _) => { _hostService.RespondToPairingRequest(req.SessionRequestId, false); RemovePendingRequest(req.SessionRequestId); };
        panel.Children.Add(accept);
        panel.Children.Add(reject);
        PendingRequestsPanel.Children.Add(panel);
    }

    private void RemovePendingRequest(string sessionRequestId)
    {
        var toRemove = PendingRequestsPanel.Children.OfType<StackPanel>().FirstOrDefault(p => (string?)p.Tag == sessionRequestId);
        if (toRemove != null) PendingRequestsPanel.Children.Remove(toRemove);
        if (!PendingRequestsPanel.Children.OfType<StackPanel>().Any()) NoPendingText.Visibility = Visibility.Visible;
    }

    private void ShowActiveSession(string controllerName)
    {
        _sessionStartedAt = DateTime.Now;
        ActiveSessionBorder.Visibility = Visibility.Visible;
        ActiveSessionText.Text = $"Connected controller: {controllerName}";

        _indicator ??= CreateIndicator();
        _indicator.SetControllerName(controllerName);
        _indicator.Show();

        FileTransfersPanel.Children.Clear();
        _filePresenter = new FileTransferPresenter(_hostService, FileTransfersPanel);
        FilesBorder.Visibility = Visibility.Visible;
    }

    private void SendFileButton_Click(object sender, RoutedEventArgs e) => _filePresenter?.PickAndSendFile();

    private void ConnectOutButton_Click(object sender, RoutedEventArgs e)
    {
        var connectWindow = new ConnectWindow(_config) { Owner = this };
        connectWindow.Show();
    }

    private ActiveIndicatorWindow CreateIndicator()
    {
        var win = new ActiveIndicatorWindow();
        win.EndRequested += () => _hostService.EndActiveSession();
        return win;
    }

    private void HideActiveSession(string reason)
    {
        ActiveSessionBorder.Visibility = Visibility.Collapsed;
        FilesBorder.Visibility = Visibility.Collapsed;
        _filePresenter = null;
        _indicator?.Hide();
        AppendLog($"Session ended: {reason}");
    }

    private void EndSessionButton_Click(object sender, RoutedEventArgs e) => _hostService.EndActiveSession();
}
