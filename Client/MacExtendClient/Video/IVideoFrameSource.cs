using Vortice.Direct3D11;

namespace MacExtendClient.Video;

/// <summary>
/// Fuente de frames decodificados que VideoHost puede presentar — implementada tanto
/// por VideoPlayer (archivo local, Fase 2) como por H264LiveDecoder (stream en vivo,
/// Fase 3b).
/// </summary>
interface IVideoFrameSource
{
    /// <summary>
    /// Si hay un frame nuevo disponible, lo transfiere a la textura de destino y
    /// devuelve true. Si todavía no corresponde mostrar un frame nuevo, devuelve false.
    /// </summary>
    bool TryTransferFrame(ID3D11Texture2D destination, int width, int height);
}
