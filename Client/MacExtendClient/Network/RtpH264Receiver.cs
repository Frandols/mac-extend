using System.Net.Sockets;

namespace MacExtendClient.Network;

/// <summary>
/// Recibe paquetes RTP/UDP (RFC 6184: NAL unit simple o fragmentado FU-A), reensambla
/// cada frame y lo entrega en formato Annex-B (NAL units con start code 00 00 00 01).
/// Descarta todo hasta ver el primer keyframe (SPS/PPS/IDR), para no entregar un frame
/// arrancado a mitad de un GOP que no se podría decodificar.
/// </summary>
sealed class RtpH264Receiver : IDisposable
{
    private const byte NalTypeFuA = 28;
    private const byte NalTypeIdrSlice = 5;
    private const byte NalTypeSps = 7;
    private const byte NalTypePps = 8;

    private readonly UdpClient _udpClient;
    private readonly List<byte> _frameBuffer = new();
    private List<byte>? _fuReassembly;
    private bool _sawKeyframe;
    private ushort? _lastSequenceNumber;
    private bool _frameCorrupted;

    public event Action<byte[]>? FrameReceived;

    public long PacketsReceived { get; private set; }
    public long FramesReceived { get; private set; }
    public long BytesReceived { get; private set; }
    public long FramesDropped { get; private set; }

    public RtpH264Receiver(int port)
    {
        _udpClient = new UdpClient(port);
        // Buffer por defecto del SO puede quedarse corto ante ráfagas grandes (un
        // keyframe de 1080p fragmentado en decenas de paquetes llegando casi
        // simultáneos), causando drops a nivel de SO antes de que la app los vea.
        _udpClient.Client.ReceiveBufferSize = 4 * 1024 * 1024;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udpClient.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            PacketsReceived++;
            BytesReceived += result.Buffer.Length;
            ProcessPacket(result.Buffer);
        }
    }

    private void ProcessPacket(byte[] packet)
    {
        // Header RTP fijo de 12 bytes: asumimos CC=0, X=0 porque el Server (nuestro
        // propio RtpH264Sender) siempre lo manda así.
        if (packet.Length < 12) return;

        bool marker = (packet[1] & 0x80) != 0;
        const int payloadOffset = 12;
        if (payloadOffset >= packet.Length) return;

        ushort sequenceNumber = (ushort)((packet[2] << 8) | packet[3]);
        CheckSequenceGap(sequenceNumber);

        byte nalHeader = packet[payloadOffset];
        byte nalType = (byte)(nalHeader & 0x1F);

        if (nalType == NalTypeFuA)
        {
            ProcessFuA(packet, payloadOffset);
        }
        else if (nalType >= 1 && nalType <= 23)
        {
            AppendNalUnit(packet, payloadOffset, packet.Length - payloadOffset);
        }

        if (marker)
        {
            CompleteFrame();
        }
    }

    /// <summary>
    /// UDP no garantiza orden ni entrega. Si el WiFi reordena o pierde un paquete a
    /// mitad de un frame (típicamente un keyframe fragmentado en FU-A), no queremos
    /// armar un NAL corrupto y pasárselo igual al decoder — eso puede dejarlo en mal
    /// estado hasta el próximo keyframe. Solo detectamos el hueco (sin reordenar ni
    /// esperar fragmentos tardíos, que agregaría latencia); el frame se descarta
    /// entero en CompleteFrame().
    /// </summary>
    private void CheckSequenceGap(ushort sequenceNumber)
    {
        if (_lastSequenceNumber.HasValue && (ushort)(_lastSequenceNumber.Value + 1) != sequenceNumber)
        {
            _frameCorrupted = true;
        }
        _lastSequenceNumber = sequenceNumber;
    }

    private void ProcessFuA(byte[] packet, int payloadOffset)
    {
        if (payloadOffset + 2 > packet.Length) return;

        byte fuIndicator = packet[payloadOffset];
        byte fuHeader = packet[payloadOffset + 1];
        bool start = (fuHeader & 0x80) != 0;
        bool end = (fuHeader & 0x40) != 0;
        byte originalNalType = (byte)(fuHeader & 0x1F);

        int fragmentOffset = payloadOffset + 2;
        int fragmentLength = packet.Length - fragmentOffset;
        if (fragmentLength < 0) return;

        if (start)
        {
            byte reconstructedNalHeader = (byte)((fuIndicator & 0xE0) | originalNalType);
            _fuReassembly = new List<byte> { reconstructedNalHeader };
        }

        if (_fuReassembly == null) return; // fragmento perdido antes del start, descartamos

        _fuReassembly.AddRange(new ArraySegment<byte>(packet, fragmentOffset, fragmentLength));

        if (end)
        {
            byte[] nalUnit = _fuReassembly.ToArray();
            _fuReassembly = null;
            AppendNalUnit(nalUnit, 0, nalUnit.Length);
        }
    }

    private void AppendNalUnit(byte[] source, int offset, int length)
    {
        if (length <= 0) return;

        byte nalType = (byte)(source[offset] & 0x1F);
        bool isParameterOrKeyframeNal = nalType is NalTypeIdrSlice or NalTypeSps or NalTypePps;

        if (!_sawKeyframe && !isParameterOrKeyframeNal)
        {
            return;
        }

        _frameBuffer.Add(0);
        _frameBuffer.Add(0);
        _frameBuffer.Add(0);
        _frameBuffer.Add(1);
        _frameBuffer.AddRange(new ArraySegment<byte>(source, offset, length));
    }

    private void CompleteFrame()
    {
        if (_frameBuffer.Count == 0)
        {
            _frameCorrupted = false;
            return;
        }

        if (_frameCorrupted)
        {
            FramesDropped++;
            _frameCorrupted = false;
            _frameBuffer.Clear();
            return;
        }

        _sawKeyframe = true;
        FramesReceived++;
        FrameReceived?.Invoke(_frameBuffer.ToArray());
        _frameBuffer.Clear();
    }

    public void Dispose()
    {
        _udpClient.Dispose();
    }
}
