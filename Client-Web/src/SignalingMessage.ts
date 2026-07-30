/**
 * Mensaje de señalización WebRTC (SDP offer/answer, candidato ICE, o "hello" inicial),
 * mandado como JSON sobre el WebSocket en /signaling. Mismo shape que
 * SignalingMessage.swift del lado Server.
 */
export interface SignalingMessage {
  type: "hello" | "offer" | "answer" | "ice";
  sdp?: string;
  sdpMLineIndex?: number;
  sdpMid?: string;
  /** Arrangement de monitores reales — solo va en "hello". */
  displays?: DisplayInfo[];
}

/** Un monitor real. Un ghost display por entrada, mismo orden (track id "display-<índice>"). */
export interface DisplayInfo {
  width: number;
  height: number;
  x: number;
  y: number;
  isPrimary: boolean;
}
