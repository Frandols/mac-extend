import Network
import CoreMedia
import WebRTC

/// Escucha conexiones TCP de control (v1: un solo client a la vez). Al conectarse un
/// client, crea el ghost display y negocia una conexión WebRTC (señalización SDP/ICE
/// sobre esta misma conexión TCP) que hace de transporte del video; al desconectarse,
/// destruye todo — así el ghost display nunca queda huérfano.
///
/// No es @MainActor a propósito: NWListener/NWConnection invocan sus callbacks
/// (@Sendable) en la queue que se les pasa (acá siempre `.main`), pero el type checker
/// no puede inferir esa garantía, así que se maneja como una clase plana.
final class StreamingController {

    private let controlPort: UInt16
    private let width: Int
    private let height: Int
    private let fps: Int

    private var listener: NWListener?
    private var activeConnection: NWConnection?
    private var streamer: WebRTCStreamer?
    private var signalingBuffer = Data()

    private let ghostDisplay = GhostDisplayManager()
    private let capture = DisplayCapture()

    var onStatusChange: ((String) -> Void)?

    // El ritmo de captura medido con el pipeline RTP casero quedaba estancado en
    // ~23fps sin importar el bitrate — evidencia de que el techo era cuánto podía
    // procesar nuestro encoder por segundo, no la red. Con WebRTC (que encodea con
    // VideoToolbox por debajo igual, pero con bitrate adaptativo real) 720p sigue
    // siendo un punto de partida razonable; subir a 1080p es una prueba futura.
    init(controlPort: UInt16 = 47632, width: Int = 1280, height: Int = 720, fps: Int = 30) {
        self.controlPort = controlPort
        self.width = width
        self.height = height
        self.fps = fps
    }

    func start() throws {
        let listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: controlPort)!)
        // Anunciar por Bonjour (aunque el Client todavía no lo descubre — eso es Fase
        // 5) es lo que dispara de forma confiable el prompt de "Red Local" en macOS.
        listener.service = NWListener.Service(name: "MacExtend Server", type: "_macextend._tcp")
        listener.newConnectionHandler = { [weak self] connection in
            self?.handleNewConnection(connection)
        }
        listener.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.onStatusChange?("Listener listo en el puerto \(self.controlPort). Esperando conexiones…")
            case .failed(let error):
                self.onStatusChange?("Error del listener: \(error.localizedDescription)")
            case .waiting(let error):
                self.onStatusChange?("Listener esperando (retry): \(error.localizedDescription)")
            case .cancelled:
                self.onStatusChange?("Listener cancelado.")
            case .setup:
                self.onStatusChange?("Listener en setup…")
            @unknown default:
                self.onStatusChange?("Listener: estado desconocido (\(state)).")
            }
        }
        listener.start(queue: .main)
        self.listener = listener
        onStatusChange?("Iniciando listener en el puerto \(controlPort)…")
    }

    func stop() {
        listener?.cancel()
        listener = nil
        teardownStream()
        onStatusChange?("Detenido.")
    }

    private func handleNewConnection(_ connection: NWConnection) {
        guard activeConnection == nil else {
            // v1: un solo client a la vez (spec §6).
            onStatusChange?("Conexión adicional rechazada (ya hay un client activo).")
            connection.cancel()
            return
        }

        guard case .hostPort(let host, _) = connection.endpoint else {
            onStatusChange?("Conexión rechazada: endpoint inesperado (\(connection.endpoint)).")
            connection.cancel()
            return
        }

        onStatusChange?("Conexión aceptada, esperando a que quede lista…")

        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                self?.startStreaming(to: host)
            case .failed(let error):
                self?.onStatusChange?("Conexión falló: \(error.localizedDescription)")
                self?.handleDisconnect()
            case .cancelled:
                self?.handleDisconnect()
            case .waiting(let error):
                self?.onStatusChange?("Conexión esperando: \(error.localizedDescription)")
            default:
                break
            }
        }

        activeConnection = connection
        connection.start(queue: .main)
    }

    private func startStreaming(to host: NWEndpoint.Host) {
        let hostDescription = Self.describe(host)
        onStatusChange?("Client conectado (\(hostDescription)). Creando ghost display…")

        do {
            let displayID = try ghostDisplay.create(
                width: width, height: height, refreshRate: Double(fps), name: "MacExtend Ghost Display"
            )

            let streamer = WebRTCStreamer()
            self.streamer = streamer

            streamer.onLocalIceCandidate = { [weak self] candidate in
                self?.sendSignaling(SignalingMessage(
                    type: "ice", sdp: candidate.sdp,
                    sdpMLineIndex: candidate.sdpMLineIndex, sdpMid: candidate.sdpMid
                ))
            }
            streamer.onConnectionStateChange = { [weak self] state in
                self?.onStatusChange?("Estado ICE: \(Self.describeIceState(state))")
            }
            streamer.onError = { [weak self] error in
                self?.onStatusChange?("Error WebRTC: \(error.localizedDescription)")
            }

            capture.onFrame = { [weak self] sampleBuffer in
                guard let self, let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
                let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
                let timeStampNs = Int64(pts.seconds * 1_000_000_000)
                self.streamer?.send(pixelBuffer: pixelBuffer, timeStampNs: timeStampNs)
            }
            capture.onError = { [weak self] error in
                self?.onStatusChange?("Error de captura: \(error.localizedDescription)")
            }

            signalingBuffer.removeAll()
            startReceivingSignaling()

            streamer.createOffer { [weak self] result in
                guard let self else { return }
                switch result {
                case .success(let sdp):
                    self.sendSignaling(SignalingMessage(type: "offer", sdp: sdp.sdp, sdpMLineIndex: nil, sdpMid: nil))
                    Task {
                        do {
                            try await self.capture.start(displayID: displayID, width: self.width, height: self.height, fps: self.fps)
                            self.onStatusChange?("Streaming (WebRTC) a \(hostDescription)…")
                        } catch {
                            self.onStatusChange?("Error iniciando captura: \(error.localizedDescription)")
                            self.teardownStream()
                        }
                    }
                case .failure(let error):
                    self.onStatusChange?("Error creando offer: \(error.localizedDescription)")
                    self.teardownStream()
                }
            }
        } catch {
            onStatusChange?("Error iniciando streaming: \(error.localizedDescription)")
            teardownStream()
        }
    }

    // MARK: - Señalización sobre el canal de control

    private func sendSignaling(_ message: SignalingMessage) {
        guard let connection = activeConnection else { return }
        guard var data = try? JSONEncoder().encode(message) else { return }
        data.append(0x0A) // delimitador de línea
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            if let error {
                self?.onStatusChange?("Error mandando señalización: \(error.localizedDescription)")
            }
        })
    }

    private func startReceivingSignaling() {
        activeConnection?.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data, !data.isEmpty {
                self.signalingBuffer.append(data)
                self.processSignalingBuffer()
            }
            if let error {
                self.onStatusChange?("Error de señalización: \(error.localizedDescription)")
                return
            }
            if isComplete {
                return // conexión cerrada, el stateUpdateHandler ya maneja el teardown
            }
            self.startReceivingSignaling()
        }
    }

    private func processSignalingBuffer() {
        while let newlineIndex = signalingBuffer.firstIndex(of: 0x0A) {
            let lineData = Data(signalingBuffer[signalingBuffer.startIndex..<newlineIndex])
            signalingBuffer.removeSubrange(signalingBuffer.startIndex...newlineIndex)
            guard !lineData.isEmpty, let message = try? JSONDecoder().decode(SignalingMessage.self, from: lineData) else {
                continue
            }
            handleSignalingMessage(message)
        }
    }

    private func handleSignalingMessage(_ message: SignalingMessage) {
        switch message.type {
        case "answer":
            guard let sdp = message.sdp else { return }
            streamer?.setRemoteAnswer(sdp: sdp) { [weak self] error in
                if let error {
                    self?.onStatusChange?("Error fijando answer: \(error.localizedDescription)")
                }
            }
        case "ice":
            guard let sdp = message.sdp, let sdpMLineIndex = message.sdpMLineIndex else { return }
            streamer?.addRemoteIceCandidate(sdp: sdp, sdpMLineIndex: sdpMLineIndex, sdpMid: message.sdpMid)
        default:
            onStatusChange?("Mensaje de señalización desconocido: \(message.type)")
        }
    }

    private func handleDisconnect() {
        onStatusChange?("Client desconectado. Liberando ghost display…")
        teardownStream()
    }

    private func teardownStream() {
        activeConnection?.cancel()
        activeConnection = nil
        capture.onFrame = nil
        let captureRef = capture
        Task { try? await captureRef.stop() }
        streamer?.close()
        streamer = nil
        signalingBuffer.removeAll()
        ghostDisplay.destroy()
    }

    private static func describe(_ host: NWEndpoint.Host) -> String {
        switch host {
        case .ipv4(let address): return "\(address)"
        case .ipv6(let address): return "\(address)"
        case .name(let name, _): return name
        @unknown default: return "\(host)"
        }
    }

    private static func describeIceState(_ state: RTCIceConnectionState) -> String {
        switch state {
        case .new: return "new"
        case .checking: return "checking"
        case .connected: return "connected"
        case .completed: return "completed"
        case .failed: return "failed"
        case .disconnected: return "disconnected"
        case .closed: return "closed"
        case .count: return "count"
        @unknown default: return "desconocido"
        }
    }
}
