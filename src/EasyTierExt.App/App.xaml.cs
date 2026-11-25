using System.Runtime.InteropServices;
using EasyTierExt.HostCore.Configuration;
using Microsoft.UI.Xaml;
using WinRT;

namespace EasyTierExt.App;

public partial class App : Application
{
    private static bool _isAutoStartLaunch;
    private MainWindow? _mainWindow;

    public App()
    {
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow(_isAutoStartLaunch);
        if (!_isAutoStartLaunch)
        {
            _mainWindow.Activate();
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        _isAutoStartLaunch = args.Any(a => string.Equals(a, AutoStartService.AutoStartArg, StringComparison.OrdinalIgnoreCase));
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
