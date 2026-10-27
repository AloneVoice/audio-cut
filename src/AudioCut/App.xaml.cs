using System.Windows;

namespace AudioCut;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "AudioCut – błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
