import CoreGraphics
import Foundation

enum GhostDisplayError: Error, LocalizedError {
    case creationFailed
    case applySettingsFailed
    case positioningFailed(CGError)

    var errorDescription: String? {
        switch self {
        case .creationFailed:
            return "CGVirtualDisplay no pudo inicializarse (init devolvió nil)."
        case .applySettingsFailed:
            return "CGVirtualDisplay.applySettings devolvió false."
        case .positioningFailed(let error):
            return "No se pudo posicionar el ghost display en el escritorio (CGError \(error.rawValue))."
        }
    }
}

/// Crea y destruye un "ghost display" respaldado por la API privada CGVirtualDisplay.
final class GhostDisplayManager {

    private(set) var displayID: CGDirectDisplayID?
    private var virtualDisplay: CGVirtualDisplay?
    private let queue = DispatchQueue(label: "com.macextend.server.ghostdisplay")

    /// Crea un ghost display con la resolución y refresh rate indicados, posicionado
    /// en (x, y) del espacio de coordenadas del escritorio (mismo sistema que usan las
    /// pantallas reales — ver `position(displayID:x:y:)`).
    /// - Returns: el CGDirectDisplayID del display recién creado, listo para capturar con ScreenCaptureKit.
    func create(width: Int, height: Int, refreshRate: Double, name: String, x: Int32 = 0, y: Int32 = 0) throws -> CGDirectDisplayID {
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
        let newDisplayID = CGDirectDisplayID(display.displayID)
        self.displayID = newDisplayID

        try Self.position(displayID: newDisplayID, x: x, y: y)

        return newDisplayID
    }

    /// Ubica el display en (x, y) del espacio de coordenadas del escritorio, vía las
    /// APIs públicas de CoreGraphics que usa el propio panel de Settings de macOS
    /// para acomodar monitores entre sí — sin esto, todos los ghost displays quedan
    /// apilados en el mismo origen (0,0), lo que además es la causa de que hasta
    /// ahora aparecieran como Mirror en vez de Extend. `.forSession` porque es una
    /// ubicación efímera, no queremos que sobreviva a un reinicio.
    private static func position(displayID: CGDirectDisplayID, x: Int32, y: Int32) throws {
        var config: CGDisplayConfigRef?
        guard CGBeginDisplayConfiguration(&config) == .success, let config else {
            throw GhostDisplayError.positioningFailed(.failure)
        }

        let configureStatus = CGConfigureDisplayOrigin(config, displayID, x, y)
        guard configureStatus == .success else {
            CGCancelDisplayConfiguration(config)
            throw GhostDisplayError.positioningFailed(configureStatus)
        }

        let completeStatus = CGCompleteDisplayConfiguration(config, .forSession)
        guard completeStatus == .success else {
            throw GhostDisplayError.positioningFailed(completeStatus)
        }
    }

    /// Libera el ghost display; macOS reordena el escritorio automáticamente.
    func destroy() {
        virtualDisplay = nil
        displayID = nil
    }

    /// Borde derecho de la pantalla principal de la Mac, en el espacio de
    /// coordenadas del escritorio — punto de partida para ubicar los ghost
    /// displays a la derecha de las pantallas reales en vez de superponerlos.
    static func baseOffsetX() -> Int32 {
        Int32(CGDisplayBounds(CGMainDisplayID()).maxX)
    }
}
