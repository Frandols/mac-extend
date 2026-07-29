import { useEffect, useRef, useState } from "react";
import type { SignalingMessage } from "./SignalingMessage";
import "./App.css";

// Puerto del HttpServer (Swifter) embebido en el Server — sirve tanto estos
// estáticos como la ruta /signaling, mismo origen, sin problemas de CORS.
const SIGNALING_PORT = 47635;

function App() {
  const videoRef = useRef<HTMLVideoElement>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const [status, setStatus] = useState("Conectando…");

  useEffect(() => {
    const ws = new WebSocket(`ws://${window.location.hostname}:${SIGNALING_PORT}/signaling`);
    wsRef.current = ws;

    ws.onopen = () => {
      setStatus("Conectado al Server, esperando oferta…");
      // Swifter no expone un callback de "conectado" del lado Server — este mensaje
      // es lo que le avisa que ya puede crear el ghost display y mandar la oferta.
      sendMessage({ type: "hello" });
    };

    ws.onmessage = (event) => {
      void handleSignalingMessage(JSON.parse(event.data) as SignalingMessage);
    };

    ws.onclose = () => setStatus("Desconectado del Server.");
    ws.onerror = () => setStatus("Error de conexión con el Server.");

    return () => {
      pcRef.current?.close();
      pcRef.current = null;
      ws.close();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function sendMessage(message: SignalingMessage) {
    wsRef.current?.send(JSON.stringify(message));
  }

  function ensurePeerConnection(): RTCPeerConnection {
    if (pcRef.current) return pcRef.current;

    // Sin STUN/TURN: Mac y Windows están en la misma LAN, alcanza con candidatos host.
    const pc = new RTCPeerConnection({ iceServers: [] });

    pc.ontrack = (event) => {
      if (videoRef.current) {
        videoRef.current.srcObject = event.streams[0];
      }
    };

    pc.onicecandidate = (event) => {
      if (event.candidate) {
        sendMessage({
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
    if (message.type === "offer" && message.sdp) {
      const pc = ensurePeerConnection();
      await pc.setRemoteDescription({ type: "offer", sdp: message.sdp });
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      sendMessage({ type: "answer", sdp: answer.sdp });
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

  return (
    <div className="app-root">
      <video ref={videoRef} autoPlay playsInline muted className="video-fullscreen" />
      <div className="status-overlay">{status}</div>
    </div>
  );
}

export default App;
