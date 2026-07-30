import Foundation

/// Mensaje de señalización WebRTC (SDP offer/answer, candidato ICE, o "hello"
/// inicial), mandado como JSON sobre el WebSocket en /signaling. Mismo shape que
/// SignalingMessage.ts del lado Client-Web.
struct SignalingMessage: Codable {
    let type: String // "hello" | "offer" | "answer" | "ice"
    let sdp: String?
    let sdpMLineIndex: Int32?
    let sdpMid: String?
    /// Arrangement de monitores reales de la PC Windows — solo viaja en "hello".
    /// Un ghost display por entrada, en el mismo orden (track id "display-<índice>").
    let displays: [DisplayInfo]?

    init(type: String, sdp: String? = nil, sdpMLineIndex: Int32? = nil, sdpMid: String? = nil, displays: [DisplayInfo]? = nil) {
        self.type = type
        self.sdp = sdp
        self.sdpMLineIndex = sdpMLineIndex
        self.sdpMid = sdpMid
        self.displays = displays
    }
}

struct DisplayInfo: Codable {
    let width: Int
    let height: Int
    let x: Int
    let y: Int
    let isPrimary: Bool
}
