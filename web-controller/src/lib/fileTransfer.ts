// File transfer protocol, run over its own "files" WebRTC data channel,
// separate from the "control" channel so large transfers never delay mouse/
// keyboard input. Peer-to-peer only - the signaling server never sees file
// contents or names.
//
// Wire format: small JSON text messages for control ({t:"offer"|"accept"|
// "reject"|"cancel"}), followed by raw binary ArrayBuffer chunks for the
// accepted file's bytes, in order (the channel is ordered+reliable). One
// transfer at a time per direction to keep v1 simple - like AnyDesk's file
// manager, additional offers should be queued by the caller instead.

export const MAX_FILE_SIZE = 2 * 1024 * 1024 * 1024; // 2GB soft cap for v1
const CHUNK_SIZE = 16 * 1024;
const BUFFERED_AMOUNT_HIGH = 4 * 1024 * 1024;

export interface IncomingFileOffer {
  id: string;
  name: string;
  size: number;
}

export type TransferDirection = "send" | "receive";

interface IncomingState {
  id: string;
  name: string;
  size: number;
  chunks: Uint8Array[];
  received: number;
}

interface OutgoingItem {
  id: string;
  file: File;
}

export class FileTransferChannel {
  private dc: RTCDataChannel;
  private incoming: IncomingState | null = null;
  private outgoingQueue: OutgoingItem[] = [];

  onIncomingOffer?: (offer: IncomingFileOffer) => void;
  onProgress?: (id: string, bytesDone: number, total: number, direction: TransferDirection) => void;
  onComplete?: (id: string, direction: TransferDirection, blob?: Blob, name?: string) => void;
  onCancelled?: (id: string, byRemote: boolean) => void;

  constructor(dc: RTCDataChannel) {
    this.dc = dc;
    dc.binaryType = "arraybuffer";
    dc.bufferedAmountLowThreshold = BUFFERED_AMOUNT_HIGH / 2;
    dc.onmessage = (ev) => this.handleMessage(ev.data);
  }

  /** Announce a file to the other side; actual bytes only flow after they accept. */
  offerFile(file: File): string {
    const id = crypto.randomUUID();
    this.outgoingQueue.push({ id, file });
    this.dc.send(JSON.stringify({ t: "offer", id, name: file.name, size: file.size }));
    return id;
  }

  respondToOffer(id: string, accept: boolean) {
    this.dc.send(JSON.stringify({ t: accept ? "accept" : "reject", id }));
    if (!accept && this.incoming?.id === id) this.incoming = null;
  }

  cancel(id: string) {
    this.dc.send(JSON.stringify({ t: "cancel", id }));
    this.outgoingQueue = this.outgoingQueue.filter((q) => q.id !== id);
    if (this.incoming?.id === id) this.incoming = null;
  }

  private handleMessage(data: string | ArrayBuffer) {
    if (typeof data === "string") {
      const msg = JSON.parse(data);
      switch (msg.t) {
        case "offer":
          this.incoming = { id: msg.id, name: msg.name, size: msg.size, chunks: [], received: 0 };
          this.onIncomingOffer?.({ id: msg.id, name: msg.name, size: msg.size });
          break;
        case "accept":
          void this.sendQueued(msg.id);
          break;
        case "reject":
          this.outgoingQueue = this.outgoingQueue.filter((q) => q.id !== msg.id);
          this.onCancelled?.(msg.id, true);
          break;
        case "cancel":
          if (this.incoming?.id === msg.id) this.incoming = null;
          this.outgoingQueue = this.outgoingQueue.filter((q) => q.id !== msg.id);
          this.onCancelled?.(msg.id, true);
          break;
      }
      return;
    }

    if (!this.incoming) return;
    const chunk = new Uint8Array(data);
    this.incoming.chunks.push(chunk);
    this.incoming.received += chunk.byteLength;
    this.onProgress?.(this.incoming.id, this.incoming.received, this.incoming.size, "receive");
    if (this.incoming.received >= this.incoming.size) {
      const blob = new Blob(this.incoming.chunks as BlobPart[]);
      this.onComplete?.(this.incoming.id, "receive", blob, this.incoming.name);
      this.incoming = null;
    }
  }

  private async sendQueued(id: string) {
    const item = this.outgoingQueue.find((q) => q.id === id);
    if (!item) return;
    const buf = await item.file.arrayBuffer();
    let offset = 0;
    while (offset < buf.byteLength) {
      if (this.dc.readyState !== "open") return; // connection dropped mid-transfer
      if (this.dc.bufferedAmount > BUFFERED_AMOUNT_HIGH) {
        await new Promise<void>((resolve) => {
          this.dc.addEventListener("bufferedamountlow", () => resolve(), { once: true });
        });
      }
      const end = Math.min(offset + CHUNK_SIZE, buf.byteLength);
      this.dc.send(buf.slice(offset, end));
      offset = end;
      this.onProgress?.(id, offset, buf.byteLength, "send");
    }
    this.onComplete?.(id, "send");
    this.outgoingQueue = this.outgoingQueue.filter((q) => q.id !== id);
  }
}
