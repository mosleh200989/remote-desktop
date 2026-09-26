using System;
using System.Windows;
using RemoteHost.Config;

namespace RemoteHost.Views;

public partial class ConnectWindow : Window
{
    private readonly HostConfig _config;
    private ControllerService? _controller;

    public ConnectWindow(HostConfig config)
    {
        InitializeComponent();
        _config = config;
        DisplayNameBox.Text = config.ControllerDisplayName;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var displayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? Environment.MachineName : DisplayNameBox.Text.Trim();
        var deviceId = DeviceIdBox.Text.Replace(" ", "").Trim();
        var code = CodeBox.Text.Trim();

        if (deviceId.Length == 0 || code.Length == 0)
        {
            ShowError("Please enter both the device ID and pairing code.");
            return;
        }

        ConnectButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "Connecting...";

        _controller = new ControllerService();
        _controller.OnStatus += s => Dispatcher.Invoke(() => StatusText.Text = s);
        _controller.OnPairingPending += () => Dispatcher.Invoke(() => StatusText.Text = "Waiting for the other computer to approve...");
        _controller.OnRejected += reason => Dispatcher.Invoke(() =>
        {
            ShowError(DescribeRejection(reason));
            ConnectButton.IsEnabled = true;
            StatusText.Visibility = Visibility.Collapsed;
        });
        _controller.OnSessionStarted += () => Dispatcher.Invoke(() =>
        {
            var view = new RemoteViewWindow(_controller!, deviceId);
            view.Show();
            Close();
        });

        try
        {
            var wsUrl = ToWebSocketUrl(_config.ServerHttpUrl);
            await _controller.ConnectAndPairAsync(wsUrl, displayName, deviceId, code);
        }
        catch (Exception ex)
        {
            ShowError($"Could not connect: {ex.Message}");
            ConnectButton.IsEnabled = true;
            StatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string DescribeRejection(string reason) => reason switch
    {
        "host_offline" => "That device isn't online right now.",
        "device_busy" => "That device already has an active controller session.",
        "not_found" => "That code doesn't match this device. Double-check both and try again.",
        "already_used" => "That code has already been used. Ask them to generate a new one.",
        "expired" => "That code has expired. Ask them to generate a new one.",
        "locked_out" => "Too many attempts for this device. Please wait a few minutes.",
        "rejected_by_host" => "They rejected the connection request.",
        "timed_out" => "They didn't respond in time.",
        _ => $"Connection failed ({reason}).",
    };

    private static string ToWebSocketUrl(string httpUrl)
    {
        var uri = new Uri(httpUrl);
        var scheme = uri.Scheme == "https" ? "wss" : "ws";
        return $"{scheme}://{uri.Authority}/ws";
    }
}
