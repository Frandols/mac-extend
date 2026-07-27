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
    @StateObject private var streamingRunner = StreamingRunner()

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

            Divider()

            Text("Fase 3a — Streaming en vivo")
                .font(.title3.bold())

            Text("Escucha en el puerto 47632. Al conectarse un client, crea el ghost display y transmite por RTP/UDP al puerto 47633.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 360)

            Text(streamingRunner.status)
                .font(.body.monospaced())
                .multilineTextAlignment(.center)
                .frame(maxWidth: 420)

            Button(streamingRunner.isRunning ? "Stop Streaming Server" : "Start Streaming Server") {
                if streamingRunner.isRunning {
                    streamingRunner.stop()
                } else {
                    streamingRunner.start()
                }
            }
            .buttonStyle(.borderedProminent)
        }
        .padding(32)
        .frame(width: 500, height: 520)
    }
}
