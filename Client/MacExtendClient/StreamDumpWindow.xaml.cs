using System.IO;
using System.Windows;
using System.Windows.Threading;
using MacExtendClient.Network;

namespace MacExtendClient;

/// <summary>
/// Herramienta de validación de la Fase 3a: recibe el stream RTP/H.264 en vivo del
/// Server y lo vuelca a un archivo Annex-B local (no decodifica ni renderiza — eso es
/// la Fase 3b). El archivo resultante se valida abriéndolo en VLC.
/// </summary>
public partial class StreamDumpWindow : Window
{
    private const int ControlPort = 47632;
    private const int VideoPort = 47633;

    private readonly string _serverHost;
    private readonly string _outputPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpControlClient _controlClient = new();
    private readonly DispatcherTimer _statusTimer;

    private RtpH264Receiver? _receiver;
    private FileStream? _outputStream;

    public StreamDumpWindow(string serverHost, string outputPath)
    {
        InitializeComponent();
        _serverHost = serverHost;
        _outputPath = outputPath;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _outputStream = new FileStream(_outputPath, FileMode.Create, FileAccess.Write);

            _receiver = new RtpH264Receiver(VideoPort);
            _receiver.FrameReceived += OnFrameReceived;

            _controlClient.Disconnected += message =>
                Dispatcher.Invoke(() => StatusText.Text = $"Desconectado: {message}");

            StatusText.Text = $"Conectando a {_serverHost}:{ControlPort}…";
            await _controlClient.ConnectAsync(_serverHost, ControlPort, _cts.Token);

            StatusText.Text = "Conectado. Esperando el primer keyframe…";
            _statusTimer.Start();
            _ = _receiver.RunAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void OnFrameReceived(byte[] annexBFrame)
    {
        _outputStream?.Write(annexBFrame, 0, annexBFrame.Length);
    }

    private void UpdateStatus()
    {
        if (_receiver == null) return;
        StatusText.Text =
            $"Paquetes: {_receiver.PacketsReceived}   Frames: {_receiver.FramesReceived}   " +
            $"Recibido: {_receiver.BytesReceived / 1024} KB\nArchivo: {_outputPath}";
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _cts.Cancel();
        if (_receiver != null)
        {
            _receiver.FrameReceived -= OnFrameReceived;
            _receiver.Dispose();
        }
        _outputStream?.Flush();
        _outputStream?.Dispose();
        _controlClient.Dispose();
    }
}
