# MacExtend

Extiende el escritorio de una MacBook (Apple Silicon) hacia los monitores físicos de una
PC Windows conectada por WiFi en la misma red local. Windows actúa solo como "monitor
receptor" — todo el control (mouse/teclado) se mantiene en la Mac.

Especificación completa: [`docs/spec.md`](docs/spec.md).

## Componentes

- **Server** (`Server/`) — app macOS en Swift/SwiftUI. Crea ghost displays, captura con
  ScreenCaptureKit, transmite el video por WebRTC (`stasel/WebRTC`) y sirve el Client
  web (HTTP + WebSocket de señalización, vía `httpswift/swifter`).
- **Client-Web** (`Client-Web/`) — SPA en React + TypeScript (Vite), servida por el
  propio Server. Se abre en un navegador (Edge en Windows, en modo kiosk) y usa el
  WebRTC nativo del navegador para decodificar y renderizar el video — sin instalar
  nada en la PC Windows más que un navegador.

## Estado de las fases

Fases según `docs/spec.md` §10:

| # | Fase | Estado |
|---|------|--------|
| 1 | Prototipo de video local (Server) | ✅ Completa |
| 2 | Client mínimo (reproduce archivo pregrabado) | ✅ Completa |
| 3 | Conexión de red básica, sin encriptar | ✅ Completa (streaming en vivo por WebRTC, Client web) |
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

### Nota sobre `SignalingServer`

Un navegador no puede abrir sockets TCP crudos, así que la señalización WebRTC
(SDP offer/answer, candidatos ICE) viaja por WebSocket (`/signaling`), servido por el
mismo `HttpServer` de Swifter que sirve la SPA de React (`/`) — mismo origen, sin
CORS. Se mantiene un `NWListener` chico y separado solo para el anuncio Bonjour (dispara
el prompt de permiso de Red Local; Swifter usa sockets propios fuera de
`Network.framework` y no lo dispara por sí solo).

## Client-Web — desarrollo

Requiere [Node.js](https://nodejs.org/).

```bash
cd Client-Web
npm install
npm run dev      # servidor de desarrollo con hot reload, apunta a un Server ya corriendo
```

Para que el Server lo sirva de verdad, hay que buildearlo y copiar el resultado a los
recursos del Server:

```bash
cd Client-Web
npm run build
rm -rf ../Server/Resources/WebClient/*
cp -R dist/. ../Server/Resources/WebClient/
cd ../Server && xcodegen generate
```

En Windows, `Client-Web/launch-kiosk.bat` abre Edge en modo kiosk (fullscreen, sin
chrome del navegador) apuntando al Server — no hace falta instalar nada más.
