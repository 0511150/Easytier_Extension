using System.Runtime.InteropServices;
using EasyTierExt.HostCore.Configuration;
using Microsoft.UI.Xaml;
using WinRT;

namespace EasyTierExt.App;

public partial class App : Application
{
    /// <summary>--ui-gallery：只打开声明式控件预览窗口，供插件开发者查看效果。</summary>
    public const string UiGalleryArg = "--ui-gallery";

    private static bool _isAutoStartLaunch;
    private static bool _isUiGallery;
    private MainWindow? _mainWindow;
    private Window? _galleryWindow;

    public App()
    {
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_isUiGallery)
        {
            _galleryWindow = new Controls.UiGalleryWindow();
            _galleryWindow.Activate();
            return;
        }

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
        _isUiGallery = args.Any(a => string.Equals(a, UiGalleryArg, StringComparison.OrdinalIgnoreCase));
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
