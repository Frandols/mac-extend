using System.Windows;
using static Vortice.MediaFoundation.MediaFactory;

namespace MacExtendClient;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MFStartup(false).CheckError();

        Window window = ResolveStartupWindow(e.Args);
        window.Show();
    }

    /// <summary>
    /// Fase 3a: "--dump-stream &lt;ip-del-server&gt; &lt;archivo-salida.h264&gt;" arranca la
    /// herramienta de validación de red (recibe RTP y vuelca a archivo, sin decodificar).
    /// Cualquier otro caso mantiene el comportamiento de la Fase 2 (reproducir un
    /// archivo local, opcionalmente pasado como argumento).
    /// </summary>
    private static Window ResolveStartupWindow(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--dump-stream")
        {
            return new StreamDumpWindow(serverHost: args[1], outputPath: args[2]);
        }
        return new MainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MFShutdown().CheckError();
        base.OnExit(e);
    }
}
