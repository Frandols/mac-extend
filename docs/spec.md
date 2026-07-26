# MacExtend — Especificación Técnica del Producto (v1.0)

*Documento de especificación funcional y técnica, escrito para ser implementado por un agente de desarrollo (Claude Code) con la mínima cantidad de decisiones abiertas posibles.*

## 1. Resumen del Producto

MacExtend es un sistema de dos aplicaciones (Server + Client) que permite a un usuario extender el escritorio de una MacBook (Apple Silicon) hacia los monitores físicos de una PC Windows, conectados por WiFi en la misma red local. El usuario controla todo exclusivamente desde el teclado y mouse de la Mac. Windows actúa únicamente como "monitor receptor": muestra en pantalla completa el video que le envía la Mac, sin enviar eventos de entrada de vuelta.

Ejemplo de uso objetivo: una MacBook con su pantalla propia + una PC Windows con 2 monitores físicos = 3 escritorios extendidos de macOS en total (1 local + 2 remotos), cada uno independiente, sin duplicar contenido (extender, no espejar).

## 2. Componentes del Sistema

### 2.1 Server (macOS — se ejecuta en la MacBook)

- **Lenguaje:** Swift (nativo)
- **UI Framework:** SwiftUI para la interfaz de configuración/estado; app de tipo "menu bar app" (ícono en la barra de menú, sin ventana principal visible en uso normal)
- **Plataforma mínima:** macOS 14 Sonoma o superior, exclusivamente Apple Silicon (arm64)
- **Responsabilidades:**
  - Anunciarse en la red local vía Bonjour/mDNS para que los clientes lo descubran
  - Recibir del client la cantidad de monitores físicos de Windows y su resolución/orientación/refresh rate
  - Crear un display virtual ("ghost display") por cada monitor físico de Windows reportado, con resolución equivalente
  - Capturar el contenido de cada ghost display en tiempo real
  - Codificar cada stream de video en H.264 con aceleración de hardware (VideoToolbox)
  - Transmitir cada stream por la red, encriptado, hacia el client correspondiente
  - Gestionar el emparejamiento inicial (PIN) y las conexiones/desconexiones
  - Destruir los ghost displays automáticamente cuando no hay client conectado

### 2.2 Client (Windows — se ejecuta en la PC de escritorio)

- **Lenguaje:** C# (.NET 8) con WPF para la UI, y Direct3D11/DXVA2 para el pipeline de decodificación y render de video
- **Plataforma mínima:** Windows 10 21H2 o superior, x64
- **Responsabilidades:**
  - Descubrir servers MacExtend disponibles en la red local vía mDNS (librería NuGet "Zeroconf" o "Makaretu.Dns")
  - Detectar automáticamente los monitores físicos conectados a la PC Windows usando la API de Windows (EnumDisplayMonitors / Windows.Graphics.Display), leyendo para cada uno: resolución, orientación, refresh rate, y **posición relativa en el arrangement de Windows** (coordenadas `rcMonitor` de la estructura `MONITORINFO`, que reflejan exactamente cómo el usuario acomodó sus monitores en Configuración > Pantalla de Windows)
  - Enviar esa información completa (incluyendo las coordenadas de arrangement) al Server durante el handshake de conexión, y reenviarla si el usuario reordena sus monitores en Windows mientras la app está corriendo
  - Recibir el/los stream(s) de video, uno por monitor
  - Decodificar cada stream con aceleración de hardware
  - Renderizar cada stream decodificado en una ventana borderless a pantalla completa (kiosk mode) en el monitor físico correspondiente — NO en modo virtual display driver, ya que los monitores de Windows son físicos reales y solo actúan como "visor"
  - **Importante:** el client NO captura ni envía eventos de mouse/teclado de Windows. Es un receptor de video puro, unidireccional.
  - Detectar desconexión, mostrar overlay de "Reconectando…" y reintentar conexión automáticamente cada 2 segundos
  - Si la desconexión supera un umbral configurable (default: 15 segundos), cerrar las ventanas de pantalla completa y devolver el control total de los monitores físicos al escritorio normal de Windows

## 3. Arquitectura de Red

### 3.1 Descubrimiento
- Protocolo: mDNS/Bonjour (tipo de servicio custom, ej. `_macextend._tcp.local`)
- El Server anuncia su nombre de host, IP, puerto e ID único de emparejamiento
- El Client escucha y lista los servers disponibles en la red local

### 3.2 Transporte
- Un canal de control (TCP) para handshake, autenticación, metadata de monitores, heartbeat/keepalive
- Un canal de video por cada monitor, usando RTP sobre UDP, priorizando baja latencia sobre confiabilidad total (frames perdidos se descartan, no se retransmiten)
- Codec: H.264 (perfil High), con aceleración de hardware: VideoToolbox en el Server (encode), Media Foundation/DXVA en el Client (decode)
- Bitrate objetivo adaptativo: entre 8 y 20 Mbps por monitor según resolución (1080p ~8-12 Mbps, 1440p/4K ~15-20 Mbps), con lógica simple de reducción de bitrate si se detecta pérdida de paquetes sostenida
- Frame rate objetivo: 60 FPS (configurable a 30 FPS como modo de ahorro de ancho de banda)

### 3.3 Seguridad
- Todo el canal de control se transmite sobre TLS 1.3
- El canal de video (UDP/RTP) se encripta con SRTP, usando las claves derivadas del handshake TLS inicial
- **Emparejamiento inicial:** al conectar un client nuevo, el Server genera y muestra en su propia pantalla un PIN numérico de 6 dígitos. El usuario lo ingresa en el Client. Con ese PIN se deriva una clave compartida (ej. usando SPAKE2 o un intercambio Diffie-Hellman autenticado por PIN) que se usa para establecer el certificado/clave TLS de la sesión.
- Una vez emparejado exitosamente, el Client guarda un token/certificado local para reconectar automáticamente sin pedir PIN de nuevo, salvo que el usuario elimine el emparejamiento manualmente
- Cada Server puede tener una lista de clients emparejados, gestionable desde la UI del Server (opción "olvidar dispositivo")

## 4. Gestión de Pantallas Virtuales (Ghost Displays)

- Al conectar, el Client informa al Server: cantidad de monitores físicos, resolución de cada uno, orientación y posición relativa (para respetar el layout de escritorio extendido)
- El Server crea un ghost display por cada monitor reportado, usando la API de CoreGraphics para displays virtuales (CGVirtualDisplay). Nota para implementación: esta es una API privada de Apple usada por productos comerciales existentes (ej. Luna Display, DeskPad); si durante el desarrollo se encuentran restricciones de entitlements, la alternativa es implementar un driver de pantalla virtual vía DriverKit (framework oficial de Apple para reemplazar kernel extensions). Priorizar CGVirtualDisplay para la v1 por menor complejidad; dejar DriverKit documentado como plan B.

**Mapeo automático de arrangement (sin intervención manual del usuario):**
- El Client (Windows) lee las coordenadas `rcMonitor` de cada monitor físico vía `EnumDisplayMonitors`, que representan su posición exacta dentro del arrangement virtual de Windows (ej. Monitor A en x=0, Monitor B a su derecha en x=1920)
- Esas coordenadas se envían al Server como parte de la metadata de cada monitor (junto con resolución y orientación)
- El Server traduce esas coordenadas relativas de Windows al sistema de coordenadas de macOS, y las aplica a cada ghost display usando `CGConfigureDisplayOrigin` dentro de una transacción `CGBeginDisplayConfiguration` / `CGCompleteDisplayConfiguration`
- El resultado es que el Arrangement de macOS (Preferencias de Sistema > Pantallas) queda armado automáticamente respetando el mismo orden y disposición espacial (izquierda/derecha, arriba/abajo) que el usuario ya configuró en Windows — sin que el usuario tenga que entrar nunca a Preferencias de Sistema y arrastrar rectángulos a mano
- Si el usuario reordena sus monitores en Windows durante una sesión activa, el Client detecta el cambio (evento `WM_DISPLAYCHANGE`) y reenvía la nueva metadata al Server, que reconfigura el arrangement de los ghost displays en caliente
- Cada ghost display queda así contiguo a la pantalla física de la Mac, respetando el layout físico real de los monitores de Windows
- El usuario puede arrastrar ventanas de macOS libremente entre su pantalla local y los ghost displays, igual que con cualquier monitor externo
- Al desconectarse el client (o cerrar la app), los ghost displays correspondientes se destruyen automáticamente y macOS reordena el escritorio

## 5. Flujos de Usuario (UX)

### 5.1 Primera configuración
1. Usuario instala Server en la Mac. Ícono aparece en la barra de menú.
2. Usuario instala Client en la PC Windows. Ícono aparece en la bandeja del sistema (system tray).
3. Client detecta automáticamente el Server en la red y lo muestra en una lista.
4. Usuario selecciona el Server y hace clic en "Conectar".
5. Server muestra un PIN de 6 dígitos en su pantalla.
6. Usuario ingresa el PIN en el Client.
7. Se establece la conexión, se crean los ghost displays, y los monitores de Windows pasan a pantalla completa mostrando el escritorio extendido de macOS.

### 5.2 Uso diario
- Al iniciar ambas apps (con opción de "iniciar con el sistema" en ambas), la reconexión a dispositivos ya emparejados es automática, sin pedir PIN de nuevo.
- Desde el ícono de la bandeja/barra de menú, el usuario puede: desconectar manualmente, ver estado de conexión (latencia, bitrate actual), y acceder a configuración (calidad de video, dispositivos emparejados, FPS objetivo).

### 5.3 Manejo de desconexión
- Corte de conexión → Client muestra overlay semi-transparente "Conexión perdida. Reconectando…" sobre la última imagen congelada
- Reintento automático cada 2 segundos
- Si no reconecta en 15 segundos (configurable) → Client cierra las ventanas fullscreen y libera los monitores físicos de Windows para su uso normal
- Al reconectar, el flujo vuelve a fullscreen automáticamente sin intervención del usuario

## 6. Fuera de Alcance para v1 (explícitamente excluido)

- Envío de eventos de mouse/teclado desde Windows hacia Mac (no se implementa; el control es exclusivamente desde la Mac)
- Soporte para Macs con chip Intel
- Soporte para más de un Client conectado simultáneamente a un mismo Server (v1 es 1 Mac ↔ 1 PC Windows)
- Transmisión de audio
- Copiar/pegar de archivos o clipboard entre dispositivos
- Soporte multiplataforma para Linux

## 7. Modelo de Licenciamiento

- Licencia de pago único (no suscripción), precio bajo (rango sugerido: 9,99–19,99 USD)
- El Server (Mac) es la app que valida la licencia (es la que se compra una sola vez por usuario)
- El Client (Windows) es gratuito y no requiere licencia (se distribuye libremente, ya que sin un Server pagado no sirve para nada)
- Validación de licencia: clave de licencia generada en el momento de compra (vía Gumroad, Paddle o Lemon Squeezy — plataformas que manejan pagos e impuestos automáticamente, recomendadas para desarrolladores independientes), validada online contra la API del proveedor al activar, y guardada localmente encriptada para uso offline posterior
- Modelo de prueba: permitir 10 minutos de uso por sesión en modo trial sin licencia, luego mostrar pantalla de compra

## 8. Requisitos No Funcionales

- Latencia de video objetivo: menor a 100ms en red WiFi doméstica estándar (extremo a extremo, un solo sentido)
- Uso de CPU en Mac: menor al 25% en un core durante transmisión activa de 2 displays a 1080p60 (gracias a encoding por hardware)
- Uso de CPU en Windows: menor al 15% por stream decodificado (gracias a decoding por hardware)
- Tiempo de reconexión tras corte breve de WiFi: menor a 3 segundos
- Consumo de ancho de banda total: debe mantenerse dentro de los límites de una red WiFi doméstica estándar (802.11ac), considerando 2 streams simultáneos

## 9. Stack Tecnológico — Resumen de Decisiones

| Aspecto | Decisión |
|---|---|
| Lenguaje Server (Mac) | Swift |
| Lenguaje Client (Windows) | C# (.NET 8) + WPF, con Direct3D11 para render de video |
| Creación de ghost displays | CGVirtualDisplay (CoreGraphics, API privada) — plan B: DriverKit |
| Captura de pantalla en Mac | ScreenCaptureKit |
| Codec de video | H.264 High Profile |
| Encoding hardware (Mac) | VideoToolbox |
| Decoding hardware (Windows) | Media Foundation / DXVA2 |
| Transporte de video | RTP sobre UDP, encriptado con SRTP |
| Transporte de control | TCP sobre TLS 1.3 |
| Descubrimiento en red | mDNS/Bonjour |
| Emparejamiento/autenticación | PIN de 6 dígitos en primera conexión + intercambio de clave autenticado (SPAKE2 o equivalente) |
| Licenciamiento | Pago único vía Gumroad/Paddle/Lemon Squeezy, validado en el Server (Mac) |

## 10. Fases de Implementación Sugeridas (para Claude Code)

1. **Fase 1 — Prototipo de video local:** Server en Mac que crea un ghost display, lo captura con ScreenCaptureKit, y lo codifica en H.264 guardando a un archivo local (sin red aún). Validar que el pipeline de captura/encoding funciona.
2. **Fase 2 — Client mínimo:** App Windows que recibe un archivo/stream de video H.264 pregrabado y lo reproduce fullscreen en un monitor. Validar decoding y render.
3. **Fase 3 — Conexión de red básica:** Conectar Server y Client por IP fija (sin descubrimiento aún), streaming en vivo de un ghost display a un monitor Windows, sin encriptación. Validar latencia.
4. **Fase 4 — Multi-monitor:** Extender a manejar N ghost displays / N monitores Windows simultáneamente, respetando el arrangement físico.
5. **Fase 5 — Descubrimiento automático:** Implementar mDNS/Bonjour en ambos lados.
6. **Fase 6 — Seguridad:** Implementar TLS, PIN de emparejamiento, SRTP.
7. **Fase 7 — Resiliencia de conexión:** Reconexión automática, timeout, liberación de monitores físicos.
8. **Fase 8 — UI pulida:** Menu bar app en Mac, system tray app en Windows, pantallas de configuración y estado.
9. **Fase 9 — Licenciamiento:** Integración con proveedor de pagos, validación de licencia, modo trial.
10. **Fase 10 — Empaquetado y distribución:** Notarización de la app Mac (requerido por Apple para distribuir fuera del App Store), instalador .msi o .exe firmado para Windows.
