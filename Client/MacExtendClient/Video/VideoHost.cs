using System.Runtime.InteropServices;
using System.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MacExtendClient.Video;

/// <summary>
/// HwndHost que crea un HWND hijo nativo y le engancha un swapchain DXGI, para
/// presentar frames de video decodificados por hardware directamente vía Direct3D11
/// (evita el pipeline de composición software de WPF, que agregaría latencia).
/// </summary>
sealed class VideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr hwndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    private readonly ID3D11Device _device;
    private readonly int _hwndWidth;
    private readonly int _hwndHeight;
    private readonly int _bufferWidth;
    private readonly int _bufferHeight;
    private IDXGISwapChain1? _swapChain;

    /// <param name="hwndWidth">Tamaño del HWND nativo (normalmente la pantalla completa).</param>
    /// <param name="bufferWidth">
    /// Tamaño del back buffer del swapchain. No tiene por qué coincidir con el del HWND:
    /// con Scaling.Stretch, DXGI escala el buffer al presentarlo. Esto importa porque
    /// IVideoFrameSource.TryTransferFrame puede copiar sin escalar (CopySubresourceRegion,
    /// a diferencia de IMFMediaEngine.TransferVideoFrame que sí escala internamente) — el
    /// buffer tiene que coincidir con la resolución nativa del video en esos casos.
    /// </param>
    public VideoHost(ID3D11Device device, int hwndWidth, int hwndHeight, int bufferWidth, int bufferHeight)
    {
        _device = device;
        _hwndWidth = hwndWidth;
        _hwndHeight = hwndHeight;
        _bufferWidth = bufferWidth;
        _bufferHeight = bufferHeight;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // "static" es una window class nativa de Windows, no hace falta registrar la nuestra.
        IntPtr hwnd = CreateWindowEx(0, "static", string.Empty, WsChild | WsVisible,
            0, 0, _hwndWidth, _hwndHeight, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        using IDXGIFactory2 factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        SwapChainDescription1 description = new(
            (uint)_bufferWidth, (uint)_bufferHeight, Format.B8G8R8A8_UNorm, false,
            Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapChainFlags.None);

        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, description, null, null);

        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _swapChain?.Dispose();
        _swapChain = null;
        DestroyWindow(hwnd.Handle);
    }

    /// <summary>
    /// Le pide a la fuente que transfiera el frame actual (si hay uno nuevo) al back
    /// buffer del swapchain, y solo presenta si efectivamente se escribió un frame nuevo.
    /// </summary>
    public void RenderFrame(IVideoFrameSource source)
    {
        if (_swapChain == null) return;

        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        if (source.TryTransferFrame(backBuffer, _bufferWidth, _bufferHeight))
        {
            _swapChain.Present(1);
        }
    }
}
