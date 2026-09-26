using System;
using System.Windows;

namespace RemoteHost.Views;

public partial class ActiveIndicatorWindow : Window
{
    public event Action? EndRequested;

    public ActiveIndicatorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Width = area.Width;
            Left = area.Left;
            Top = area.Top;
        };
    }

    public void SetControllerEmail(string email)
    {
        MessageText.Text = $"Remote control is active - connected: {email}";
    }

    private void EndButton_Click(object sender, RoutedEventArgs e) => EndRequested?.Invoke();
}
