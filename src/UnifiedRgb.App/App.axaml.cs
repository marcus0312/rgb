using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UnifiedRgb.App.ViewModels;
using UnifiedRgb.App.Views;

namespace UnifiedRgb.App;

public partial class App : Application
{
    private MainViewModel? _vm;
    private MainWindow? _mainWindow;
    private bool _forceExit;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _vm = new MainViewModel();
            DataContext = _vm;

            // Rebuild tray "Load profile" submenu from current profiles.
            RebuildTrayProfileMenu();

            _mainWindow = new MainWindow { DataContext = _vm };
            desktop.MainWindow = _mainWindow;

            _vm.ShowWindowRequested += (_, _) => ShowMainWindow();
            _vm.ExitRequested += (_, _) => ForceExit();
            _vm.ProfilesChanged += (_, _) =>
                Dispatcher.UIThread.Post(RebuildTrayProfileMenu);

            var startMinimized = _vm.StartMinimized ||
                                 Environment.GetCommandLineArgs().Any(a =>
                                     string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

            if (startMinimized)
            {
                _mainWindow.WindowState = WindowState.Minimized;
                _mainWindow.ShowInTaskbar = false;
                // Hide after first layout so tray-only startup works.
                Dispatcher.UIThread.Post(() =>
                {
                    _mainWindow.Hide();
                }, DispatcherPriority.Background);
            }

            desktop.Exit += (_, _) =>
            {
                _vm.Dispose();
            };

            // Defer startup connect so the window / tray exist first.
            Dispatcher.UIThread.Post(() => _vm.BeginStartupFlow(), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;

        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ForceExit()
    {
        _forceExit = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public bool ShouldCloseToTray()
    {
        if (_forceExit)
            return false;
        return _vm?.CloseToTray == true;
    }

    public void RebuildTrayProfileMenu()
    {
        if (_vm is null)
            return;

        var icons = TrayIcon.GetIcons(this);
        var tray = icons?.FirstOrDefault();
        if (tray?.Menu is not NativeMenu menu)
            return;

        // Find or replace the "Load profile" item with a submenu of profiles.
        NativeMenuItem? loadItem = null;
        var index = -1;
        for (var i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i] is NativeMenuItem nmi && nmi.Header == "Load profile")
            {
                loadItem = nmi;
                index = i;
                break;
            }
        }

        if (loadItem is null || index < 0)
            return;

        var submenu = new NativeMenu();
        if (_vm.ProfileNames.Count == 0)
        {
            submenu.Items.Add(new NativeMenuItem("No profiles") { IsEnabled = false });
        }
        else
        {
            foreach (var name in _vm.ProfileNames.ToList())
            {
                var profileName = name;
                var item = new NativeMenuItem(profileName)
                {
                    Command = _vm.TrayLoadProfileCommand,
                    CommandParameter = profileName
                };
                submenu.Items.Add(item);
            }
        }

        var replacement = new NativeMenuItem("Load profile") { Menu = submenu };
        menu.Items[index] = replacement;
    }
}
