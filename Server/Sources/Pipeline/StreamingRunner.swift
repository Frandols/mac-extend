import Foundation

/// Envuelve SignalingServer para exponer su estado a la UI de SwiftUI.
@MainActor
final class StreamingRunner: ObservableObject {
    @Published var status: String = "Detenido."
    @Published var isRunning = false

    private var server: SignalingServer?

    func start() {
        guard !isRunning else { return }

        let server = SignalingServer()
        server.onStatusChange = { [weak self] message in
            FileLogger.append("Status: \(message)")
            Task { @MainActor in
                self?.status = message
            }
        }

        do {
            try server.start()
            self.server = server
            isRunning = true
        } catch {
            status = "Error: \(error.localizedDescription)"
        }
    }

    func stop() {
        server?.stop()
        server = nil
        isRunning = false
        status = "Detenido."
    }
}
