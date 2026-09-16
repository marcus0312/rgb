using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedRgb.Core.Models;
using UnifiedRgb.Core.Services;

namespace UnifiedRgb.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly OpenRgbService _openRgb = new();
    private readonly ProfileStore _profiles = new();

    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private string _portText = "6742";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "Disconnected — start OpenRGB SDK Server (default port 6742).";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private DeviceInfo? _selectedDevice;
    [ObservableProperty] private int _red = 255;
    [ObservableProperty] private int _green;
    [ObservableProperty] private int _blue;
    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private string _profileName = "My Profile";
    [ObservableProperty] private string? _selectedProfileName;
    [ObservableProperty] private string _colorHex = "#FF0000";

    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public ObservableCollection<string> ProfileNames { get; } = new();

    public IBrush PreviewBrush => new SolidColorBrush(Color.FromRgb((byte)Red, (byte)Green, (byte)Blue));

    public string ConnectionBadge => IsConnected ? "Connected" : "Disconnected";
    public IBrush ConnectionBadgeBrush =>
        IsConnected
            ? new SolidColorBrush(Color.FromRgb(46, 160, 67))
            : new SolidColorBrush(Color.FromRgb(180, 60, 60));

    public string ProfilesFolder => _profiles.DirectoryPath;

    public MainViewModel()
    {
        _openRgb.ConnectionChanged += OnConnectionChanged;
        RefreshProfileList();
    }

    partial void OnRedChanged(int value) => NotifyColorChanged();
    partial void OnGreenChanged(int value) => NotifyColorChanged();
    partial void OnBlueChanged(int value) => NotifyColorChanged();
    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionBadge));
        OnPropertyChanged(nameof(ConnectionBadgeBrush));
    }

    private void NotifyColorChanged()
    {
        ColorHex = $"#{(byte)Red:X2}{(byte)Green:X2}{(byte)Blue:X2}";
        OnPropertyChanged(nameof(PreviewBrush));
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = _openRgb.IsConnected;
            if (!IsConnected)
            {
                Devices.Clear();
                SelectedDevice = null;
                if (!string.IsNullOrWhiteSpace(_openRgb.LastError))
                    StatusText = _openRgb.LastError;
                else
                    StatusText = "Disconnected.";
            }
        });
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText = "Port must be a number between 1 and 65535.";
            return;
        }

        IsBusy = true;
        StatusText = $"Connecting to {Host}:{port}…";
        try
        {
            await Task.Run(() => _openRgb.Connect(Host, port));
            IsConnected = true;
            StatusText = $"Connected to OpenRGB SDK at {_openRgb.Host}:{_openRgb.Port}.";
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = ex.Message;
            Devices.Clear();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _openRgb.Disconnect();
        IsConnected = false;
        Devices.Clear();
        SelectedDevice = null;
        StatusText = "Disconnected.";
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (!IsConnected)
        {
            StatusText = "Not connected.";
            return;
        }

        IsBusy = true;
        try
        {
            var list = await Task.Run(() => _openRgb.ListDevices());
            Devices.Clear();
            foreach (var d in list)
                Devices.Add(d);

            if (SelectedDevice is not null)
                SelectedDevice = Devices.FirstOrDefault(d => d.Index == SelectedDevice.Index);

            StatusText = Devices.Count == 0
                ? "Connected — no devices detected. Check OpenRGB device list / PawnIO / close vendor RGB apps."
                : $"Connected — {Devices.Count} device(s).";
        }
        catch (Exception ex)
        {
            IsConnected = _openRgb.IsConnected;
            StatusText = ex.Message;
            if (!IsConnected)
                Devices.Clear();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyToSelectedAsync()
    {
        if (SelectedDevice is null)
        {
            StatusText = "Select a device first.";
            return;
        }

        IsBusy = true;
        try
        {
            var color = CurrentColor();
            var bright = Brightness / 100.0;
            var index = SelectedDevice.Index;
            await Task.Run(() => _openRgb.ApplySolidColor(index, color, bright));
            StatusText = $"Applied {color.ToHex()} @ {Brightness:0}% to {SelectedDevice.Name}.";
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            IsConnected = _openRgb.IsConnected;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyToAllAsync()
    {
        if (!IsConnected)
        {
            StatusText = "Not connected.";
            return;
        }

        IsBusy = true;
        try
        {
            var color = CurrentColor();
            var bright = Brightness / 100.0;
            await Task.Run(() => _openRgb.ApplySolidColorToAll(color, bright));
            StatusText = $"Synced {color.ToHex()} @ {Brightness:0}% to all devices.";
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            IsConnected = _openRgb.IsConnected;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ParseHex()
    {
        var c = RgbColor.FromHex(ColorHex);
        Red = c.R;
        Green = c.G;
        Blue = c.B;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            StatusText = "Enter a profile name.";
            return;
        }

        if (Devices.Count == 0)
        {
            StatusText = "No devices to save. Connect and refresh first.";
            return;
        }

        try
        {
            var profile = new ColorProfile
            {
                Name = ProfileName.Trim(),
                Brightness = Brightness / 100.0,
                Devices = Devices.Select(d =>
                {
                    var c = d.CurrentColor ?? CurrentColor();
                    return new DeviceColorEntry
                    {
                        DeviceName = d.Name,
                        R = c.R,
                        G = c.G,
                        B = c.B
                    };
                }).ToList()
            };

            // Prefer the picker color for every device when saving an intentional look
            var pick = CurrentColor();
            foreach (var entry in profile.Devices)
            {
                entry.R = pick.R;
                entry.G = pick.G;
                entry.B = pick.B;
            }

            _profiles.Save(profile);
            RefreshProfileList();
            SelectedProfileName = profile.Name;
            StatusText = $"Saved profile \"{profile.Name}\" → {_profiles.DirectoryPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadProfileAsync()
    {
        var name = SelectedProfileName ?? ProfileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = "Select or enter a profile name to load.";
            return;
        }

        var profile = _profiles.Load(name);
        if (profile is null)
        {
            StatusText = $"Profile \"{name}\" not found.";
            return;
        }

        ProfileName = profile.Name;
        Brightness = Math.Clamp(profile.Brightness * 100.0, 0, 100);

        if (profile.Devices.Count > 0)
        {
            var first = profile.Devices[0];
            Red = first.R;
            Green = first.G;
            Blue = first.B;
        }

        if (!IsConnected)
        {
            StatusText = $"Loaded \"{profile.Name}\" into picker (not connected — connect to apply).";
            return;
        }

        IsBusy = true;
        try
        {
            var devices = await Task.Run(() => _openRgb.ListDevices());
            var bright = Brightness / 100.0;

            await Task.Run(() =>
            {
                foreach (var entry in profile.Devices)
                {
                    var match = devices.FirstOrDefault(d =>
                        string.Equals(d.Name, entry.DeviceName, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                        continue;
                    _openRgb.ApplySolidColor(match.Index, new RgbColor(entry.R, entry.G, entry.B), bright);
                }
            });

            StatusText = $"Loaded and applied profile \"{profile.Name}\".";
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            IsConnected = _openRgb.IsConnected;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        var name = SelectedProfileName ?? ProfileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = "Select a profile to delete.";
            return;
        }

        if (_profiles.Delete(name))
        {
            RefreshProfileList();
            StatusText = $"Deleted profile \"{name}\".";
        }
        else
        {
            StatusText = $"Profile \"{name}\" not found.";
        }
    }

    [RelayCommand]
    private void RefreshProfiles() => RefreshProfileList();

    private void RefreshProfileList()
    {
        ProfileNames.Clear();
        foreach (var n in _profiles.ListProfiles())
            ProfileNames.Add(n);
    }

    private RgbColor CurrentColor() => new((byte)Red, (byte)Green, (byte)Blue);

    public void Dispose()
    {
        _openRgb.ConnectionChanged -= OnConnectionChanged;
        _openRgb.Dispose();
    }
}
