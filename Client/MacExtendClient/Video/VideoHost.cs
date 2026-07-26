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
    private readonly int _width;
    private readonly int _height;
    private IDXGISwapChain1? _swapChain;

    public VideoHost(ID3D11Device device, int width, int height)
    {
        _device = device;
        _width = width;
        _height = height;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // "static" es una window class nativa de Windows, no hace falta registrar la nuestra.
        IntPtr hwnd = CreateWindowEx(0, "static", string.Empty, WsChild | WsVisible,
            0, 0, _width, _height, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        using IDXGIFactory2 factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        SwapChainDescription1 description = new(
            (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, false,
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
    /// Le pide al VideoPlayer que transfiera el frame actual (si hay uno nuevo) al back
    /// buffer del swapchain, y solo presenta si efectivamente se escribió un frame nuevo.
    /// </summary>
    public void RenderFrame(VideoPlayer player)
    {
        if (_swapChain == null) return;

        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        if (player.TryTransferFrame(backBuffer, _width, _height))
        {
            _swapChain.Present(1);
        }
    }
}
