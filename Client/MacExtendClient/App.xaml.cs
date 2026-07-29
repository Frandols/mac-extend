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
    /// "--stream &lt;ip-del-server&gt;" arranca la reproducción en vivo fullscreen vía
    /// WebRTC. Cualquier otro caso mantiene el comportamiento de la Fase 2 (reproducir
    /// un archivo local, opcionalmente pasado como argumento).
    /// </summary>
    private static Window ResolveStartupWindow(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--stream")
        {
            return new LiveStreamWindow(serverHost: args[1]);
        }
        return new MainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MFShutdown().CheckError();
        base.OnExit(e);
    }
}
