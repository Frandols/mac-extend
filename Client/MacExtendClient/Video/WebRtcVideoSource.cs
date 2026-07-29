using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using Vortice.Direct3D11;

namespace MacExtendClient.Video;

/// <summary>
/// Maneja el lado Client de una conexión WebRTC: recibe el offer del Server (llega
/// por el canal de señalización TCP existente), arma la RTCPeerConnection de
/// SIPSorcery, decodifica el video vía FFmpegVideoEndPoint, e implementa
/// IVideoFrameSource para que VideoHost lo renderice exactamente igual que
/// H264LiveDecoder antes — reemplaza RtpH264Receiver+H264LiveDecoder. WebRTC hace acá
/// el bitrate adaptativo y la recuperación de paquetes perdidos que el pipeline RTP
/// casero no tenía.
/// </summary>
sealed class WebRtcVideoSource : IVideoFrameSource, IDisposable
{
    private readonly ID3D11Device _device;
    private readonly RTCPeerConnection _peerConnection;
    private readonly FFmpegVideoEndPoint _videoSink;
    private readonly object _lock = new();

    private byte[]? _latestFrame;
    private int _latestWidth;
    private int _latestHeight;
    private int _latestStride;
    private bool _hasNewFrame;

    /// <summary>Candidato ICE local nuevo, para mandar por el canal de señalización.</summary>
    public event Action<RTCIceCandidate>? LocalIceCandidateGenerated;
    public event Action<RTCIceConnectionState>? ConnectionStateChanged;
    public event Action<Exception>? DecodeError;
    public long FramesDecoded { get; private set; }

    public WebRtcVideoSource(ID3D11Device device)
    {
        _device = device;

        try
        {
            FFmpegInit.Initialise();
        }
        catch (Exception ex)
        {
            // SIPSorceryMedia.FFmpeg necesita los binarios nativos de FFmpeg
            // instalados en el sistema — no vienen empaquetados en el NuGet. El
            // mensaje de la excepción original ("Unable to find FFMPEG binaries")
            // no dice qué hacer, así que lo envolvemos con el fix concreto.
            throw new InvalidOperationException(
                "No se encontraron los binarios de FFmpeg. Instalalos con " +
                "'winget install \"FFmpeg (Shared)\" --version 7.0' en PowerShell " +
                "y volvé a intentar.", ex);
        }

        _videoSink = new FFmpegVideoEndPoint();
        _videoSink.RestrictFormats(format => format.Codec == VideoCodecsEnum.H264);
        _videoSink.OnVideoSinkDecodedSampleFaster += OnDecodedSample;

        // Sin configuración explícita: por defecto no usa STUN/TURN, que es lo que
        // queremos para una conexión LAN-only entre Mac y Windows.
        _peerConnection = new RTCPeerConnection();

        MediaStreamTrack videoTrack = new MediaStreamTrack(_videoSink.GetVideoSinkFormats(), MediaStreamStatusEnum.RecvOnly);
        _peerConnection.addTrack(videoTrack);

        _peerConnection.OnVideoFrameReceived += _videoSink.GotVideoFrame;
        _peerConnection.OnVideoFormatsNegotiated += formats => _videoSink.SetVideoSinkFormat(formats.First());

        _peerConnection.onicecandidate += candidate =>
        {
            if (candidate != null)
            {
                LocalIceCandidateGenerated?.Invoke(candidate);
            }
        };
        _peerConnection.oniceconnectionstatechange += state => ConnectionStateChanged?.Invoke(state);
    }

    /// <summary>
    /// Recibe el SDP offer del Server, lo fija como remote description, y devuelve
    /// el SDP del answer para mandar de vuelta por el canal de señalización.
    /// </summary>
    public async Task<string> HandleRemoteOfferAsync(string offerSdp)
    {
        var offer = new RTCSessionDescriptionInit { sdp = offerSdp, type = RTCSdpType.offer };
        _peerConnection.setRemoteDescription(offer);

        RTCSessionDescriptionInit answer = _peerConnection.createAnswer(new RTCAnswerOptions());
        await _peerConnection.setLocalDescription(answer);
        return answer.sdp;
    }

    /// <summary>Recibe un candidato ICE remoto del Server.</summary>
    public void AddRemoteIceCandidate(string candidate, string? sdpMid, ushort sdpMLineIndex)
    {
        _peerConnection.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = sdpMLineIndex,
        });
    }

    private void OnDecodedSample(RawImage rawImage)
    {
        try
        {
            // El swapchain espera BGRA (Format.B8G8R8A8_UNorm en VideoHost) — si FFmpeg
            // entrega otro layout no lo convertimos todavía, mejor fallar visible que
            // subir bytes con el layout equivocado a la textura.
            if (rawImage.PixelFormat != VideoPixelFormatsEnum.Bgra)
            {
                DecodeError?.Invoke(new InvalidOperationException(
                    $"FFmpegVideoEndPoint entregó un pixel format inesperado: {rawImage.PixelFormat} (se esperaba Bgra)."));
                return;
            }

            lock (_lock)
            {
                _latestFrame = rawImage.GetBuffer();
                _latestWidth = rawImage.Width;
                _latestHeight = rawImage.Height;
                _latestStride = rawImage.Stride;
                _hasNewFrame = true;
            }
            FramesDecoded++;
        }
        catch (Exception ex)
        {
            DecodeError?.Invoke(ex);
        }
    }

    public bool TryTransferFrame(ID3D11Texture2D destination, int width, int height)
    {
        byte[] frame;
        int frameWidth;
        int frameHeight;
        int stride;

        lock (_lock)
        {
            if (!_hasNewFrame || _latestFrame == null) return false;
            frame = _latestFrame;
            frameWidth = _latestWidth;
            frameHeight = _latestHeight;
            stride = _latestStride;
            _hasNewFrame = false;
        }

        // FFmpegVideoEndPoint entrega el frame decodificado ya en memoria de sistema
        // (a diferencia del pipeline Media Foundation viejo, que dejaba el frame en
        // una textura GPU compartible sin copiar) — subida simple CPU→GPU vía
        // UpdateSubresource. Más lenta que el camino DXGI directo de antes, pero
        // mucho más simple; se revisita si hace falta más eficiencia.
        _device.ImmediateContext.UpdateSubresource(frame, destination, 0, (uint)stride, 0);

        return true;
    }

    public void Dispose()
    {
        _videoSink.OnVideoSinkDecodedSampleFaster -= OnDecodedSample;
        _videoSink.CloseVideoSink();
        _peerConnection.close();
    }
}
