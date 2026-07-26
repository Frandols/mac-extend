using System.IO;
using System.Windows;
using System.Windows.Media;
using MacExtendClient.Video;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace MacExtendClient;

/// <summary>
/// Ventana kiosk fullscreen que reproduce, por hardware, el archivo de video pasado
/// por línea de comandos (o "sample.mp4" junto al ejecutable si no se pasa ninguno).
/// </summary>
public partial class MainWindow : Window
{
    private ID3D11Device? _device;
    private VideoHost? _videoHost;
    private VideoPlayer? _videoPlayer;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowState = WindowState.Maximized;

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            null,
            out ID3D11Device? device).CheckError();
        _device = device!;

        using (ID3D11Multithread multithread = _device.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }

        // Fase 2: un solo monitor (el primario). Elegir el monitor físico correcto
        // vía EnumDisplayMonitors es explícitamente Fase 4.
        int width = (int)SystemParameters.PrimaryScreenWidth;
        int height = (int)SystemParameters.PrimaryScreenHeight;

        _videoHost = new VideoHost(_device, width, height);
        Content = _videoHost;

        string videoPath = ResolveVideoPath();
        try
        {
            _videoPlayer = new VideoPlayer(_device, videoPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No se pudo reproducir '{videoPath}':\n{ex.Message}",
                "MacExtend Client", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_videoPlayer == null || _videoHost == null) return;
        _videoHost.RenderFrame(_videoPlayer);
    }

    private static string ResolveVideoPath()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            return args[1];
        }
        return Path.Combine(AppContext.BaseDirectory, "sample.mp4");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _videoPlayer?.Dispose();
        _device?.Dispose();
    }
}
