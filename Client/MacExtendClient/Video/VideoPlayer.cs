using System.IO;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace MacExtendClient.Video;

/// <summary>
/// Decodifica un archivo de video local con Media Foundation, usando el Device
/// Manager de DXGI para forzar decode por hardware (DXVA) sobre el mismo
/// ID3D11Device que usa el renderer, y expone los frames decodificados como
/// texturas D3D11 listas para presentar.
/// </summary>
sealed class VideoPlayer : IDisposable
{
    private readonly IMFDXGIDeviceManager _dxgiDeviceManager;
    private readonly IMFMediaEngine _mediaEngine;
    private readonly IMFMediaEngineEx _mediaEngineEx;
    private readonly MFByteStream _byteStream;
    private readonly ManualResetEventSlim _readyToPlay = new(false);
    private string? _startupError;

    public SizeI VideoSize => _mediaEngine.NativeVideoSize;

    public VideoPlayer(ID3D11Device device, string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"No se encontró el archivo de video: {filePath}", filePath);
        }

        _dxgiDeviceManager = MFCreateDXGIDeviceManager();
        _dxgiDeviceManager.ResetDevice(device).CheckError();

        using IMFMediaEngineClassFactory engineFactory = new();
        using IMFAttributes attributes = MFCreateAttributes(2);
        attributes.VideoOutputFormat = Vortice.DXGI.Format.B8G8R8A8_UNorm;
        attributes.DxgiManager = _dxgiDeviceManager;

        _mediaEngine = engineFactory.CreateInstance(MediaEngineCreateFlags.None, attributes, OnPlaybackEvent);
        _mediaEngineEx = _mediaEngine.QueryInterface<IMFMediaEngineEx>();

        _byteStream = new MFByteStream(filePath);
        _mediaEngineEx.SetSourceFromByteStream(_byteStream, filePath);

        if (!_readyToPlay.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException($"Media Foundation no pudo abrir '{filePath}' (timeout esperando CanPlay).");
        }
        if (_startupError != null)
        {
            throw new InvalidOperationException(_startupError);
        }

        _mediaEngineEx.Play();
    }

    private void OnPlaybackEvent(MediaEngineEvent mediaEvent, nuint param1, int param2)
    {
        switch (mediaEvent)
        {
            case MediaEngineEvent.CanPlay:
                _readyToPlay.Set();
                break;
            case MediaEngineEvent.Error:
            case MediaEngineEvent.Abort:
                _startupError ??= $"Media Foundation playback event {mediaEvent} (param1={param1}, param2={param2}).";
                _readyToPlay.Set();
                break;
        }
    }

    /// <summary>
    /// Si hay un frame nuevo disponible según el reloj interno del MediaEngine, lo
    /// transfiere (con hardware scaling) a la textura de destino. Devuelve false si
    /// todavía no corresponde mostrar un frame nuevo.
    /// </summary>
    public bool TryTransferFrame(ID3D11Texture2D destination, int width, int height)
    {
        if (!_mediaEngine.OnVideoStreamTick(out _))
        {
            return false;
        }

        _mediaEngine.TransferVideoFrame(destination, new RectI(0, 0, width, height));
        return true;
    }

    public void Dispose()
    {
        _mediaEngine.Shutdown();
        _mediaEngineEx.Dispose();
        _mediaEngine.Dispose();
        _byteStream.Dispose();
        _dxgiDeviceManager.Dispose();
        _readyToPlay.Dispose();
    }
}
