using System;
using System.Collections.Generic;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteHost.Capture;

public sealed record MonitorInfo(int AdapterIndex, int OutputIndex, string DeviceName, int Left, int Top, int Width, int Height);

public sealed class CapturedFrame
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>
/// Captures a single monitor using the DXGI Desktop Duplication API - the
/// same low-latency, GPU-accelerated capture path Windows itself uses for
/// screen recording / remote assistance. Runs its own capture thread and
/// recovers automatically from resolution changes, monitor topology changes,
/// and the lock screen (DXGI_ERROR_ACCESS_LOST) by re-initializing.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly int _adapterIndex;
    private readonly int _outputIndex;
    private Thread? _thread;
    private volatile bool _running;

    public event Action<CapturedFrame>? OnFrame;
    public event Action<string>? OnStatus;

    public ScreenCapture(int adapterIndex, int outputIndex)
    {
        _adapterIndex = adapterIndex;
        _outputIndex = outputIndex;
    }

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var result = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success && adapter != null; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output != null; o++)
            {
                var desc = output.Description;
                result.Add(new MonitorInfo(
                    (int)a, (int)o, desc.DeviceName,
                    desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
                    desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                    desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top));
                output.Dispose();
            }
            adapter.Dispose();
        }
        return result;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenCapture" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
    }

    private void CaptureLoop()
    {
        while (_running)
        {
            try
            {
                RunDuplicationSession();
            }
            catch (Exception ex)
            {
                OnStatus?.Invoke($"Capture error, retrying: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    private void RunDuplicationSession()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        using var adapter = GetAdapter(factory, _adapterIndex);
        using var output = GetOutput(adapter, _outputIndex);
        using var output1 = output.QueryInterface<IDXGIOutput1>();

        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            null!, out ID3D11Device? device, out _, out ID3D11DeviceContext? context)
            .CheckError();
        using var d3dDevice = device!;
        using var d3dContext = context!;

        using var duplication = output1.DuplicateOutput(d3dDevice);

        var outputDesc = output.Description;
        int width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
        int height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;

        var stagingDesc = new Texture2DDescription
        {
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            Format = Format.B8G8R8A8_UNorm,
            Width = (uint)width,
            Height = (uint)height,
            MiscFlags = ResourceOptionFlags.None,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
        };
        using var staging = d3dDevice.CreateTexture2D(stagingDesc);

        OnStatus?.Invoke($"Capturing monitor {outputDesc.DeviceName} ({width}x{height})");

        var frameBuffer = new byte[width * height * 4];

        while (_running)
        {
            var result = duplication.AcquireNextFrame(500, out var _, out IDXGIResource? resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                continue; // no new frame yet - screen hasn't changed
            }
            if (result.Failure)
            {
                resource?.Dispose();
                result.CheckError(); // throws -> caught by CaptureLoop, triggers re-init
                return;
            }

            using (resource)
            using (var texture = resource!.QueryInterface<ID3D11Texture2D>())
            {
                d3dContext.CopyResource(staging, texture);
            }
            duplication.ReleaseFrame();

            var mapped = d3dContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                int rowBytes = width * 4;
                fixed (byte* dstBase = frameBuffer)
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + y * mapped.RowPitch, dstBase + y * rowBytes, rowBytes, rowBytes);
                    }
                }
            }
            d3dContext.Unmap(staging, 0);

            OnFrame?.Invoke(new CapturedFrame { Bgra = frameBuffer, Width = width, Height = height });
        }
    }

    private static IDXGIAdapter1 GetAdapter(IDXGIFactory1 factory, int index)
    {
        if (!factory.EnumAdapters1((uint)index, out IDXGIAdapter1? adapter).Success || adapter == null)
            throw new InvalidOperationException($"No display adapter at index {index}");
        return adapter;
    }

    private static IDXGIOutput GetOutput(IDXGIAdapter1 adapter, int index)
    {
        if (!adapter.EnumOutputs((uint)index, out IDXGIOutput? output).Success || output == null)
            throw new InvalidOperationException($"No monitor output at index {index}");
        return output;
    }

    public void Dispose() => Stop();
}
