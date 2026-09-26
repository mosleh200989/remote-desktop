using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace RemoteHost.Rtc;

public sealed record IncomingFileOffer(string Id, string Name, long Size);

/// <summary>
/// File transfer over its own "files" WebRTC data channel - same wire
/// protocol as the web controller's FileTransferChannel (small JSON control
/// messages, then raw binary chunks for an accepted transfer). Works
/// identically whether this side is the host or the controller, since the
/// protocol is symmetric. One transfer at a time per direction.
/// </summary>
public sealed class FileTransferChannel
{
    private const int ChunkSize = 16 * 1024;
    private const ulong BufferedHigh = 4 * 1024 * 1024;

    private readonly RTCDataChannel _dc;
    private IncomingTransfer? _incoming;
    private readonly Dictionary<string, string> _outgoingPaths = new();

    public event Action<IncomingFileOffer>? OnIncomingOffer;
    public event Action<string, long, long>? OnProgress; // id, bytesDone, total
    public event Action<string, string>? OnReceiveComplete; // id, savedPath
    public event Action<string>? OnSendComplete; // id
    public event Action<string, bool>? OnCancelled; // id, byRemote

    public FileTransferChannel(RTCDataChannel dc)
    {
        _dc = dc;
        _dc.onmessage += OnMessage;
    }

    public string OfferFile(string filePath)
    {
        var id = Guid.NewGuid().ToString("N");
        var info = new FileInfo(filePath);
        _outgoingPaths[id] = filePath;
        SendJson(new { t = "offer", id, name = info.Name, size = info.Length });
        return id;
    }

    /// <summary>Call after the user picked a destination path for an incoming offer.</summary>
    public void AcceptOffer(string id, string savePath, long size)
    {
        _incoming = new IncomingTransfer(id, savePath, size);
        SendJson(new { t = "accept", id });
    }

    public void RejectOffer(string id) => SendJson(new { t = "reject", id });

    public void Cancel(string id)
    {
        SendJson(new { t = "cancel", id });
        _outgoingPaths.Remove(id);
        if (_incoming?.Id == id)
        {
            _incoming.Dispose();
            _incoming = null;
        }
    }

    private void OnMessage(RTCDataChannel dc, DataChannelPayloadProtocols proto, byte[] data)
    {
        if (proto == DataChannelPayloadProtocols.WebRTC_String)
        {
            HandleControlMessage(data);
            return;
        }

        if (_incoming == null) return;
        _incoming.Stream.Write(data, 0, data.Length);
        _incoming.Received += data.Length;
        OnProgress?.Invoke(_incoming.Id, _incoming.Received, _incoming.Size);
        if (_incoming.Received >= _incoming.Size)
        {
            _incoming.Stream.Dispose();
            OnReceiveComplete?.Invoke(_incoming.Id, _incoming.SavePath);
            _incoming = null;
        }
    }

    private void HandleControlMessage(byte[] data)
    {
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(data));
        var root = doc.RootElement;
        var t = root.GetProperty("t").GetString();
        var id = root.GetProperty("id").GetString()!;
        switch (t)
        {
            case "offer":
                OnIncomingOffer?.Invoke(new IncomingFileOffer(id, root.GetProperty("name").GetString()!, root.GetProperty("size").GetInt64()));
                break;
            case "accept":
                _ = SendQueuedAsync(id);
                break;
            case "reject":
                _outgoingPaths.Remove(id);
                OnCancelled?.Invoke(id, true);
                break;
            case "cancel":
                if (_incoming?.Id == id)
                {
                    _incoming.Dispose();
                    _incoming = null;
                }
                _outgoingPaths.Remove(id);
                OnCancelled?.Invoke(id, true);
                break;
        }
    }

    private void SendJson(object o) => _dc.send(JsonSerializer.Serialize(o));

    private async Task SendQueuedAsync(string id)
    {
        if (!_outgoingPaths.TryGetValue(id, out var path)) return;
        var size = new FileInfo(path).Length;
        await using var fs = File.OpenRead(path);
        var buffer = new byte[ChunkSize];
        long sent = 0;
        int read;
        while ((read = await fs.ReadAsync(buffer)) > 0)
        {
            while (_dc.bufferedAmount > BufferedHigh)
            {
                await Task.Delay(10);
            }
            _dc.send(buffer, 0, read);
            sent += read;
            OnProgress?.Invoke(id, sent, size);
        }
        OnSendComplete?.Invoke(id);
        _outgoingPaths.Remove(id);
    }

    private sealed class IncomingTransfer : IDisposable
    {
        public string Id { get; }
        public string SavePath { get; }
        public long Size { get; }
        public long Received { get; set; }
        public FileStream Stream { get; }

        public IncomingTransfer(string id, string savePath, long size)
        {
            Id = id;
            SavePath = savePath;
            Size = size;
            Stream = new FileStream(savePath, FileMode.Create, FileAccess.Write);
        }

        public void Dispose()
        {
            Stream.Dispose();
        }
    }
}
