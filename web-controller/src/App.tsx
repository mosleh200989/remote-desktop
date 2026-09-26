import { useState } from "react";
import { PairForm } from "./components/PairForm";
import type { PairedSession } from "./components/PairForm";
import { Viewer } from "./components/Viewer";

export default function App() {
  const [session, setSession] = useState<PairedSession | null>(null);

  function onEnd() {
    session?.signaling.close();
    setSession(null);
  }

  if (!session) return <PairForm onPaired={setSession} />;
  return <Viewer session={session} onEnd={onEnd} />;
}
