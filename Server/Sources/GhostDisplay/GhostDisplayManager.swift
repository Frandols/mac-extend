import CoreGraphics
import Foundation

enum GhostDisplayError: Error, LocalizedError {
    case creationFailed
    case applySettingsFailed

    var errorDescription: String? {
        switch self {
        case .creationFailed:
            return "CGVirtualDisplay no pudo inicializarse (init devolvió nil)."
        case .applySettingsFailed:
            return "CGVirtualDisplay.applySettings devolvió false."
        }
    }
}

/// Crea y destruye un "ghost display" respaldado por la API privada CGVirtualDisplay.
final class GhostDisplayManager {

    private(set) var displayID: CGDirectDisplayID?
    private var virtualDisplay: CGVirtualDisplay?
    private let queue = DispatchQueue(label: "com.macextend.server.ghostdisplay")

    /// Crea un ghost display con la resolución y refresh rate indicados.
    /// - Returns: el CGDirectDisplayID del display recién creado, listo para capturar con ScreenCaptureKit.
    func create(width: Int, height: Int, refreshRate: Double, name: String) throws -> CGDirectDisplayID {
        let descriptor = CGVirtualDisplayDescriptor()
        descriptor.name = name
        descriptor.maxPixelsWide = UInt(width)
        descriptor.maxPixelsHigh = UInt(height)
        descriptor.sizeInMillimeters = CGSize(width: 521, height: 293) // ~24" 16:9, valor informativo
        descriptor.productID = 0x1234
        descriptor.vendorID = 0x3456
        descriptor.serialNum = UInt32(Date().timeIntervalSince1970)
        descriptor.dispatchQueue = queue
        descriptor.terminationHandler = { [weak self] _, reason in
            self?.displayID = nil
            self?.virtualDisplay = nil
        }

        guard let display = CGVirtualDisplay(descriptor: descriptor) else {
            throw GhostDisplayError.creationFailed
        }

        let mode = CGVirtualDisplayMode(width: UInt(width), height: UInt(height), refreshRate: refreshRate)
        let settings = CGVirtualDisplaySettings()
        settings.hiDPI = 0
        settings.modes = [mode]

        guard display.apply(settings) else {
            throw GhostDisplayError.applySettingsFailed
        }

        self.virtualDisplay = display
        self.displayID = CGDirectDisplayID(display.displayID)
        return CGDirectDisplayID(display.displayID)
    }

    /// Libera el ghost display; macOS reordena el escritorio automáticamente.
    func destroy() {
        virtualDisplay = nil
        displayID = nil
    }
}
