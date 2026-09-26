/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_SIGNALING_HTTP_URL: string;
  readonly VITE_SIGNALING_WS_URL: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
