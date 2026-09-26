using System;

namespace RemoteHost.Rtc;

/// <summary>
/// Common shape both HostService and ControllerService expose for file
/// transfer, so a single UI presenter (FileTransferPresenter) can drive
/// either one without caring which role this session is playing.
/// </summary>
public interface IFileTransferSession
{
    event Action<IncomingFileOffer>? OnIncomingFileOffer;
    event Action<string, long, long>? OnFileProgress; // id, bytesDone, total
    event Action<string, string>? OnFileReceiveComplete; // id, savedPath
    event Action<string>? OnFileSendComplete; // id
    event Action<string, bool>? OnFileCancelled; // id, byRemote

    string? OfferFile(string filePath);
    void AcceptFileOffer(string id, string savePath, long size);
    void RejectFileOffer(string id);
    void CancelFileTransfer(string id);
}
