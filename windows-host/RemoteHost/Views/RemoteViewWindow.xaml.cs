using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RemoteHost.Views;

public partial class RemoteViewWindow : Window
{
    private readonly ControllerService _controller;
    private readonly string _deviceId;
    private WriteableBitmap? _bitmap;
    private FileTransferPresenter? _filePresenter;
    private bool _filesOpen;

    public RemoteViewWindow(ControllerService controller, string deviceId)
    {
        InitializeComponent();
        _controller = controller;
        _deviceId = deviceId;
        Title = $"Remote Desktop - {deviceId}";

        _controller.OnConnectionStateChanged += state => Dispatcher.Invoke(() => SetConnectionState(state));
        _controller.OnDecodedFrame += (sample, w, h, stride, pf) => Dispatcher.Invoke(() => RenderFrame(sample, w, h, stride, pf));
        _controller.OnSessionEnded += reason => Dispatcher.Invoke(() =>
        {
            MessageBox.Show(this, $"The session ended ({reason}).", "Remote Desktop", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        });

        _filePresenter = new FileTransferPresenter(_controller, FileTransfersPanel);

        Closing += (_, _) => _controller.EndSession();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        TextInput += OnTextInput;
    }

    private void SetConnectionState(RTCPeerConnectionState state)
    {
        StatusDot.Fill = state == RTCPeerConnectionState.connected ? Brushes.LimeGreen : Brushes.Gray;
        StatusText.Text = state switch
        {
            RTCPeerConnectionState.connected => "Connected",
            RTCPeerConnectionState.connecting or RTCPeerConnectionState.@new => "Connecting...",
            RTCPeerConnectionState.disconnected => "Reconnecting...",
            RTCPeerConnectionState.failed => "Connection failed",
            RTCPeerConnectionState.closed => "Session ended",
            _ => state.ToString(),
        };
    }

    // ---- Video rendering ----

    private void RenderFrame(byte[] sample, int width, int height, int stride, VideoPixelFormatsEnum pixelFormat)
    {
        if (width <= 0 || height <= 0) return;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            RemoteImage.Source = _bitmap;
        }

        // The Bgra case keeps whatever row stride the decoder actually used;
        // every other case is converted into a tightly packed width*4 buffer.
        if (pixelFormat == VideoPixelFormatsEnum.Bgra)
        {
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), sample, stride > 0 ? stride : width * 4, 0);
            return;
        }

        int srcStride = stride > 0 ? stride : width * BytesPerPixel(pixelFormat);
        byte[] bgra = pixelFormat switch
        {
            VideoPixelFormatsEnum.Rgba => SwapRedBlue(sample, width, height, srcStride, 4),
            VideoPixelFormatsEnum.Rgb => Rgb24ToBgra(sample, width, height, srcStride),
            VideoPixelFormatsEnum.Bgr => Bgr24ToBgra(sample, width, height, srcStride),
            VideoPixelFormatsEnum.I420 => I420ToBgra(sample, width, height),
            _ => Array.Empty<byte>(),
        };
        if (bgra.Length == 0) return;

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
    }

    private static int BytesPerPixel(VideoPixelFormatsEnum pf) => pf switch
    {
        VideoPixelFormatsEnum.Rgba => 4,
        VideoPixelFormatsEnum.Rgb or VideoPixelFormatsEnum.Bgr => 3,
        _ => 1,
    };

    private static byte[] SwapRedBlue(byte[] src, int width, int height, int srcStride, int bpp)
    {
        var outBuf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * srcStride;
            int d = y * width * 4;
            for (int x = 0; x < width; x++, d += 4)
            {
                int s = rowStart + x * bpp;
                outBuf[d + 0] = src[s + 2];
                outBuf[d + 1] = src[s + 1];
                outBuf[d + 2] = src[s + 0];
                outBuf[d + 3] = bpp == 4 ? src[s + 3] : (byte)255;
            }
        }
        return outBuf;
    }

    private static byte[] Rgb24ToBgra(byte[] src, int width, int height, int srcStride)
    {
        var outBuf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * srcStride;
            int d = y * width * 4;
            for (int x = 0; x < width; x++, d += 4)
            {
                int s = rowStart + x * 3;
                outBuf[d + 0] = src[s + 2];
                outBuf[d + 1] = src[s + 1];
                outBuf[d + 2] = src[s + 0];
                outBuf[d + 3] = 255;
            }
        }
        return outBuf;
    }

    private static byte[] Bgr24ToBgra(byte[] src, int width, int height, int srcStride)
    {
        var outBuf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * srcStride;
            int d = y * width * 4;
            for (int x = 0; x < width; x++, d += 4)
            {
                int s = rowStart + x * 3;
                outBuf[d + 0] = src[s + 0];
                outBuf[d + 1] = src[s + 1];
                outBuf[d + 2] = src[s + 2];
                outBuf[d + 3] = 255;
            }
        }
        return outBuf;
    }

    private static byte[] I420ToBgra(byte[] i420, int width, int height)
    {
        var outBuf = new byte[width * height * 4];
        int frameSize = width * height;
        int uOffset = frameSize;
        int vOffset = frameSize + frameSize / 4;
        int uvWidth = width / 2;

        for (int y = 0; y < height; y++)
        {
            int uvRow = y / 2;
            for (int x = 0; x < width; x++)
            {
                int yIndex = y * width + x;
                int uvCol = x / 2;
                int uIndex = uOffset + uvRow * uvWidth + uvCol;
                int vIndex = vOffset + uvRow * uvWidth + uvCol;

                int Y = i420[yIndex];
                int U = i420[uIndex] - 128;
                int V = i420[vIndex] - 128;

                int r = Y + ((91881 * V) >> 16);
                int g = Y - ((22554 * U + 46802 * V) >> 16);
                int b = Y + ((116130 * U) >> 16);

                int o = yIndex * 4;
                outBuf[o + 0] = ClampByte(b);
                outBuf[o + 1] = ClampByte(g);
                outBuf[o + 2] = ClampByte(r);
                outBuf[o + 3] = 255;
            }
        }
        return outBuf;
    }

    private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // ---- Local input capture -> normalized control events ----

    private Point? ToNormalized(Point p)
    {
        if (_bitmap is null || RemoteImage.ActualWidth == 0 || RemoteImage.ActualHeight == 0) return null;
        double boxAspect = RemoteImage.ActualWidth / RemoteImage.ActualHeight;
        double imgAspect = (double)_bitmap.PixelWidth / _bitmap.PixelHeight;
        double contentW = RemoteImage.ActualWidth, contentH = RemoteImage.ActualHeight, offsetX = 0, offsetY = 0;
        if (imgAspect > boxAspect)
        {
            contentH = RemoteImage.ActualWidth / imgAspect;
            offsetY = (RemoteImage.ActualHeight - contentH) / 2;
        }
        else
        {
            contentW = RemoteImage.ActualHeight * imgAspect;
            offsetX = (RemoteImage.ActualWidth - contentW) / 2;
        }
        double x = (p.X - offsetX) / contentW;
        double y = (p.Y - offsetY) / contentH;
        if (x < 0 || x > 1 || y < 0 || y > 1) return null;
        return new Point(x, y);
    }

    private void RemoteImage_MouseMove(object sender, MouseEventArgs e)
    {
        var n = ToNormalized(e.GetPosition(RemoteImage));
        if (n is { } p) _controller.SendControl(new { t = "move", x = p.X, y = p.Y });
    }

    private void RemoteImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        RemoteImage.Focus();
        var n = ToNormalized(e.GetPosition(RemoteImage));
        if (n is { } p) _controller.SendControl(new { t = "down", x = p.X, y = p.Y, button = ButtonIndex(e.ChangedButton) });
    }

    private void RemoteImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var n = ToNormalized(e.GetPosition(RemoteImage));
        if (n is { } p) _controller.SendControl(new { t = "up", x = p.X, y = p.Y, button = ButtonIndex(e.ChangedButton) });
    }

    private void RemoteImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var n = ToNormalized(e.GetPosition(RemoteImage));
        if (n is { } p) _controller.SendControl(new { t = "wheel", x = p.X, y = p.Y, dx = 0, dy = -e.Delta });
    }

    private static int ButtonIndex(MouseButton b) => b switch
    {
        MouseButton.Right => 2,
        MouseButton.Middle => 1,
        _ => 0,
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) => ForwardNamedKey(e, isDown: true);
    private void OnPreviewKeyUp(object sender, KeyEventArgs e) => ForwardNamedKey(e, isDown: false);

    private void ForwardNamedKey(KeyEventArgs e, bool isDown)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = MapKeyName(key);
        if (name is null) return;
        _controller.SendControl(new { t = isDown ? "keydown" : "keyup", key = name });
        e.Handled = true;
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        _controller.SendControl(new { t = "text", text = e.Text });
        e.Handled = true;
    }

    private static string? MapKeyName(Key key) => key switch
    {
        Key.Enter or Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Tab => "Tab",
        Key.Escape => "Escape",
        Key.Delete => "Delete",
        Key.Up => "ArrowUp",
        Key.Down => "ArrowDown",
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.LeftCtrl or Key.RightCtrl => "Control",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LWin or Key.RWin => "Meta",
        Key.CapsLock => "CapsLock",
        >= Key.F1 and <= Key.F12 => "F" + (key - Key.F1 + 1),
        _ => null,
    };

    // ---- File transfer panel ----

    private void FilesButton_Click(object sender, RoutedEventArgs e)
    {
        _filesOpen = !_filesOpen;
        FilesPanelHost.Visibility = _filesOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseFilesButton_Click(object sender, RoutedEventArgs e)
    {
        _filesOpen = false;
        FilesPanelHost.Visibility = Visibility.Collapsed;
    }

    private void SendFileButton_Click(object sender, RoutedEventArgs e) => _filePresenter?.PickAndSendFile();

    private void EndSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.EndSession();
        Close();
    }
}
