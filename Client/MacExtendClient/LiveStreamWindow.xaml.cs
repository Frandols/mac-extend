using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MacExtendClient.Network;
using MacExtendClient.Video;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace MacExtendClient;

/// <summary>
/// Ventana kiosk fullscreen que reproduce en vivo el stream RTP/H.264 del Server
/// (Fase 3b) — decode crudo vía IMFTransform en H264LiveDecoder, en vez del archivo
/// local que usa MainWindow (Fase 2).
/// </summary>
public partial class LiveStreamWindow : Window
{
    private const int ControlPort = 47632;
    private const int VideoPort = 47633;

    // Misma resolución fija que usa el Server (StreamingController) — negociar la
    // resolución real es explícitamente Fase 4.
    private const int VideoWidth = 1920;
    private const int VideoHeight = 1080;

    private readonly string _serverHost;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _statusTimer;
    private bool _loading;
    private bool _shownFirstError;

    private ID3D11Device? _device;
    private VideoHost? _videoHost;
    private H264LiveDecoder? _decoder;
    private TcpControlClient? _controlClient;
    private RtpH264Receiver? _receiver;

    public LiveStreamWindow(string serverHost)
    {
        InitializeComponent();
        _serverHost = serverHost;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true;

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

        int hwndWidth = (int)SystemParameters.PrimaryScreenWidth;
        int hwndHeight = (int)SystemParameters.PrimaryScreenHeight;

        // El buffer del swapchain va al tamaño nativo del video (no de la pantalla):
        // H264LiveDecoder copia con CopySubresourceRegion, que no escala. DXGI
        // (Scaling.Stretch) se encarga de estirar el buffer al HWND al presentar.
        _videoHost = new VideoHost(_device, hwndWidth, hwndHeight, VideoWidth, VideoHeight);
        RootGrid.Children.Insert(0, _videoHost);

        try
        {
            _decoder = new H264LiveDecoder(_device, VideoWidth, VideoHeight);
            _decoder.DecodeError += OnDecodeError;

            _receiver = new RtpH264Receiver(VideoPort);
            _receiver.FrameReceived += _decoder.OnFrameReceived;

            _controlClient = new TcpControlClient();
            _controlClient.Disconnected += message =>
                Dispatcher.Invoke(() => StatusText.Text = $"Desconectado: {message}");

            await _controlClient.ConnectAsync(_serverHost, ControlPort, _cts.Token);
            _ = _receiver.RunAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No se pudo conectar a '{_serverHost}':\n{ex}",
                "MacExtend Client", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        _statusTimer.Start();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnDecodeError(Exception ex)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"Error de decode: {ex.GetType().Name}: {ex.Message}";
            if (!_shownFirstError)
            {
                _shownFirstError = true;
                MessageBox.Show(ex.ToString(), "MacExtend Client — Error de decode",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void UpdateStatus()
    {
        if (_receiver == null || _decoder == null) return;
        StatusText.Text =
            $"Paquetes: {_receiver.PacketsReceived}   Frames recibidos: {_receiver.FramesReceived}   " +
            $"Frames decodificados: {_decoder.FramesDecoded}";
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_decoder == null || _videoHost == null) return;
        _videoHost.RenderFrame(_decoder);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        CompositionTarget.Rendering -= OnRendering;
        _cts.Cancel();

        if (_receiver != null && _decoder != null)
        {
            _receiver.FrameReceived -= _decoder.OnFrameReceived;
        }
        if (_decoder != null)
        {
            _decoder.DecodeError -= OnDecodeError;
        }
        _receiver?.Dispose();
        _decoder?.Dispose();
        _controlClient?.Dispose();
        _device?.Dispose();
    }
}
