import Network

/// Escucha conexiones TCP de control (v1: un solo client a la vez). Al conectarse un
/// client, crea el ghost display y arranca captura+encoding+envío RTP hacia esa IP; al
/// desconectarse, destruye todo — así el ghost display nunca queda huérfano.
///
/// No es @MainActor a propósito: NWListener/NWConnection invocan sus callbacks
/// (@Sendable) en la queue que se les pasa (acá siempre `.main`), pero el type checker
/// no puede inferir esa garantía, así que se maneja como una clase plana.
final class StreamingController {

    private let controlPort: UInt16
    private let videoPort: UInt16
    private let width: Int
    private let height: Int
    private let fps: Int

    private var listener: NWListener?
    private var activeConnection: NWConnection?
    private var sender: RtpH264Sender?

    private let ghostDisplay = GhostDisplayManager()
    private let capture = DisplayCapture()
    private let encoder = H264LiveEncoder()

    // Diagnóstico: cuántos frames de captura se saltean por backpressure (sender
    // ocupado) vs. cuántos efectivamente se mandan a encodear. Se loggea cada
    // diagnosticsLogInterval frames capturados para tener números concretos sobre
    // dónde está el cuello de botella real, sin instrumentar más a ciegas.
    private var framesCaptured = 0
    private var framesEncoded = 0
    private var framesSkipped = 0
    private let diagnosticsLogInterval = 60

    var onStatusChange: ((String) -> Void)?

    init(controlPort: UInt16 = 47632, videoPort: UInt16 = 47633, width: Int = 1920, height: Int = 1080, fps: Int = 30) {
        self.controlPort = controlPort
        self.videoPort = videoPort
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
            try encoder.start(width: width, height: height, fps: fps)

            let sender = RtpH264Sender(host: host, port: videoPort)
            sender.start()
            self.sender = sender

            encoder.onEncodedFrame = { [weak self] frame in
                self?.sender?.send(frame)
            }
            encoder.onError = { [weak self] error in
                self?.onStatusChange?("Error de encoding: \(error.localizedDescription)")
            }
            framesCaptured = 0
            framesEncoded = 0
            framesSkipped = 0

            capture.onFrame = { [weak self] sampleBuffer in
                guard let self else { return }
                self.framesCaptured += 1

                if self.sender?.isBusy == true {
                    // La red todavía no terminó de mandar el frame anterior — no
                    // vale la pena encodear este (el encoder no gasta CPU en un
                    // frame que de todos modos llegaría tarde, y no le agregamos más
                    // trabajo a una cola de red ya atrasada).
                    self.framesSkipped += 1
                } else {
                    self.framesEncoded += 1
                    self.encoder.encode(sampleBuffer: sampleBuffer)
                }

                if self.framesCaptured % self.diagnosticsLogInterval == 0 {
                    FileLogger.append("Capturados: \(self.framesCaptured)  Encodeados: \(self.framesEncoded)  Salteados (backpressure): \(self.framesSkipped)")
                }
            }
            capture.onError = { [weak self] error in
                self?.onStatusChange?("Error de captura: \(error.localizedDescription)")
            }

            Task {
                do {
                    try await capture.start(displayID: displayID, width: width, height: height, fps: fps)
                    onStatusChange?("Streaming a \(hostDescription):\(videoPort)…")
                } catch {
                    onStatusChange?("Error iniciando captura: \(error.localizedDescription)")
                    teardownStream()
                }
            }
        } catch {
            onStatusChange?("Error iniciando streaming: \(error.localizedDescription)")
            teardownStream()
        }
    }

    private func handleDisconnect() {
        onStatusChange?("Client desconectado. Liberando ghost display…")
        teardownStream()
    }

    private func teardownStream() {
        activeConnection?.cancel()
        activeConnection = nil
        sender?.stop()
        sender = nil
        capture.onFrame = nil
        let captureRef = capture
        Task { try? await captureRef.stop() }
        encoder.stop()
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
}
