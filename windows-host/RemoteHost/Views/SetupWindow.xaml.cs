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
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;

        if (serverUrl.Length == 0 || email.Length == 0 || password.Length == 0)
        {
            ShowError("Please fill in the server URL, email, and password.");
            return;
        }

        SubmitButton.IsEnabled = false;
        BusyText.Visibility = Visibility.Visible;
        try
        {
            var api = new ApiClient(serverUrl);
            var auth = RegisterCheckBox.IsChecked == true
                ? await api.RegisterAsync(email, password)
                : await api.LoginAsync(email, password);

            var device = await api.RegisterDeviceAsync(auth.Token, deviceName);

            var wsUrl = ToWebSocketUrl(serverUrl);
            var config = new HostConfig
            {
                ServerHttpUrl = serverUrl,
                ServerWsUrl = wsUrl,
                DeviceId = device.DeviceId,
                HostToken = device.HostToken,
                DeviceName = device.Name,
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
