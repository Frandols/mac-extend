import ScreenCaptureKit
import CoreGraphics

enum DisplayCaptureError: Error, LocalizedError {
    case displayNotFound(CGDirectDisplayID)

    var errorDescription: String? {
        switch self {
        case .displayNotFound(let id):
            return "No se encontró un SCDisplay para el CGDirectDisplayID \(id) en SCShareableContent."
        }
    }
}

/// Captura un display (típicamente un ghost display) frame a frame vía ScreenCaptureKit.
final class DisplayCapture: NSObject, SCStreamOutput, SCStreamDelegate {

    private var stream: SCStream?
    private let outputQueue = DispatchQueue(label: "com.macextend.server.capture")
    var onFrame: ((CMSampleBuffer) -> Void)?
    var onError: ((Error) -> Void)?

    func start(displayID: CGDirectDisplayID, width: Int, height: Int, fps: Int) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let scDisplay = content.displays.first(where: { $0.displayID == displayID }) else {
            throw DisplayCaptureError.displayNotFound(displayID)
        }

        let filter = SCContentFilter(display: scDisplay, excludingWindows: [])

        let config = SCStreamConfiguration()
        config.width = width
        config.height = height
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.showsCursor = true
        config.queueDepth = 5

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: outputQueue)
        try await stream.startCapture()
        self.stream = stream
    }

    func stop() async throws {
        guard let stream else { return }
        try await stream.stopCapture()
        self.stream = nil
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid else { return }
        onFrame?(sampleBuffer)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        onError?(error)
    }
}
