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
}
