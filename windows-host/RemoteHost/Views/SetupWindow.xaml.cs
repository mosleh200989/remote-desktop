using System;
using System.Windows;
using RemoteHost.Config;

namespace RemoteHost.Views;

public partial class SetupWindow : Window
{
    public HostConfig? ResultConfig { get; private set; }

    public SetupWindow()
    {
        InitializeComponent();
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var serverUrl = ServerUrlBox.Text.Trim().TrimEnd('/');
        var deviceName = string.IsNullOrWhiteSpace(DeviceNameBox.Text) ? Environment.MachineName : DeviceNameBox.Text.Trim();
        var displayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? Environment.MachineName : DisplayNameBox.Text.Trim();

        if (serverUrl.Length == 0)
        {
            ShowError("Please fill in the server URL.");
            return;
        }

        SubmitButton.IsEnabled = false;
        BusyText.Visibility = Visibility.Visible;
        try
        {
            var api = new ApiClient(serverUrl);
            var device = await api.RegisterDeviceAsync(deviceName);

            var wsUrl = ToWebSocketUrl(serverUrl);
            var config = new HostConfig
            {
                ServerHttpUrl = serverUrl,
                ServerWsUrl = wsUrl,
                DeviceId = device.DeviceId,
                HostToken = device.HostToken,
                DeviceName = device.Name,
                ControllerDisplayName = displayName,
            };
            config.Save();
            ResultConfig = config;
            DialogResult = true;
            Close();
        }
        catch (ApiException apiEx)
        {
            ShowError(apiEx.Message);
        }
        catch (Exception ex)
        {
            ShowError($"Could not reach the server: {ex.Message}");
        }
        finally
        {
            SubmitButton.IsEnabled = true;
            BusyText.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string ToWebSocketUrl(string httpUrl)
    {
        var uri = new Uri(httpUrl);
        var scheme = uri.Scheme == "https" ? "wss" : "ws";
        return $"{scheme}://{uri.Authority}/ws";
    }
}
