import Foundation

/// Envuelve StreamingController para exponer su estado a la UI de SwiftUI.
@MainActor
final class StreamingRunner: ObservableObject {
    @Published var status: String = "Detenido."
    @Published var isRunning = false

    private var controller: StreamingController?

    func start() {
        guard !isRunning else { return }

        let controller = StreamingController()
        controller.onStatusChange = { [weak self] message in
            Task { @MainActor in
                self?.status = message
            }
        }

        do {
            try controller.start()
            self.controller = controller
            isRunning = true
        } catch {
            status = "Error: \(error.localizedDescription)"
        }
    }

    func stop() {
        controller?.stop()
        controller = nil
        isRunning = false
        status = "Detenido."
    }
}
