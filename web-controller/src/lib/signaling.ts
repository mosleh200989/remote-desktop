import { WS_BASE } from "./api";

export type IceServerConfig = { urls: string; username?: string; credential?: string };

type Listener = (msg: any) => void;

/**
 * Thin wrapper around the signaling WebSocket. One instance per pairing
 * attempt / session - create a fresh one each time the user tries to pair.
 */
export class SignalingSocket {
  private ws: WebSocket;
  private listeners = new Set<Listener>();
  private openPromise: Promise<void>;
  private token: string;

  constructor(token: string) {
    this.token = token;
    this.ws = new WebSocket(WS_BASE);
    this.openPromise = new Promise((resolve, reject) => {
      this.ws.addEventListener("open", () => resolve(), { once: true });
      this.ws.addEventListener("error", () => reject(new Error("Could not reach signaling server")), {
        once: true,
      });
    });
    this.ws.addEventListener("message", (ev) => {
      let msg: any;
      try {
        msg = JSON.parse(ev.data);
      } catch {
        return;
      }
      for (const l of this.listeners) l(msg);
    });
  }

  on(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Resolves with the first message matching `pred`, or rejects on timeout/close. */
  waitFor(pred: (msg: any) => boolean, timeoutMs = 15000): Promise<any> {
    return new Promise((resolve, reject) => {
      const off = this.on((msg) => {
        if (pred(msg)) {
          off();
          clearTimeout(timer);
          resolve(msg);
        }
      });
      const timer = setTimeout(() => {
        off();
        reject(new Error("Timed out waiting for server response"));
      }, timeoutMs);
      this.ws.addEventListener(
        "close",
        () => {
          off();
          clearTimeout(timer);
          reject(new Error("Connection to signaling server closed"));
        },
        { once: true }
      );
    });
  }

  async send(msg: unknown) {
    await this.openPromise;
    this.ws.send(JSON.stringify(msg));
  }

  async authenticate(): Promise<{ iceServers: IceServerConfig[] }> {
    await this.send({ type: "controller:auth", token: this.token });
    const res = await this.waitFor(
      (m) => m.type === "controller:auth-ok" || m.type === "controller:auth-failed"
    );
    if (res.type === "controller:auth-failed") throw new Error("Your session expired, please log in again");
    return { iceServers: res.iceServers ?? [] };
  }

  close() {
    this.ws.close();
  }
}
