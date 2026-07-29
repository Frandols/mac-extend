import Foundation

/// Mensaje de señalización WebRTC (SDP offer/answer, candidato ICE), mandado como
/// una línea de JSON compacto sobre la misma NWConnection de control que ya existía
/// para connect/disconnect. El Client tiene el mismo shape en C# (SignalingMessage.cs).
struct SignalingMessage: Codable {
    let type: String // "offer" | "answer" | "ice"
    let sdp: String?
    let sdpMLineIndex: Int32?
    let sdpMid: String?
}
