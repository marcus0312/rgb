using System;
using Avalonia;
using Avalonia.Controls;

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
}
