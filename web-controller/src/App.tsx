import { useState } from "react";
import { AuthForm } from "./components/AuthForm";
import { PairForm } from "./components/PairForm";
import type { PairedSession } from "./components/PairForm";
import { Viewer } from "./components/Viewer";

const TOKEN_KEY = "rd_token";

export default function App() {
  const [token, setToken] = useState<string | null>(() => localStorage.getItem(TOKEN_KEY));
  const [session, setSession] = useState<PairedSession | null>(null);

  function onAuthed(t: string) {
    localStorage.setItem(TOKEN_KEY, t);
    setToken(t);
  }

  function onLogout() {
    localStorage.removeItem(TOKEN_KEY);
    setToken(null);
  }

  function onEnd() {
    session?.signaling.close();
    setSession(null);
  }

  if (!token) return <AuthForm onAuthed={onAuthed} />;
  if (!session) return <PairForm token={token} onPaired={setSession} onLogout={onLogout} />;
  return <Viewer session={session} onEnd={onEnd} />;
}
