using System.Net.Sockets;

namespace MacExtendClient.Network;

/// <summary>
/// Mantiene una conexión TCP persistente al puerto de control del Server. Cerrar esta
/// conexión (o que se corte) es la señal que usa el Server para liberar el ghost display.
/// </summary>
sealed class TcpControlClient : IDisposable
{
    private readonly TcpClient _client = new();

    public event Action<string>? Disconnected;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(host, port, cancellationToken);
        _ = Task.Run(() => MonitorConnectionAsync(cancellationToken), cancellationToken);
    }

    private async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            NetworkStream stream = _client.GetStream();
            byte[] buffer = new byte[1];
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    Disconnected?.Invoke("El Server cerró la conexión.");
                    return;
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
