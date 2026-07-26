import Network
import CoreMedia

/// Empaqueta NAL units en RTP (RFC 6184: paquete simple o fragmentación FU-A) y los
/// manda por UDP. Sin retransmisión ni control de congestión — coherente con la spec,
/// que prioriza baja latencia sobre confiabilidad total para el canal de video.
final class RtpH264Sender {

    private let connection: NWConnection
    private let ssrc = UInt32.random(in: UInt32.min...UInt32.max)
    private var sequenceNumber = UInt16.random(in: .min ... .max)
    private let clockRate = 90_000.0
    private let payloadType: UInt8 = 96
    private let maxPayloadSize = 1200

    init(host: NWEndpoint.Host, port: UInt16) {
        connection = NWConnection(
            host: host,
            port: NWEndpoint.Port(rawValue: port)!,
            using: .udp
        )
    }

    func start() {
        connection.start(queue: .global(qos: .userInteractive))
    }

    func stop() {
        connection.cancel()
    }

    func send(_ frame: EncodedFrame) {
        let timestamp = UInt32((frame.presentationTimeStamp.seconds * clockRate).rounded())
        let lastIndex = frame.nalUnits.count - 1
        for (index, nalUnit) in frame.nalUnits.enumerated() {
            sendNalUnit(nalUnit, timestamp: timestamp, markerIfLast: index == lastIndex)
        }
    }

    private func sendNalUnit(_ nalUnit: Data, timestamp: UInt32, markerIfLast: Bool) {
        if nalUnit.count <= maxPayloadSize {
            sendPacket(payload: nalUnit, timestamp: timestamp, marker: markerIfLast)
            return
        }

        guard let firstByte = nalUnit.first else { return }
        let nalType = firstByte & 0x1F
        let nri = firstByte & 0x60
        let fuIndicator = nri | 28 // type 28 = FU-A

        let payload = nalUnit.dropFirst()
        var offset = payload.startIndex
        var isFirstFragment = true

        while offset < payload.endIndex {
            let chunkEnd = payload.index(offset, offsetBy: maxPayloadSize - 2, limitedBy: payload.endIndex) ?? payload.endIndex
            let isLastFragment = chunkEnd == payload.endIndex

            var fuHeader = nalType
            if isFirstFragment { fuHeader |= 0x80 }
            if isLastFragment { fuHeader |= 0x40 }

            var fragment = Data([fuIndicator, fuHeader])
            fragment.append(payload[offset..<chunkEnd])
            sendPacket(payload: fragment, timestamp: timestamp, marker: markerIfLast && isLastFragment)

            offset = chunkEnd
            isFirstFragment = false
        }
    }

    private func sendPacket(payload: Data, timestamp: UInt32, marker: Bool) {
        var packet = Data(count: 12)
        packet[0] = 0x80 // V=2, P=0, X=0, CC=0
        packet[1] = (marker ? 0x80 : 0x00) | payloadType
        packet.replaceSubrange(2..<4, with: withUnsafeBytes(of: sequenceNumber.bigEndian) { Data($0) })
        packet.replaceSubrange(4..<8, with: withUnsafeBytes(of: timestamp.bigEndian) { Data($0) })
        packet.replaceSubrange(8..<12, with: withUnsafeBytes(of: ssrc.bigEndian) { Data($0) })
        packet.append(payload)

        sequenceNumber = sequenceNumber &+ 1
        connection.send(content: packet, completion: .contentProcessed { _ in })
    }
}
