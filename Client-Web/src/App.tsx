import { useEffect, useRef, useState } from "react";
import type { SignalingMessage, DisplayInfo } from "./SignalingMessage";
import "./App.css";

// Puerto del HttpServer (Swifter) embebido en el Server — sirve tanto estos
// estáticos como la ruta /signaling, mismo origen, sin problemas de CORS.
const SIGNALING_PORT = 47635;

/// Lee el arrangement real de monitores vía la Window Management API. Si el
/// browser no la soporta o el usuario rechaza el permiso, cae a un solo display
/// con la resolución de la ventana actual — mantiene el comportamiento de antes
/// en vez de romper.
async function readDisplayArrangement(): Promise<DisplayInfo[]> {
  if (window.getScreenDetails) {
    try {
      const details = await window.getScreenDetails();
      return details.screens.map((screen) => ({
        width: screen.width,
        height: screen.height,
        x: screen.left,
        y: screen.top,
        isPrimary: screen.isPrimary,
      }));
    } catch {
      // Permiso rechazado, o falló por otro motivo — cae al fallback de abajo.
    }
  }
  return [{ width: window.screen.width, height: window.screen.height, x: 0, y: 0, isPrimary: true }];
}

function App() {
  const [started, setStarted] = useState(false);
  const [status, setStatus] = useState("");
  const [displays, setDisplays] = useState<DisplayInfo[]>([]);
  const [streamsByIndex, setStreamsByIndex] = useState<Record<number, MediaStream>>({});

  const wsRef = useRef<WebSocket | null>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);

  useEffect(() => {
    return () => {
      pcRef.current?.close();
      pcRef.current = null;
      wsRef.current?.close();
    };
  }, []);

  // getScreenDetails() necesita un gesto de usuario para el prompt de permiso la
  // primera vez — por eso ya no se conecta solo al cargar (como antes), hace falta
  // este click.
  async function handleStart() {
    const arrangement = await readDisplayArrangement();
    setDisplays(arrangement);
    setStarted(true);
    connect(arrangement);
  }

  function connect(arrangement: DisplayInfo[]) {
    setStatus("Conectando…");
    const ws = new WebSocket(`ws://${window.location.hostname}:${SIGNALING_PORT}/signaling`);
    wsRef.current = ws;

    ws.onopen = () => {
      setStatus("Conectado al Server, esperando oferta…");
      sendMessage(ws, { type: "hello", displays: arrangement });
    };

    ws.onmessage = (event) => {
      void handleSignalingMessage(JSON.parse(event.data) as SignalingMessage);
    };

    ws.onclose = () => setStatus("Desconectado del Server.");
    ws.onerror = () => setStatus("Error de conexión con el Server.");
  }

  function sendMessage(ws: WebSocket, message: SignalingMessage) {
    ws.send(JSON.stringify(message));
  }

  function ensurePeerConnection(): RTCPeerConnection {
    if (pcRef.current) return pcRef.current;

    // Sin STUN/TURN: Mac y Windows están en la misma LAN, alcanza con candidatos host.
    const pc = new RTCPeerConnection({ iceServers: [] });

    // Un track por ghost display, con stream id "display-<índice>" (mismo orden que
    // el arrangement mandado en "hello") — así se sabe a qué monitor corresponde
    // cada track sin necesidad de un mensaje de señalización extra.
    pc.ontrack = (event) => {
      const streamId = event.streams[0]?.id ?? "";
      const match = /^display-(\d+)$/.exec(streamId);
      if (!match) return;
      const index = Number(match[1]);
      const stream = event.streams[0];
      setStreamsByIndex((prev) => ({ ...prev, [index]: stream }));
    };

    pc.onicecandidate = (event) => {
      if (event.candidate && wsRef.current) {
        sendMessage(wsRef.current, {
          type: "ice",
          sdp: event.candidate.candidate,
          sdpMLineIndex: event.candidate.sdpMLineIndex ?? undefined,
          sdpMid: event.candidate.sdpMid ?? undefined,
        });
      }
    };

    pc.oniceconnectionstatechange = () => {
      setStatus(`Estado ICE: ${pc.iceConnectionState}`);
    };

    pcRef.current = pc;
    return pc;
  }

  async function handleSignalingMessage(message: SignalingMessage) {
    const ws = wsRef.current;
    if (!ws) return;

    if (message.type === "offer" && message.sdp) {
      const pc = ensurePeerConnection();
      await pc.setRemoteDescription({ type: "offer", sdp: message.sdp });
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      sendMessage(ws, { type: "answer", sdp: answer.sdp });
    } else if (message.type === "ice" && message.sdp) {
      const pc = pcRef.current;
      if (!pc) return;
      await pc.addIceCandidate({
        candidate: message.sdp,
        sdpMLineIndex: message.sdpMLineIndex,
        sdpMid: message.sdpMid,
      });
    }
  }

  if (!started) {
    return (
      <div className="start-screen">
        <button className="start-button" onClick={() => void handleStart()}>
          Iniciar
        </button>
      </div>
    );
  }

  return (
    <div className="app-root">
      <DisplayGrid displays={displays} streamsByIndex={streamsByIndex} />
      <div className="status-overlay">{status}</div>
    </div>
  );
}

/// Un <video> por display, ubicado dentro del contenedor según su posición/tamaño
/// relativo dentro del bounding box de todo el arrangement — un grid que refleja
/// la disposición real de los monitores, todo en la misma página (por ahora, sin
/// pestañas/ventanas separadas por monitor).
function DisplayGrid({
  displays,
  streamsByIndex,
}: {
  displays: DisplayInfo[];
  streamsByIndex: Record<number, MediaStream>;
}) {
  if (displays.length === 0) return null;

  const minX = Math.min(...displays.map((d) => d.x));
  const minY = Math.min(...displays.map((d) => d.y));
  const maxX = Math.max(...displays.map((d) => d.x + d.width));
  const maxY = Math.max(...displays.map((d) => d.y + d.height));
  const totalWidth = maxX - minX || 1;
  const totalHeight = maxY - minY || 1;

  return (
    <div className="display-grid">
      {displays.map((display, index) => (
        <div
          key={index}
          className="display-cell"
          style={{
            left: `${((display.x - minX) / totalWidth) * 100}%`,
            top: `${((display.y - minY) / totalHeight) * 100}%`,
            width: `${(display.width / totalWidth) * 100}%`,
            height: `${(display.height / totalHeight) * 100}%`,
          }}
        >
          <video
            autoPlay
            playsInline
            muted
            className="display-video"
            ref={(el) => {
              const stream = streamsByIndex[index];
              if (el && stream && el.srcObject !== stream) {
                el.srcObject = stream;
              }
            }}
          />
          <div className="display-label">
            {display.width}x{display.height}
            {display.isPrimary ? " (primario)" : ""}
          </div>
        </div>
      ))}
    </div>
  );
}

export default App;
