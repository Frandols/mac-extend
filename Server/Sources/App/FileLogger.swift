import Foundation

/// Log a un archivo fijo, independiente de cómo se haya lanzado el proceso.
///
/// `print()` a stdout depende de cómo el proceso fue lanzado: si se ejecuta el
/// binario directo desde Terminal (para poder leer los logs), macOS deja de mostrar
/// el diálogo de permiso de Screen Recording — hay que lanzar la app siempre con
/// `open` para que el permiso funcione, pero `open --stdout` no logra redirigir la
/// salida de una app GUI de forma confiable. Este logger evita el problema por
/// completo: escribe directo a un archivo, sin importar el método de lanzamiento.
enum FileLogger {
    static let url = URL(fileURLWithPath: NSHomeDirectory() + "/Library/Logs/MacExtendServer.log")

    static func append(_ message: String) {
        let line = "\(Date()) \(message)\n"
        guard let data = line.data(using: .utf8) else { return }

        if !FileManager.default.fileExists(atPath: url.path) {
            FileManager.default.createFile(atPath: url.path, contents: nil)
        }

        guard let handle = try? FileHandle(forWritingTo: url) else { return }
        defer { try? handle.close() }
        handle.seekToEndOfFile()
        handle.write(data)
    }
}
