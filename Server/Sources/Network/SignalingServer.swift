import Foundation
import Network
import CoreMedia
import WebRTC
import Swifter

/// Reemplaza a StreamingController: en vez de un NWListener/NWConnection TCP crudo
/// con un protocolo de señalización a mano, sirve la SPA de React (Client-Web) y la
/// señalización WebRTC sobre un HttpServer embebido (Swifter) — necesario porque un
/// navegador no puede abrir sockets TCP crudos, solo HTTP/WebSocket.
///
/// Se mantiene un NWListener chico y separado solo para el anuncio Bonjour: Swifter
/// usa sockets propios fuera de Network.framework, así que no dispara por sí solo el
/// prompt de permiso de Red Local — ya se comprobó en este proyecto que sin el
/// anuncio Bonjour el prompt no sale de forma confiable.
final class SignalingServer {

    private let bonjourPort: UInt16
    private let httpPort: in_port_t
    private let width: Int
    private let height: Int
    private let fps: Int

    private let httpServer = HttpServer()
    private var bonjourListener: NWListener?

    private var currentSession: WebSocketSession?
    private var streamer: WebRTCStreamer?

    private let ghostDisplay = GhostDisplayManager()
    private let capture = DisplayCapture()

    var onStatusChange: ((String) -> Void)?

    init(bonjourPort: UInt16 = 47632, httpPort: in_port_t = 47635, width: Int = 1280, height: Int = 720, fps: Int = 30) {
        self.bonjourPort = bonjourPort
        self.httpPort = httpPort
        self.width = width
        self.height = height
        self.fps = fps
    }

    func start() throws {
        startBonjourListener()

        guard let webRoot = Bundle.main.resourceURL?.appendingPathComponent("WebClient") else {
            throw SignalingServerError.missingWebClientResources
        }
        httpServer["/"] = shareFilesFromDirectory(webRoot.path)
        httpServer["/signaling"] = websocket(text: { [weak self] session, text in
            self?.handleMessage(text, session: session)
        })

        try httpServer.start(httpPort, forceIPv4: true)
        onStatusChange?("Server listo en el puerto \(httpPort). Abrí http://<ip-de-esta-mac>:\(httpPort)/ desde el navegador de Windows.")
    }

    func stop() {
        httpServer.stop()
        bonjourListener?.cancel()
        bonjourListener = nil
        teardownStream()
        onStatusChange?("Detenido.")
    }

    private func startBonjourListener() {
        do {
            let listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: bonjourPort)!)
            listener.service = NWListener.Service(name: "MacExtend Server", type: "_macextend._tcp")
            // No maneja conexiones entrantes de verdad — solo existe para el anuncio
            // Bonjour (ver comentario de la clase).
            listener.newConnectionHandler = { connection in connection.cancel() }
            listener.stateUpdateHandler = { [weak self] state in
                if case .failed(let error) = state {
                    self?.onStatusChange?("Error del listener Bonjour: \(error.localizedDescription)")
                }
            }
            listener.start(queue: .main)
            self.bonjourListener = listener
        } catch {
            onStatusChange?("Error iniciando el listener Bonjour: \(error.localizedDescription)")
        }
    }

    // MARK: - Señalización WebSocket

    private func handleMessage(_ text: String, session: WebSocketSession) {
        guard let data = text.data(using: .utf8),
              let message = try? JSONDecoder().decode(SignalingMessage.self, from: data) else {
            onStatusChange?("Mensaje de señalización inválido, descartado.")
            return
        }

        // El websocket de Swifter entrega los mensajes en su propia queue en
        // background — se pasa a .main para mantener la misma convención de
        // threading que el resto de la app (GhostDisplayManager/WebRTCStreamer se
        // crean y usan siempre desde .main).
        DispatchQueue.main.async { [weak self] in
            self?.route(message, session: session)
        }
    }

    private func route(_ message: SignalingMessage, session: WebSocketSession) {
        switch message.type {
        case "hello":
            handleHello(session: session)
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

    /// Swifter no expone un callback de "conectado" del lado del WebSocket — el
    /// browser manda este mensaje apenas abre el socket, y es lo que dispara la
    /// creación del ghost display + oferta SDP (antes se disparaba al aceptar la
    /// conexión TCP).
    private func handleHello(session: WebSocketSession) {
        guard currentSession == nil else {
            // v1: un solo client a la vez (spec §6).
            onStatusChange?("Conexión adicional rechazada (ya hay un client activo).")
            return
        }

        onStatusChange?("Client conectado. Creando ghost display…")
        currentSession = session

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
                if state == .disconnected || state == .failed || state == .closed {
                    self?.teardownStream()
                }
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

            streamer.createOffer { [weak self] result in
                guard let self else { return }
                switch result {
                case .success(let sdp):
                    self.sendSignaling(SignalingMessage(type: "offer", sdp: sdp.sdp, sdpMLineIndex: nil, sdpMid: nil))
                    Task {
                        do {
                            try await self.capture.start(displayID: displayID, width: self.width, height: self.height, fps: self.fps)
                            self.onStatusChange?("Streaming (WebRTC) al Client…")
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

    private func sendSignaling(_ message: SignalingMessage) {
        guard let session = currentSession,
              let data = try? JSONEncoder().encode(message),
              let json = String(data: data, encoding: .utf8) else { return }
        session.writeText(json)
    }

    private func teardownStream() {
        guard currentSession != nil || streamer != nil else { return }
        currentSession = nil
        capture.onFrame = nil
        let captureRef = capture
        Task { try? await captureRef.stop() }
        streamer?.close()
        streamer = nil
        ghostDisplay.destroy()
        onStatusChange?("Client desconectado. Liberando ghost display…")
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

enum SignalingServerError: Error, LocalizedError {
    case missingWebClientResources

    var errorDescription: String? {
        switch self {
        case .missingWebClientResources:
            return "No se encontraron los recursos de WebClient en el bundle de la app."
        }
    }
}
