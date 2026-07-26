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

    public event Action<byte[]>? FrameReceived;

    public long PacketsReceived { get; private set; }
    public long FramesReceived { get; private set; }
    public long BytesReceived { get; private set; }

    public RtpH264Receiver(int port)
    {
        _udpClient = new UdpClient(port);
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
        if (_frameBuffer.Count == 0) return;

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
