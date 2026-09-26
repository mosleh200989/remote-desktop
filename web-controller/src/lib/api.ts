const HTTP_BASE = import.meta.env.VITE_SIGNALING_HTTP_URL ?? "http://localhost:8443";
export const WS_BASE = import.meta.env.VITE_SIGNALING_WS_URL ?? "ws://localhost:8443/ws";

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(HTTP_BASE + path, {
    ...init,
    headers: {
      "content-type": "application/json",
      ...(init?.headers ?? {}),
    },
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.error ?? `Request failed (${res.status})`);
  return body as T;
}

export function authHeader(token: string) {
  return { authorization: `Bearer ${token}` };
}

export const api = {
  register: (email: string, password: string) =>
    request<{ token: string; email: string }>("/api/auth/register", {
      method: "POST",
      body: JSON.stringify({ email, password }),
    }),
  login: (email: string, password: string) =>
    request<{ token: string; email: string }>("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ email, password }),
    }),
};
