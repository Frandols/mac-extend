import WebRTC
import CoreMedia

/// Maneja el lado Server de una conexión WebRTC: crea la RTCPeerConnection y un
/// RTCVideoSource al que se le entregan los CVPixelBuffer que ya entrega
/// DisplayCapture.onFrame (sin tocar la captura en sí), y expone la señalización
/// (SDP offer/ICE candidates) vía closures para que StreamingController la mande por
/// el canal de control TCP existente. Reemplaza a H264LiveEncoder+RtpH264Sender — acá
/// WebRTC hace el encoding (VideoToolbox por debajo en Apple Silicon), el bitrate
/// adaptativo y la recuperación de paquetes perdidos, en vez de nuestra
/// implementación RTP casera.
final class WebRTCStreamer: NSObject {

    private static let factory: RTCPeerConnectionFactory = {
        RTCInitializeSSL()
        let encoderFactory = RTCDefaultVideoEncoderFactory()
        let decoderFactory = RTCDefaultVideoDecoderFactory()
        return RTCPeerConnectionFactory(encoderFactory: encoderFactory, decoderFactory: decoderFactory)
    }()

    private let peerConnection: RTCPeerConnection
    private let videoSource: RTCVideoSource
    private let videoTrack: RTCVideoTrack
    private let dummyCapturer: RTCVideoCapturer

    /// Candidato ICE local nuevo, para mandar por el canal de señalización.
    var onLocalIceCandidate: ((RTCIceCandidate) -> Void)?
    var onConnectionStateChange: ((RTCIceConnectionState) -> Void)?
    var onError: ((Error) -> Void)?

    override init() {
        let configuration = RTCConfiguration()
        configuration.sdpSemantics = .unifiedPlan
        // Mac y Windows están en la misma LAN — no hace falta STUN/TURN, alcanza con
        // candidatos ICE de tipo host.
        configuration.iceServers = []

        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)

        let source = Self.factory.videoSource()
        self.videoSource = source
        self.videoTrack = Self.factory.videoTrack(with: source, trackId: "macextend-video0")
        self.dummyCapturer = RTCVideoCapturer(delegate: source)

        guard let connection = Self.factory.peerConnection(with: configuration, constraints: constraints, delegate: nil) else {
            fatalError("RTCPeerConnectionFactory no pudo crear una RTCPeerConnection.")
        }
        self.peerConnection = connection

        super.init()

        self.peerConnection.delegate = self
        self.peerConnection.add(self.videoTrack, streamIds: ["macextend-stream"])
    }

    /// Arranca la negociación: crea el offer, lo fija como local description, y lo
    /// entrega por el completion handler para mandarlo por el canal de señalización.
    func createOffer(completion: @escaping (Result<RTCSessionDescription, Error>) -> Void) {
        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)
        peerConnection.offer(for: constraints) { [weak self] sdp, error in
            guard let self else { return }
            if let error {
                completion(.failure(error))
                return
            }
            guard let sdp else {
                completion(.failure(WebRTCStreamerError.missingLocalDescription))
                return
            }
            self.peerConnection.setLocalDescription(sdp) { error in
                if let error {
                    completion(.failure(error))
                } else {
                    completion(.success(sdp))
                }
            }
        }
    }

    /// Recibe el SDP answer del Client (llega por el canal de señalización).
    func setRemoteAnswer(sdp: String, completion: @escaping (Error?) -> Void) {
        let description = RTCSessionDescription(type: .answer, sdp: sdp)
        peerConnection.setRemoteDescription(description, completionHandler: completion)
    }

    /// Recibe un candidato ICE remoto del Client.
    func addRemoteIceCandidate(sdp: String, sdpMLineIndex: Int32, sdpMid: String?) {
        let candidate = RTCIceCandidate(sdp: sdp, sdpMLineIndex: sdpMLineIndex, sdpMid: sdpMid)
        peerConnection.add(candidate) { [weak self] error in
            if let error {
                self?.onError?(error)
            }
        }
    }

    /// Empuja un frame capturado (mismo CVPixelBuffer que llega a DisplayCapture.onFrame,
    /// extraído del CMSampleBuffer) a WebRTC para que lo encodee y transmita.
    func send(pixelBuffer: CVPixelBuffer, timeStampNs: Int64) {
        let rtcPixelBuffer = RTCCVPixelBuffer(pixelBuffer: pixelBuffer)
        let videoFrame = RTCVideoFrame(buffer: rtcPixelBuffer, rotation: ._0, timeStampNs: timeStampNs)
        videoSource.capturer(dummyCapturer, didCapture: videoFrame)
    }

    func close() {
        peerConnection.close()
    }
}

enum WebRTCStreamerError: Error, LocalizedError {
    case missingLocalDescription

    var errorDescription: String? {
        switch self {
        case .missingLocalDescription:
            return "RTCPeerConnection no generó un SDP local al crear el offer."
        }
    }
}

extension WebRTCStreamer: RTCPeerConnectionDelegate {
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}

    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        onConnectionStateChange?(newState)
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        onLocalIceCandidate?(candidate)
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {}
}
