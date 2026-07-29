import VideoToolbox
import CoreMedia

struct EncodedFrame {
    /// NAL units sin start code (Annex-B se arma en el sender). Incluye SPS/PPS
    /// delante del IDR cuando el frame es keyframe.
    let nalUnits: [Data]
    let isKeyframe: Bool
    let presentationTimeStamp: CMTime
}

enum H264LiveEncoderError: Error, LocalizedError {
    case sessionCreationFailed(OSStatus)
    case propertyConfigFailed(OSStatus)
    case encodeFailed(OSStatus)

    var errorDescription: String? {
        switch self {
        case .sessionCreationFailed(let status):
            return "VTCompressionSessionCreate falló (OSStatus \(status))."
        case .propertyConfigFailed(let status):
            return "VTSessionSetProperty falló (OSStatus \(status))."
        case .encodeFailed(let status):
            return "VTCompressionSessionEncodeFrame falló (OSStatus \(status))."
        }
    }
}

/// Codifica frames en tiempo real a H.264, entregando NAL units individuales (no un
/// archivo) para poder empaquetarlos en RTP a medida que se generan.
final class H264LiveEncoder {

    private var session: VTCompressionSession?
    var onEncodedFrame: ((EncodedFrame) -> Void)?
    var onError: ((Error) -> Void)?

    func start(width: Int, height: Int, fps: Int) throws {
        var newSession: VTCompressionSession?
        let createStatus = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(width),
            height: Int32(height),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: nil,
            refcon: nil,
            compressionSessionOut: &newSession
        )
        guard createStatus == noErr, let newSession else {
            throw H264LiveEncoderError.sessionCreationFailed(createStatus)
        }

        try set(newSession, kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        try set(newSession, kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        try set(newSession, kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel)
        try set(newSession, kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: fps))
        try set(newSession, kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: fps))

        // Medido con diagnóstico real: a 6 Mbps el Server manda todo sin backpressure
        // (0 frames salteados), pero igual se pierde ~15% entre la Mac y Windows antes
        // de llegar al socket UDP — evidencia de que el WiFi real del usuario no banca
        // ese caudal de forma sostenida. La prioridad explícita acá es fluidez a 30fps
        // por sobre nitidez, así que se prioriza dejar mucho margen de sobra en vez de
        // ajustar al límite: 2.5 Mbps es bajo para 1080p (se va a notar borroso en
        // texto/detalle fino), pero para uso de escritorio (no video) debería alcanzar.
        let averageBitRate = 2_500_000
        try set(newSession, kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: averageBitRate))
        try set(newSession, kVTCompressionPropertyKey_DataRateLimits,
                [NSNumber(value: averageBitRate / 8), NSNumber(value: 1)] as CFArray)

        VTCompressionSessionPrepareToEncodeFrames(newSession)
        session = newSession
    }

    func stop() {
        guard let session else { return }
        VTCompressionSessionInvalidate(session)
        self.session = nil
    }

    func encode(sampleBuffer: CMSampleBuffer) {
        guard let session, let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let presentationTimeStamp = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)

        let status = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: presentationTimeStamp,
            duration: .invalid,
            frameProperties: nil,
            infoFlagsOut: nil
        ) { [weak self] status, _, outputSampleBuffer in
            guard status == noErr, let outputSampleBuffer else {
                if status != noErr {
                    self?.onError?(H264LiveEncoderError.encodeFailed(status))
                }
                return
            }
            self?.handleEncoded(sampleBuffer: outputSampleBuffer)
        }

        if status != noErr {
            onError?(H264LiveEncoderError.encodeFailed(status))
        }
    }

    private func set(_ session: VTCompressionSession, _ key: CFString, _ value: CFTypeRef) throws {
        let status = VTSessionSetProperty(session, key: key, value: value)
        guard status == noErr else {
            throw H264LiveEncoderError.propertyConfigFailed(status)
        }
    }

    private func handleEncoded(sampleBuffer: CMSampleBuffer) {
        guard let dataBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }

        var totalLength = 0
        var dataPointer: UnsafeMutablePointer<Int8>?
        let pointerStatus = CMBlockBufferGetDataPointer(
            dataBuffer, atOffset: 0, lengthAtOffsetOut: nil,
            totalLengthOut: &totalLength, dataPointerOut: &dataPointer
        )
        guard pointerStatus == noErr, let dataPointer else { return }

        var nalUnits: [Data] = []
        let isKeyframe = Self.isSyncSample(sampleBuffer)

        if isKeyframe, let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer) {
            nalUnits.append(contentsOf: Self.parameterSets(formatDescription))
        }

        // El output de VideoToolbox viene en formato AVCC: cada NAL unit tiene un
        // prefijo de 4 bytes big-endian con su longitud, en vez de start codes.
        var offset = 0
        let lengthPrefixSize = 4
        while offset + lengthPrefixSize <= totalLength {
            var nalLength: UInt32 = 0
            memcpy(&nalLength, dataPointer + offset, lengthPrefixSize)
            nalLength = CFSwapInt32BigToHost(nalLength)
            offset += lengthPrefixSize

            guard nalLength > 0, offset + Int(nalLength) <= totalLength else { break }
            nalUnits.append(Data(bytes: dataPointer + offset, count: Int(nalLength)))
            offset += Int(nalLength)
        }

        guard !nalUnits.isEmpty else { return }

        let presentationTimeStamp = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        onEncodedFrame?(EncodedFrame(
            nalUnits: nalUnits, isKeyframe: isKeyframe, presentationTimeStamp: presentationTimeStamp
        ))
    }

    private static func isSyncSample(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachmentsArray = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
              let attachments = attachmentsArray.first else {
            return true
        }
        return (attachments[kCMSampleAttachmentKey_NotSync] as? Bool) != true
    }

    private static func parameterSets(_ formatDescription: CMFormatDescription) -> [Data] {
        var count = 0
        CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            formatDescription, parameterSetIndex: 0,
            parameterSetPointerOut: nil, parameterSetSizeOut: nil,
            parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil
        )

        var result: [Data] = []
        for index in 0..<count {
            var pointer: UnsafePointer<UInt8>?
            var size = 0
            let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                formatDescription, parameterSetIndex: index,
                parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
                parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil
            )
            if status == noErr, let pointer {
                result.append(Data(bytes: pointer, count: size))
            }
        }
        return result
    }
}
