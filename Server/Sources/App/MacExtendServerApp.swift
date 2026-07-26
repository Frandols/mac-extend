import SwiftUI

@main
struct MacExtendServerApp: App {
    var body: some Scene {
        WindowGroup {
            Phase1TestView()
        }
        .windowResizability(.contentSize)
    }
}

struct Phase1TestView: View {
    @StateObject private var runner = Phase1Runner()

    var body: some View {
        VStack(spacing: 16) {
            Text("MacExtend — Fase 1")
                .font(.title2.bold())

            Text("Crea un ghost display, lo captura con ScreenCaptureKit y lo codifica en H.264 a un archivo local.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 360)

            Text(runner.status)
                .font(.body.monospaced())
                .multilineTextAlignment(.center)
                .frame(maxWidth: 420)

            if let errorMessage = runner.errorMessage {
                Text(errorMessage)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 420)
            }

            HStack(spacing: 12) {
                Button(runner.isRunning ? "Corriendo…" : "Start Test") {
                    runner.startTest()
                }
                .disabled(runner.isRunning)
                .buttonStyle(.borderedProminent)

                if let outputURL = runner.outputURL {
                    Button("Reveal in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting([outputURL])
                    }
                }
            }
        }
        .padding(32)
        .frame(width: 480, height: 320)
    }
}
