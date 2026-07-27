using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace MacExtendClient.Video;

/// <summary>
/// Decodifica un stream H.264 elemental en vivo (frames Annex-B que llegan por RTP,
/// no un archivo/contenedor) usando el decoder MFT crudo (IMFTransform) en vez de
/// IMFMediaEngine — MediaEngine espera una fuente demuxeable (URL/ByteStream), no
/// frames sueltos entregados en tiempo real.
/// </summary>
sealed class H264LiveDecoder : IVideoFrameSource, IDisposable
{
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);

    private readonly IMFDXGIDeviceManager _dxgiDeviceManager;
    private readonly IMFTransform _transform;
    private readonly object _lock = new();

    // El decoder solo entrega formatos YUV (NV12/YV12/IYUV/I420/YUY2, confirmado por
    // enumeración — ningún decoder de H.264 en Windows ofrece RGB directo). El
    // swapchain espera BGRA, así que hace falta convertir color; CopySubresourceRegion
    // no lo hace (copia bytes crudos). El D3D11 Video Processor es el camino estándar
    // para esto — es lo mismo que usa IMFMediaEngine/EVR por debajo.
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _videoProcessorEnumerator;
    private readonly ID3D11VideoProcessor _videoProcessor;
    private readonly int _width;
    private readonly int _height;

    private ID3D11Texture2D? _latestTexture;
    private uint _latestSubresourceIndex;
    private bool _hasNewFrame;

    /// <summary>
    /// El loop que entrega frames (RtpH264Receiver.RunAsync) corre en background y
    /// nadie más lo observa — sin este evento, una excepción acá simplemente mata ese
    /// loop en silencio y la ventana queda congelada sin ninguna pista de por qué.
    /// </summary>
    public event Action<Exception>? DecodeError;
    public long FramesDecoded { get; private set; }

    public H264LiveDecoder(ID3D11Device device, int width, int height)
    {
        _width = width;
        _height = height;

        _dxgiDeviceManager = MFCreateDXGIDeviceManager();
        _dxgiDeviceManager.ResetDevice(device).CheckError();

        _transform = TryActivateDecoder(preferHardware: true) ?? TryActivateDecoder(preferHardware: false)
            ?? throw new InvalidOperationException("No se encontró ningún decoder MFT de H.264 en este sistema.");

        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_dxgiDeviceManager.NativePointer.ToInt64());

        using IMFMediaType inputType = MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height);
        _transform.SetInputType(0, inputType, 0);

        NegotiateOutputType();

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var contentDescription = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1),
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = new Rational(30, 1),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal,
        };
        _videoProcessorEnumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDescription);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_videoProcessorEnumerator, 0);
    }

    /// <summary>
    /// Busca un decoder MFT de H.264. Los decoders por hardware en Windows suelen
    /// registrarse como asíncronos (no síncronos), así que hay que pedir ambos modelos
    /// de transform, no solo síncrono. Con preferHardware=false se hace fallback a
    /// software si ningún decoder de hardware aparece — mejor que fallar del todo.
    /// </summary>
    private static IMFTransform? TryActivateDecoder(bool preferHardware)
    {
        EnumFlag flags = EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagAsyncmft;
        if (preferHardware)
        {
            flags |= EnumFlag.EnumFlagHardware;
        }

        // La colección y el IMFActivate elegido tienen que seguir vivos hasta terminar
        // de activar el transform — liberarlos antes (p.ej. con "using" en un método
        // que devuelve el IMFActivate para usarlo después) deja un COM object inválido.
        using IMFActivateCollection activates = MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            (uint)flags,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            null);

        using IMFActivate? firstActivate = activates.FirstOrDefault();
        return firstActivate?.ActivateObject<IMFTransform>();
    }

    private void NegotiateOutputType()
    {
        // Pedimos cada índice una sola vez (llamar GetOutputAvailableType dos veces
        // para el mismo índice, p.ej. para loggear y después para usarlo, rompía el
        // transform — probablemente al disponer la primera copia). El primero se
        // queda como candidato a usar; el resto solo se loggea y se descarta.
        IMFMediaType? chosen = null;
        for (int i = 0; ; i++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = _transform.GetOutputAvailableType(0, i);
            }
            catch
            {
                break; // MF_E_NO_MORE_TYPES: ya no hay más opciones para enumerar.
            }

            Guid candidateSubtype = candidate.GetGUID(MediaTypeAttributeKeys.Subtype);
            System.Diagnostics.Debug.WriteLine($"[MacExtend] Output type disponible [{i}]: {DescribeSubtype(candidateSubtype)} ({candidateSubtype})");

            if (chosen == null)
            {
                chosen = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }

        if (chosen == null)
        {
            throw new InvalidOperationException("El decoder no ofrece ningún output type disponible.");
        }

        using (chosen)
        {
            Guid subtype = chosen.GetGUID(MediaTypeAttributeKeys.Subtype);
            System.Diagnostics.Debug.WriteLine($"[MacExtend] Output type elegido: {DescribeSubtype(subtype)} ({subtype})");
            _transform.SetOutputType(0, chosen, 0);
        }
    }

    private static string DescribeSubtype(Guid subtype)
    {
        if (subtype == VideoFormatGuids.NV12) return "NV12";
        if (subtype == VideoFormatGuids.Rgb32) return "RGB32";
        if (subtype == VideoFormatGuids.Argb32) return "ARGB32";
        if (subtype == VideoFormatGuids.YUY2) return "YUY2";
        if (subtype == VideoFormatGuids.P010) return "P010";
        return "desconocido";
    }

    /// <summary>
    /// Recibe un frame Annex-B completo (SPS/PPS/slice con start codes, como los
    /// entrega RtpH264Receiver) y lo empuja al decoder. Corre en el thread de fondo
    /// que recibe los paquetes, no en el de UI.
    /// </summary>
    public void OnFrameReceived(byte[] annexBFrame)
    {
        try
        {
            IMFMediaBuffer buffer = MFCreateMemoryBuffer(annexBFrame.Length);
            buffer.Lock(out nint bufferPointer, out _, out _);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(annexBFrame, 0, bufferPointer, annexBFrame.Length);
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.CurrentLength = annexBFrame.Length;

            IMFSample sample = MFCreateSample();
            sample.AddBuffer(buffer);

            _transform.ProcessInput(0, sample, 0);
            DrainOutput();
        }
        catch (Exception ex)
        {
            DecodeError?.Invoke(ex);
        }
    }

    private void DrainOutput()
    {
        while (true)
        {
            var outputBuffer = new OutputDataBuffer { StreamID = 0, Sample = null };
            Result result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outputBuffer, out _);

            if (result.Code == MfETransformNeedMoreInput)
            {
                return;
            }
            if (result.Code == MfETransformStreamChange)
            {
                NegotiateOutputType();
                continue;
            }
            result.CheckError();

            using IMFSample? decodedSample = outputBuffer.Sample;
            if (decodedSample == null) return;

            StoreDecodedFrame(decodedSample);
        }
    }

    private void StoreDecodedFrame(IMFSample sample)
    {
        using IMFMediaBuffer mediaBuffer = sample.GetBufferByIndex(0);
        using IMFDXGIBuffer dxgiBuffer = mediaBuffer.QueryInterface<IMFDXGIBuffer>();

        nint texturePointer = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
        var texture = new ID3D11Texture2D(texturePointer);
        uint subresourceIndex = dxgiBuffer.SubresourceIndex;

        lock (_lock)
        {
            _latestTexture?.Dispose();
            _latestTexture = texture;
            _latestSubresourceIndex = subresourceIndex;
            _hasNewFrame = true;
        }
        FramesDecoded++;
    }

    public bool TryTransferFrame(ID3D11Texture2D destination, int width, int height)
    {
        ID3D11Texture2D? sourceTexture;
        uint subresourceIndex;

        lock (_lock)
        {
            if (!_hasNewFrame || _latestTexture == null) return false;
            sourceTexture = _latestTexture;
            subresourceIndex = _latestSubresourceIndex;
            _hasNewFrame = false;
        }

        var inputViewDescription = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = subresourceIndex },
        };
        using ID3D11VideoProcessorInputView inputView =
            _videoDevice.CreateVideoProcessorInputView(sourceTexture, _videoProcessorEnumerator, inputViewDescription);

        var outputViewDescription = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };
        using ID3D11VideoProcessorOutputView outputView =
            _videoDevice.CreateVideoProcessorOutputView(destination, _videoProcessorEnumerator, outputViewDescription);

        var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
        _videoContext.VideoProcessorBlt(_videoProcessor, outputView, 0, new[] { stream }).CheckError();

        return true;
    }

    public void Dispose()
    {
        _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _transform.Dispose();
        _dxgiDeviceManager.Dispose();
        _videoProcessor.Dispose();
        _videoProcessorEnumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        lock (_lock)
        {
            _latestTexture?.Dispose();
            _latestTexture = null;
        }
    }
}
