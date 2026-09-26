import { useEffect, useRef, useState } from "react";
import { FileTransferChannel, MAX_FILE_SIZE } from "../lib/fileTransfer";
import type { TransferDirection } from "../lib/fileTransfer";

interface Transfer {
  id: string;
  name: string;
  size: number;
  direction: TransferDirection;
  status: "awaiting-accept" | "transferring" | "done" | "rejected" | "cancelled";
  bytesDone: number;
}

export function FileTransferPanel({ channel, onClose }: { channel: FileTransferChannel | null; onClose: () => void }) {
  const [transfers, setTransfers] = useState<Record<string, Transfer>>({});
  const fileInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!channel) return;
    channel.onIncomingOffer = (offer) => {
      setTransfers((prev) => ({
        ...prev,
        [offer.id]: {
          id: offer.id,
          name: offer.name,
          size: offer.size,
          direction: "receive",
          status: "awaiting-accept",
          bytesDone: 0,
        },
      }));
    };
    channel.onProgress = (id, bytesDone, size, direction) => {
      setTransfers((prev) => ({
        ...prev,
        [id]: { ...(prev[id] ?? { id, name: "file", size, direction, status: "transferring", bytesDone: 0 }), status: "transferring", bytesDone },
      }));
    };
    channel.onComplete = (id, direction, blob, name) => {
      setTransfers((prev) => ({ ...prev, [id]: { ...prev[id], status: "done", bytesDone: prev[id]?.size ?? 0 } }));
      if (direction === "receive" && blob) {
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = name ?? "downloaded-file";
        a.click();
        setTimeout(() => URL.revokeObjectURL(url), 10_000);
      }
    };
    channel.onCancelled = (id) => {
      setTransfers((prev) => (prev[id] ? { ...prev, [id]: { ...prev[id], status: "rejected" } } : prev));
    };
  }, [channel]);

  function pickFile() {
    fileInputRef.current?.click();
  }

  function onFileSelected(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file || !channel) return;
    if (file.size > MAX_FILE_SIZE) {
      alert(`That file is larger than the ${Math.round(MAX_FILE_SIZE / (1024 * 1024 * 1024))}GB limit for this version.`);
      return;
    }
    const id = channel.offerFile(file);
    setTransfers((prev) => ({
      ...prev,
      [id]: { id, name: file.name, size: file.size, direction: "send", status: "awaiting-accept", bytesDone: 0 },
    }));
  }

  function accept(id: string, ok: boolean) {
    channel?.respondToOffer(id, ok);
    setTransfers((prev) => ({
      ...prev,
      [id]: { ...prev[id], status: ok ? "transferring" : "rejected" },
    }));
  }

  function cancel(id: string) {
    channel?.cancel(id);
    setTransfers((prev) => ({ ...prev, [id]: { ...prev[id], status: "cancelled" } }));
  }

  const list = Object.values(transfers).sort((a, b) => (a.id < b.id ? 1 : -1));

  return (
    <div className="file-panel">
      <div className="file-panel-header">
        <strong>Files</strong>
        <button className="link" onClick={onClose}>
          Close
        </button>
      </div>
      <button onClick={pickFile} disabled={!channel}>
        Send a file
      </button>
      <input ref={fileInputRef} type="file" style={{ display: "none" }} onChange={onFileSelected} />
      {!channel && <p className="subtitle">Waiting for the connection to finish...</p>}
      <div className="file-list">
        {list.length === 0 && <p className="subtitle">No transfers yet.</p>}
        {list.map((t) => (
          <div key={t.id} className="file-row">
            <div className="file-row-name">
              {t.direction === "send" ? "↑" : "↓"} {t.name}
              <span className="file-row-size"> ({formatBytes(t.size)})</span>
            </div>
            {t.status === "awaiting-accept" && t.direction === "receive" && (
              <div className="file-row-actions">
                <button onClick={() => accept(t.id, true)}>Accept</button>
                <button onClick={() => accept(t.id, false)}>Decline</button>
              </div>
            )}
            {t.status === "awaiting-accept" && t.direction === "send" && (
              <div className="file-row-actions">
                <span className="subtitle">Waiting for them to accept...</span>
                <button onClick={() => cancel(t.id)}>Cancel</button>
              </div>
            )}
            {t.status === "transferring" && (
              <div className="file-row-progress">
                <div className="file-row-bar">
                  <div className="file-row-bar-fill" style={{ width: `${Math.round((t.bytesDone / t.size) * 100)}%` }} />
                </div>
                <button className="link" onClick={() => cancel(t.id)}>
                  Cancel
                </button>
              </div>
            )}
            {t.status === "done" && <span className="file-row-status">Done</span>}
            {t.status === "rejected" && <span className="file-row-status">Declined</span>}
            {t.status === "cancelled" && <span className="file-row-status">Cancelled</span>}
          </div>
        ))}
      </div>
    </div>
  );
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}
