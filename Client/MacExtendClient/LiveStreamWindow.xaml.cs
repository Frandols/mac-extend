using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MacExtendClient.Network;
using MacExtendClient.Video;
using SIPSorcery.Net;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace MacExtendClient;

/// <summary>
/// Ventana kiosk fullscreen que reproduce en vivo el stream WebRTC del Server —
/// decode vía WebRtcVideoSource (SIPSorcery + FFmpeg), en vez del pipeline RTP/H.264
/// casero que usaba antes (H264LiveDecoder/RtpH264Receiver, eliminados).
/// </summary>
public partial class LiveStreamWindow : Window
{
    private const int ControlPort = 47632;

    // Misma resolución fija que usa el Server (StreamingController) — negociar la
    // resolución real es explícitamente Fase 4.
    private const int VideoWidth = 1280;
    private const int VideoHeight = 720;

    private readonly string _serverHost;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _statusTimer;
    private bool _loading;
    private bool _shownFirstError;

    private ID3D11Device? _device;
    private VideoHost? _videoHost;
    private WebRtcVideoSource? _webRtcSource;
    private TcpControlClient? _controlClient;

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

        _videoHost = new VideoHost(_device, hwndWidth, hwndHeight, VideoWidth, VideoHeight);
        RootGrid.Children.Insert(0, _videoHost);

        try
        {
            _webRtcSource = new WebRtcVideoSource(_device);
            _webRtcSource.DecodeError += OnDecodeError;
            _webRtcSource.ConnectionStateChanged += OnIceConnectionStateChanged;

            _controlClient = new TcpControlClient();
            _controlClient.Disconnected += message =>
                Dispatcher.Invoke(() => StatusText.Text = $"Desconectado: {message}");
            _controlClient.MessageReceived += OnSignalingMessageReceived;

            _webRtcSource.LocalIceCandidateGenerated += candidate =>
            {
                _ = SendSignalingAsync(new SignalingMessage
                {
                    Type = "ice",
                    Sdp = candidate.candidate,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                    SdpMid = candidate.sdpMid,
                });
            };

            await _controlClient.ConnectAsync(_serverHost, ControlPort, _cts.Token);
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

    private async void OnSignalingMessageReceived(string json)
    {
        SignalingMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<SignalingMessage>(json);
        }
        catch (JsonException)
        {
            return;
        }
        if (message == null || _webRtcSource == null) return;

        try
        {
            switch (message.Type)
            {
                case "offer" when message.Sdp != null:
                    string answerSdp = await _webRtcSource.HandleRemoteOfferAsync(message.Sdp);
                    await SendSignalingAsync(new SignalingMessage { Type = "answer", Sdp = answerSdp });
                    break;
                case "ice" when message.Sdp != null:
                    _webRtcSource.AddRemoteIceCandidate(
                        message.Sdp, message.SdpMid, (ushort)(message.SdpMLineIndex ?? 0));
                    break;
            }
        }
        catch (Exception ex)
        {
            OnDecodeError(ex);
        }
    }

    private async Task SendSignalingAsync(SignalingMessage message)
    {
        if (_controlClient == null) return;
        string json = JsonSerializer.Serialize(message);
        try
        {
            await _controlClient.SendMessageAsync(json, _cts.Token);
        }
        catch (Exception ex)
        {
            OnDecodeError(ex);
        }
    }

    private void OnIceConnectionStateChanged(RTCIceConnectionState state)
    {
        Dispatcher.Invoke(() => StatusText.Text = $"Estado ICE: {state}");
    }

    private void OnDecodeError(Exception ex)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"Error: {ex.GetType().Name}: {ex.Message}";
            if (!_shownFirstError)
            {
                _shownFirstError = true;
                MessageBox.Show(ex.ToString(), "MacExtend Client — Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void UpdateStatus()
    {
        if (_webRtcSource == null) return;
        string status = $"Frames decodificados: {_webRtcSource.FramesDecoded}";

        // El texto de estado queda tapado por VideoHost (HwndHost siempre se dibuja por
        // encima del contenido WPF normal — "airspace"), así que también lo mandamos a
        // Debug Output, visible en Visual Studio sin depender del layout de la ventana.
        StatusText.Text = status;
        System.Diagnostics.Debug.WriteLine($"[MacExtend] {status}");
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_webRtcSource == null || _videoHost == null) return;
        _videoHost.RenderFrame(_webRtcSource);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        CompositionTarget.Rendering -= OnRendering;
        _cts.Cancel();

        if (_webRtcSource != null)
        {
            _webRtcSource.DecodeError -= OnDecodeError;
            _webRtcSource.ConnectionStateChanged -= OnIceConnectionStateChanged;
        }
        _webRtcSource?.Dispose();
        _controlClient?.Dispose();
        _device?.Dispose();
    }
}
