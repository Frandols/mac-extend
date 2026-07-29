using System.Net.Sockets;
using System.Text;

namespace MacExtendClient.Network;

/// <summary>
/// Mantiene una conexión TCP persistente al puerto de control del Server. Además de
/// ser la señal de connect/disconnect (cerrar esta conexión, o que se corte, es lo
/// que el Server usa para liberar el ghost display), ahora también lleva la
/// señalización WebRTC (SDP offer/answer, candidatos ICE) como líneas de JSON.
/// </summary>
sealed class TcpControlClient : IDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream? _stream;

    public event Action<string>? Disconnected;
    public event Action<string>? MessageReceived;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(host, port, cancellationToken);
        _stream = _client.GetStream();
        _ = Task.Run(() => ReceiveLoopAsync(cancellationToken), cancellationToken);
    }

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        if (_stream == null) return;
        byte[] payload = Encoding.UTF8.GetBytes(message + "\n");
        await _stream.WriteAsync(payload, cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_stream == null) return;

        var lineBuffer = new List<byte>();
        byte[] chunk = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await _stream.ReadAsync(chunk, cancellationToken);
                if (bytesRead == 0)
                {
                    Disconnected?.Invoke("El Server cerró la conexión.");
                    return;
                }

                for (int i = 0; i < bytesRead; i++)
                {
                    if (chunk[i] == (byte)'\n')
                    {
                        if (lineBuffer.Count > 0)
                        {
                            MessageReceived?.Invoke(Encoding.UTF8.GetString(lineBuffer.ToArray()));
                            lineBuffer.Clear();
                        }
                    }
                    else
                    {
                        lineBuffer.Add(chunk[i]);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre pedido por nosotros, no es un error.
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke(ex.Message);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
