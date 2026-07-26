# MacExtend

Extiende el escritorio de una MacBook (Apple Silicon) hacia los monitores físicos de una
PC Windows conectada por WiFi en la misma red local. Windows actúa solo como "monitor
receptor" — todo el control (mouse/teclado) se mantiene en la Mac.

Especificación completa: [`docs/spec.md`](docs/spec.md).

## Componentes

- **Server** (`Server/`) — app macOS en Swift/SwiftUI. Crea ghost displays, captura con
  ScreenCaptureKit, codifica en H.264 con VideoToolbox y transmite el video.
- **Client** (`Client/`) — app Windows en C#/.NET + WPF. Descubre servers, decodifica y
  renderiza el video en pantalla completa. Todavía no implementado.

## Estado de las fases

Fases según `docs/spec.md` §10:

| # | Fase | Estado |
|---|------|--------|
| 1 | Prototipo de video local (Server) | 🚧 En progreso |
| 2 | Client mínimo (reproduce archivo pregrabado) | ⬜ Pendiente |
| 3 | Conexión de red básica, sin encriptar | ⬜ Pendiente |
| 4 | Multi-monitor | ⬜ Pendiente |
| 5 | Descubrimiento automático (mDNS) | ⬜ Pendiente |
| 6 | Seguridad (TLS, PIN, SRTP) | ⬜ Pendiente |
| 7 | Resiliencia de conexión | ⬜ Pendiente |
| 8 | UI pulida (menu bar / system tray) | ⬜ Pendiente |
| 9 | Licenciamiento | ⬜ Pendiente |
| 10 | Empaquetado y distribución | ⬜ Pendiente |

## Server — desarrollo

Requiere [XcodeGen](https://github.com/yonaskolb/XcodeGen).

```bash
cd Server
xcodegen generate
open MacExtendServer.xcodeproj
```

O compilar desde línea de comandos:

```bash
xcodebuild -project Server/MacExtendServer.xcodeproj -scheme MacExtendServer -configuration Debug build
```

### Nota sobre `CGVirtualDisplay`

La creación de ghost displays usa `CGVirtualDisplay`, una API privada de CoreGraphics
(la misma que usan Luna Display, DeskPad y BetterDisplay). No hay entitlement especial
involucrado, pero al no ser pública, Apple puede cambiar sus internals entre versiones
de macOS sin aviso. Si falla en runtime, el plan B documentado en la spec es implementar
un driver de pantalla virtual vía DriverKit.
