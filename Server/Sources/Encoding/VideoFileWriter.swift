import AVFoundation
import CoreMedia

enum VideoFileWriterError: Error, LocalizedError {
    case cannotAppendFrame
    case missingPixelBuffer

    var errorDescription: String? {
        switch self {
        case .cannotAppendFrame:
            return "AVAssetWriterInput no está listo para recibir más frames."
        case .missingPixelBuffer:
            return "El CMSampleBuffer capturado no contiene un CVPixelBuffer."
        }
    }
}

/// Codifica frames capturados a un archivo .mp4 en H.264. En Apple Silicon,
/// AVAssetWriter delega la compresión H.264 al encoder de hardware (VideoToolbox)
/// automáticamente, sin necesidad de manejar VTCompressionSession a mano.
/// Se usa .mp4 (no .mov) para que el demuxer de Media Foundation en Windows lo
/// reconozca sin ambigüedad.
final class VideoFileWriter {

    let outputURL: URL
    private let writer: AVAssetWriter
    private let input: AVAssetWriterInput
    private let adaptor: AVAssetWriterInputPixelBufferAdaptor
    private var sessionStarted = false

    init(outputURL: URL, width: Int, height: Int, fps: Int, bitrate: Int = 12_000_000) throws {
        self.outputURL = outputURL

        writer = try AVAssetWriter(url: outputURL, fileType: .mp4)

        let outputSettings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: bitrate,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                AVVideoExpectedSourceFrameRateKey: fps,
            ],
        ]

        input = AVAssetWriterInput(mediaType: .video, outputSettings: outputSettings)
        input.expectsMediaDataInRealTime = true

        adaptor = AVAssetWriterInputPixelBufferAdaptor(
            assetWriterInput: input,
            sourcePixelBufferAttributes: [
                kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                kCVPixelBufferWidthKey as String: width,
                kCVPixelBufferHeightKey as String: height,
            ]
        )

        guard writer.canAdd(input) else {
            throw VideoFileWriterError.cannotAppendFrame
        }
        writer.add(input)
    }

    /// Agrega un frame capturado. La primera llamada arranca la sesión de escritura,
    /// usando el timestamp del primer frame como origen.
    func append(sampleBuffer: CMSampleBuffer) throws {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else {
            throw VideoFileWriterError.missingPixelBuffer
        }
        let presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)

        if !sessionStarted {
            writer.startWriting()
            writer.startSession(atSourceTime: presentationTime)
            sessionStarted = true
        }

        guard input.isReadyForMoreMediaData else { return }
        guard adaptor.append(pixelBuffer, withPresentationTime: presentationTime) else {
            throw VideoFileWriterError.cannotAppendFrame
        }
    }

    func finish() async -> URL {
        input.markAsFinished()
        await writer.finishWriting()
        return outputURL
    }
}
