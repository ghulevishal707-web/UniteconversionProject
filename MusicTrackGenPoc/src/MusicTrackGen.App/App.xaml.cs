using System.Windows;
using System.Windows.Threading;

namespace MusicTrackGen.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A PoC should fail loudly and legibly rather than vanishing.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
