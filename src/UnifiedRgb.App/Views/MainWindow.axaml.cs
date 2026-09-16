using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UnifiedRgb.App.ViewModels;

namespace UnifiedRgb.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Application.Current is App app && app.ShouldCloseToTray())
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
            return;
        }

        // Explicit exit — let App shut down.
        if (Application.Current is App exitApp)
            exitApp.ForceExit();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Application.Current is App app)
            app.RebuildTrayProfileMenu();
    }

    private void OnColorHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (DataContext is MainViewModel vm)
            vm.ParseHexCommand.Execute(null);
        e.Handled = true;
    }

    private void OnColorHexLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            _ = vm.TryApplyHexText(vm.ColorHex, normalizeHex: true);
    }
}
