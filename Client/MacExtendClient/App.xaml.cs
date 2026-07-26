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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MFShutdown().CheckError();
        base.OnExit(e);
    }
}
