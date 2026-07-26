import CoreGraphics
import Foundation

/// Orquesta el pipeline de la Fase 1: crear ghost display -> capturar -> codificar a
/// archivo -> limpiar. Sin red todavía; existe para validar que cada pieza funciona.
@MainActor
final class Phase1Runner: ObservableObject {

    @Published var status: String = "Listo para iniciar."
    @Published var isRunning = false
    @Published var outputURL: URL?
    @Published var errorMessage: String?

    private let ghostDisplay = GhostDisplayManager()
    private let capture = DisplayCapture()

    private let width = 1920
    private let height = 1080
    private let fps = 30
    private let testDuration: TimeInterval = 15

    func startTest() {
        guard !isRunning else { return }
        isRunning = true
        errorMessage = nil
        outputURL = nil

        Task {
            do {
                try await runPipeline()
            } catch {
                errorMessage = error.localizedDescription
                status = "Error: \(error.localizedDescription)"
            }
            isRunning = false
        }
    }

    private func runPipeline() async throws {
        status = "Creando ghost display (\(width)x\(height)@\(fps))…"
        let displayID = try ghostDisplay.create(
            width: width, height: height, refreshRate: Double(fps), name: "MacExtend Ghost Display"
        )
        status = "Ghost display creado (ID \(displayID)). Iniciando captura…"

        let outputURL = Self.makeOutputURL()
        let writer = try VideoFileWriter(outputURL: outputURL, width: width, height: height, fps: fps)

        capture.onFrame = { sampleBuffer in
            try? writer.append(sampleBuffer: sampleBuffer)
        }
        capture.onError = { [weak self] error in
            Task { @MainActor in
                self?.errorMessage = error.localizedDescription
            }
        }

        try await capture.start(displayID: displayID, width: width, height: height, fps: fps)
        status = "Capturando y codificando por \(Int(testDuration))s…"

        try await Task.sleep(nanoseconds: UInt64(testDuration * 1_000_000_000))

        status = "Finalizando captura…"
        try await capture.stop()
        capture.onFrame = nil

        let finalURL = await writer.finish()
        ghostDisplay.destroy()

        self.outputURL = finalURL
        status = "Listo. Archivo generado en \(finalURL.path)"
    }

    private static func makeOutputURL() -> URL {
        let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first!
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        let name = "MacExtendPhase1_\(formatter.string(from: Date())).mov"
        return desktop.appendingPathComponent(name)
    }
}
