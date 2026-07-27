using SharpGen.Runtime;
using Vortice.Direct3D11;
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

    private ID3D11Texture2D? _latestTexture;
    private uint _latestSubresourceIndex;
    private bool _hasNewFrame;

    public H264LiveDecoder(ID3D11Device device, int width, int height)
    {
        _dxgiDeviceManager = MFCreateDXGIDeviceManager();
        _dxgiDeviceManager.ResetDevice(device).CheckError();

        using IMFActivateCollection activates = MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSyncmft),
            null,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

        IMFActivate? firstActivate = activates.FirstOrDefault();
        if (firstActivate == null)
        {
            throw new InvalidOperationException("No se encontró ningún decoder MFT de H.264 en este sistema.");
        }

        _transform = firstActivate.ActivateObject<IMFTransform>();

        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_dxgiDeviceManager.NativePointer.ToInt64());

        using IMFMediaType inputType = MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height);
        _transform.SetInputType(0, inputType, 0);

        NegotiateOutputType();

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private void NegotiateOutputType()
    {
        using IMFMediaType outputType = _transform.GetOutputAvailableType(0, 0);
        _transform.SetOutputType(0, outputType, 0);
    }

    /// <summary>
    /// Recibe un frame Annex-B completo (SPS/PPS/slice con start codes, como los
    /// entrega RtpH264Receiver) y lo empuja al decoder. Corre en el thread de fondo
    /// que recibe los paquetes, no en el de UI.
    /// </summary>
    public void OnFrameReceived(byte[] annexBFrame)
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

        using ID3D11DeviceContext context = sourceTexture.Device.ImmediateContext;
        context.CopySubresourceRegion(destination, 0, 0, 0, 0, sourceTexture, subresourceIndex, null);
        return true;
    }

    public void Dispose()
    {
        _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _transform.Dispose();
        _dxgiDeviceManager.Dispose();
        lock (_lock)
        {
            _latestTexture?.Dispose();
            _latestTexture = null;
        }
    }
}
