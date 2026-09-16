using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
    private const int DefaultSuggestedLedCount = 24;
    private const int StartupConnectAttempts = 12;
    private const int StartupConnectDelayMs = 2500;

    private readonly OpenRgbService _openRgb = new();
    private readonly ProfileStore _profiles = new();
    private readonly SettingsStore _settingsStore = new();
    private AppSettings _settings;
    private bool _suppressZoneLedSync;
    private bool _suppressSettingsPersist;
    private CancellationTokenSource? _startupCts;
    private bool _startupFlowStarted;
    private bool _suppressDeviceEffectSync;
    private string? _effectStateDeviceKey;
    private readonly Dictionary<string, DeviceEffectState> _deviceEffects =
        new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private string _portText = "6742";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "Disconnected — start OpenRGB SDK Server (default port 6742).";
    [ObservableProperty] private string? _vendorConflictWarning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private DeviceInfo? _selectedDevice;
    [ObservableProperty] private ZoneInfo? _selectedZone;
    [ObservableProperty] private int _zoneLedCount = DefaultSuggestedLedCount;
    [ObservableProperty] private int _red = 255;
    [ObservableProperty] private int _green;
    [ObservableProperty] private int _blue;
    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private string? _selectedEffectMode = "Static";
    [ObservableProperty] private double _effectSpeed = 50;
    [ObservableProperty] private bool _effectSpeedEnabled = true;
    [ObservableProperty] private string _profileName = "My Profile";
    [ObservableProperty] private string? _selectedProfileName;
    [ObservableProperty] private string _colorHex = "#FF0000";
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _autoApplyOnLaunch = true;

    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public ObservableCollection<ZoneInfo> Zones { get; } = new();
    public ObservableCollection<string> ProfileNames { get; } = new();
    public ObservableCollection<string> EffectModeOptions { get; } = new();

    public IBrush PreviewBrush => new SolidColorBrush(Color.FromRgb((byte)Red, (byte)Green, (byte)Blue));

    public string ConnectionBadge => IsConnected ? "Connected" : "Disconnected";
    public IBrush ConnectionBadgeBrush =>
        IsConnected
            ? new SolidColorBrush(Color.FromRgb(46, 160, 67))
            : new SolidColorBrush(Color.FromRgb(180, 60, 60));

    public string ProfilesFolder => _profiles.DirectoryPath;
    public string SettingsFolder => _settingsStore.DirectoryPath;
    public bool AutostartSupported => AutostartService.IsSupported;
    public bool HasVendorConflict => !string.IsNullOrWhiteSpace(VendorConflictWarning);

    /// <summary>Raised when the UI should show the main window (tray → Show).</summary>
    public event EventHandler? ShowWindowRequested;

    /// <summary>Raised when the app should exit fully (tray → Exit).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the profile list changes (tray submenu refresh).</summary>
    public event EventHandler? ProfilesChanged;

    public string ZoneLimitsHint
    {
        get
        {
            if (SelectedZone is null)
                return "Select a channel/zone to resize ARGB strips (e.g. CM Gen2 A1 V2).";
            var z = SelectedZone;
            return $"Current {z.LedCount} LEDs · min {z.LedsMin} · max {z.LedsMax}" +
                   (z.LedCount == 0 ? " — resize before lights will work." : "");
        }
    }

    public MainViewModel()
    {
        _settings = _settingsStore.Load();
        _suppressSettingsPersist = true;
        try
        {
            Host = string.IsNullOrWhiteSpace(_settings.Host) ? "127.0.0.1" : _settings.Host;
            PortText = _settings.Port is >= 1 and <= 65535 ? _settings.Port.ToString() : "6742";
            StartWithWindows = _settings.StartWithWindows;
            StartMinimized = _settings.StartMinimized;
            CloseToTray = _settings.CloseToTray;
            AutoApplyOnLaunch = _settings.AutoApplyOnLaunch;
            if (!string.IsNullOrWhiteSpace(_settings.LastProfileName))
            {
                ProfileName = _settings.LastProfileName;
                SelectedProfileName = _settings.LastProfileName;
            }
        }
        finally
        {
            _suppressSettingsPersist = false;
        }

        _openRgb.ConnectionChanged += OnConnectionChanged;
        RefreshProfileList();
        RefreshVendorConflicts();
        RebuildEffectModeOptions(null);
    }

    /// <summary>Call once after the UI is ready — connect with retry and auto-apply last profile.</summary>
    public void BeginStartupFlow()
    {
        if (_startupFlowStarted)
            return;
        _startupFlowStarted = true;

        // Keep HKCU Run key in sync with the setting on first launch of an installed build.
        if (AutostartService.IsSupported && StartWithWindows)
        {
            try { AutostartService.SetEnabled(true, StartMinimized); }
            catch { /* ignore registry failures */ }
        }

        _startupCts = new CancellationTokenSource();
        _ = RunStartupConnectAsync(_startupCts.Token);
    }

    partial void OnRedChanged(int value) => NotifyColorChanged();
    partial void OnGreenChanged(int value) => NotifyColorChanged();
    partial void OnBlueChanged(int value) => NotifyColorChanged();
    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionBadge));
        OnPropertyChanged(nameof(ConnectionBadgeBrush));
    }

    partial void OnVendorConflictWarningChanged(string? value) =>
        OnPropertyChanged(nameof(HasVendorConflict));

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (!_suppressDeviceEffectSync)
            PersistCurrentDeviceEffectState();

        PopulateZonesFromDevice(value);
        RebuildEffectModeOptions(value);

        if (!_suppressDeviceEffectSync)
            LoadDeviceEffectState(value);
    }

    partial void OnSelectedEffectModeChanged(string? value) => UpdateEffectSpeedEnabled();

    partial void OnSelectedZoneChanged(ZoneInfo? value)
    {
        SyncZoneLedCountFromSelection(value);
        OnPropertyChanged(nameof(ZoneLimitsHint));
    }

    partial void OnHostChanged(string value) => PersistSettings();
    partial void OnPortTextChanged(string value) => PersistSettings();
    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_suppressSettingsPersist && AutostartService.IsSupported && StartWithWindows)
        {
            try { AutostartService.SetEnabled(true, value); }
            catch { /* ignore */ }
        }
        PersistSettings();
    }
    partial void OnCloseToTrayChanged(bool value) => PersistSettings();
    partial void OnAutoApplyOnLaunchChanged(bool value) => PersistSettings();

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressSettingsPersist)
            return;

        try
        {
            if (AutostartService.IsSupported)
                AutostartService.SetEnabled(value, StartMinimized);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not update Start with Windows: {ex.Message}";
        }

        PersistSettings();
    }

    partial void OnSelectedProfileNameChanged(string? value)
    {
        if (_suppressSettingsPersist || string.IsNullOrWhiteSpace(value))
            return;
        _settings.LastProfileName = value;
        PersistSettings();
    }

    private void NotifyColorChanged()
    {
        ColorHex = $"#{(byte)Red:X2}{(byte)Green:X2}{(byte)Blue:X2}";
        OnPropertyChanged(nameof(PreviewBrush));
    }

    private void PersistSettings()
    {
        if (_suppressSettingsPersist)
            return;

        _settings.Host = Host?.Trim() ?? "127.0.0.1";
        if (int.TryParse(PortText.Trim(), out var port) && port is >= 1 and <= 65535)
            _settings.Port = port;
        _settings.StartWithWindows = StartWithWindows;
        _settings.StartMinimized = StartMinimized;
        _settings.CloseToTray = CloseToTray;
        _settings.AutoApplyOnLaunch = AutoApplyOnLaunch;
        if (!string.IsNullOrWhiteSpace(SelectedProfileName))
            _settings.LastProfileName = SelectedProfileName;
        else if (!string.IsNullOrWhiteSpace(ProfileName))
            _settings.LastProfileName = ProfileName.Trim();

        try { _settingsStore.Save(_settings); }
        catch { /* best-effort */ }
    }

    private void PopulateZonesFromDevice(DeviceInfo? device)
    {
        var previousZoneIndex = SelectedZone?.Index;
        Zones.Clear();

        if (device?.Zones is { Count: > 0 })
        {
            foreach (var z in device.Zones)
                Zones.Add(z);

            SelectedZone = previousZoneIndex is int idx
                ? Zones.FirstOrDefault(z => z.Index == idx) ?? Zones[0]
                : Zones[0];
        }
        else
        {
            SelectedZone = null;
            if (!_suppressZoneLedSync)
                ZoneLedCount = DefaultSuggestedLedCount;
        }

        OnPropertyChanged(nameof(ZoneLimitsHint));
    }

    private void SyncZoneLedCountFromSelection(ZoneInfo? zone)
    {
        if (_suppressZoneLedSync)
            return;

        if (zone is null)
        {
            ZoneLedCount = DefaultSuggestedLedCount;
            return;
        }

        if (zone.LedCount > 0)
        {
            ZoneLedCount = (int)zone.LedCount;
            return;
        }

        if (zone.LedsMax == 0 || zone.LedsMax >= DefaultSuggestedLedCount)
            ZoneLedCount = DefaultSuggestedLedCount;
        else if (zone.LedsMax > 0)
            ZoneLedCount = (int)zone.LedsMax;
        else
            ZoneLedCount = DefaultSuggestedLedCount;
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = _openRgb.IsConnected;
            if (!IsConnected)
            {
                Devices.Clear();
                Zones.Clear();
                SelectedDevice = null;
                SelectedZone = null;
                if (!string.IsNullOrWhiteSpace(_openRgb.LastError))
                    StatusText = _openRgb.LastError;
                else
                    StatusText = "Disconnected.";
            }
        });
    }

    private async Task RunStartupConnectAsync(CancellationToken ct)
    {
        RefreshVendorConflicts();

        for (var attempt = 1; attempt <= StartupConnectAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return;

            if (!int.TryParse(PortText.Trim(), out var port) || port is < 1 or > 65535)
            {
                StatusText = "Port must be a number between 1 and 65535.";
                return;
            }

            StatusText = attempt == 1
                ? $"Connecting to {Host}:{port}…"
                : $"Waiting for OpenRGB at {Host}:{port} (attempt {attempt}/{StartupConnectAttempts})…";

            try
            {
                await Task.Run(() => _openRgb.Connect(Host, port), ct);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    IsConnected = true;
                    StatusText = $"Connected to OpenRGB SDK at {_openRgb.Host}:{_openRgb.Port}.";
                    RefreshVendorConflicts();
                    await RefreshDevicesAsync();
                    if (AutoApplyOnLaunch)
                        await AutoApplyLastProfileAsync();
                });
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                if (attempt >= StartupConnectAttempts)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsConnected = false;
                        StatusText =
                            $"Could not reach OpenRGB at {Host}:{port} after {StartupConnectAttempts} tries. Start the SDK Server and click Connect.";
                        RefreshVendorConflicts();
                    });
                    return;
                }

                try
                {
                    await Task.Delay(StartupConnectDelayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task AutoApplyLastProfileAsync()
    {
        var name = _settings.LastProfileName ?? SelectedProfileName;
        if (string.IsNullOrWhiteSpace(name))
            return;

        var profile = _profiles.Load(name);
        if (profile is null)
        {
            StatusText = $"Connected — last profile \"{name}\" not found on disk.";
            return;
        }

        SelectedProfileName = profile.Name;
        ProfileName = profile.Name;
        await ApplyLoadedProfileAsync(profile, announcePrefix: "Auto-applied");
    }

    public void RefreshVendorConflicts()
    {
        var conflicts = VendorConflictDetector.DetectRunning();
        VendorConflictWarning = VendorConflictDetector.FormatWarning(conflicts);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText = "Port must be a number between 1 and 65535.";
            return;
        }

        PersistSettings();
        IsBusy = true;
        StatusText = $"Connecting to {Host}:{port}…";
        try
        {
            await Task.Run(() => _openRgb.Connect(Host, port));
            IsConnected = true;
            StatusText = $"Connected to OpenRGB SDK at {_openRgb.Host}:{_openRgb.Port}.";
            RefreshVendorConflicts();
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = ex.Message;
            Devices.Clear();
            Zones.Clear();
            RefreshVendorConflicts();
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
        Zones.Clear();
        SelectedDevice = null;
        SelectedZone = null;
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
            var previousDeviceIndex = SelectedDevice?.Index;
            var previousZoneIndex = SelectedZone?.Index;
            var previousLedCount = ZoneLedCount;

            var list = await Task.Run(() => _openRgb.ListDevices());
            Devices.Clear();
            foreach (var d in list)
                Devices.Add(d);

            _suppressZoneLedSync = true;
            try
            {
                if (previousDeviceIndex is int di)
                    SelectedDevice = Devices.FirstOrDefault(d => d.Index == di);
                else
                    SelectedDevice = Devices.FirstOrDefault();

                if (SelectedDevice is not null && previousZoneIndex is int zi)
                {
                    var match = Zones.FirstOrDefault(z => z.Index == zi);
                    if (match is not null)
                        SelectedZone = match;
                }

                ZoneLedCount = previousLedCount;
            }
            finally
            {
                _suppressZoneLedSync = false;
            }

            OnPropertyChanged(nameof(ZoneLimitsHint));
            RefreshVendorConflicts();
            RebuildEffectModeOptions(SelectedDevice);

            var baseStatus = Devices.Count == 0
                ? "Connected — no devices detected. Check OpenRGB device list / PawnIO / close vendor RGB apps."
                : $"Connected — {Devices.Count} device(s).";
            StatusText = string.IsNullOrWhiteSpace(VendorConflictWarning)
                ? baseStatus
                : baseStatus + " " + VendorConflictWarning;
        }
        catch (Exception ex)
        {
            IsConnected = _openRgb.IsConnected;
            StatusText = ex.Message;
            if (!IsConnected)
            {
                Devices.Clear();
                Zones.Clear();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyResizeAsync()
    {
        if (SelectedDevice is null)
        {
            StatusText = "Select a device first.";
            return;
        }

        if (SelectedZone is null)
        {
            StatusText = "Select a channel/zone first.";
            return;
        }

        if (ZoneLedCount < 0)
        {
            StatusText = "LED count must be >= 0.";
            return;
        }

        IsBusy = true;
        try
        {
            var deviceIndex = SelectedDevice.Index;
            var zoneIndex = SelectedZone.Index;
            var size = ZoneLedCount;
            var zoneName = SelectedZone.Name;
            await Task.Run(() => _openRgb.ResizeZone(deviceIndex, zoneIndex, size));
            StatusText = $"Applied size {size} LED(s) on \"{zoneName}\" ({SelectedDevice.Name}) via ResizeZone/ConfigureZone.";
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
            var speed = EffectSpeed / 100.0;
            var mode = SelectedEffectMode ?? "Static";
            var deviceIndex = SelectedDevice.Index;
            var deviceName = SelectedDevice.Name;

            RememberDeviceEffect(deviceName, color, mode, EffectSpeed, Brightness);

            if (SelectedZone is not null)
            {
                var zoneIndex = SelectedZone.Index;
                var zoneName = SelectedZone.Name;
                var ledCount = SelectedZone.LedCount;
                var desiredSize = ZoneLedCount > 0 ? ZoneLedCount : DefaultSuggestedLedCount;

                await Task.Run(() =>
                {
                    if (ledCount == 0)
                        _openRgb.ResizeZone(deviceIndex, zoneIndex, desiredSize);

                    _openRgb.ApplyEffectToZone(deviceIndex, zoneIndex, color, mode, speed, bright);
                });

                StatusText = AppendLastStatus(ledCount == 0
                    ? $"Resized \"{zoneName}\" to {desiredSize} then applied {mode} {color.ToHex()} @ {Brightness:0}% speed {EffectSpeed:0}."
                    : $"Applied {mode} {color.ToHex()} @ {Brightness:0}% (speed {EffectSpeed:0}) to zone \"{zoneName}\" on {deviceName}.");
            }
            else
            {
                await Task.Run(() => _openRgb.ApplyEffect(deviceIndex, color, mode, speed, bright));
                StatusText = AppendLastStatus(
                    $"Applied {mode} {color.ToHex()} @ {Brightness:0}% (speed {EffectSpeed:0}) to {deviceName}.");
            }

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
            var speed = EffectSpeed / 100.0;
            var mode = SelectedEffectMode ?? "Static";

            // Sync all = push current global color+mode+speed to every device.
            foreach (var d in Devices)
                RememberDeviceEffect(d.Name, color, mode, EffectSpeed, Brightness);

            await Task.Run(() => _openRgb.ApplyEffectToAll(color, mode, speed, bright));
            StatusText = AppendLastStatus(
                $"Synced {mode} {color.ToHex()} @ {Brightness:0}% (speed {EffectSpeed:0}) to all devices.");
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


    /// <summary>Tray: sync all using last profile if set, otherwise current picker color.</summary>
    [RelayCommand]
    private async Task TraySyncAllAsync()
    {
        if (!IsConnected)
        {
            await ConnectAsync();
            if (!IsConnected)
                return;
        }

        var name = _settings.LastProfileName ?? SelectedProfileName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var profile = _profiles.Load(name);
            if (profile is not null)
            {
                SelectedProfileName = profile.Name;
                ProfileName = profile.Name;
                await ApplyLoadedProfileAsync(profile, announcePrefix: "Tray sync");
                return;
            }
        }

        await ApplyToAllAsync();
    }

    [RelayCommand]
    private void TrayShow() => ShowWindowRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void TrayExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task TrayLoadProfileAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        SelectedProfileName = name;
        await LoadProfileAsync();
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
            PersistCurrentDeviceEffectState();
            var pick = CurrentColor();
            var globalMode = SelectedEffectMode ?? "Static";

            var profile = new ColorProfile
            {
                Name = ProfileName.Trim(),
                Brightness = Brightness / 100.0,
                ModeName = globalMode,
                Speed = EffectSpeed,
                Devices = Devices.Select(d =>
                {
                    if (_deviceEffects.TryGetValue(d.Name, out var state))
                    {
                        return new DeviceColorEntry
                        {
                            DeviceName = d.Name,
                            R = state.R,
                            G = state.G,
                            B = state.B,
                            ModeName = state.ModeName,
                            Speed = state.Speed,
                            Brightness = state.Brightness / 100.0
                        };
                    }

                    var c = d.CurrentColor ?? pick;
                    return new DeviceColorEntry
                    {
                        DeviceName = d.Name,
                        R = c.R,
                        G = c.G,
                        B = c.B,
                        ModeName = globalMode,
                        Speed = EffectSpeed,
                        Brightness = Brightness / 100.0
                    };
                }).ToList()
            };

            _profiles.Save(profile);
            RefreshProfileList();
            SelectedProfileName = profile.Name;
            _settings.LastProfileName = profile.Name;
            PersistSettings();
            StatusText = $"Saved profile \"{profile.Name}\" (per-device color/mode/speed) → {_profiles.DirectoryPath}";
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
        _settings.LastProfileName = profile.Name;
        PersistSettings();

        if (!IsConnected)
        {
            ApplyProfileToPicker(profile);
            StatusText = $"Loaded \"{profile.Name}\" into picker (not connected — connect to apply).";
            return;
        }

        await ApplyLoadedProfileAsync(profile, announcePrefix: "Loaded and applied");
    }

    private async Task ApplyLoadedProfileAsync(ColorProfile profile, string announcePrefix)
    {
        ApplyProfileToPicker(profile);

        IsBusy = true;
        try
        {
            var devices = await Task.Run(() => _openRgb.ListDevices());
            var profileBright = Math.Clamp(profile.Brightness, 0, 1);
            var profileSpeed = Math.Clamp((profile.Speed ?? EffectSpeed) / 100.0, 0, 1);
            var profileMode = profile.ModeName ?? SelectedEffectMode ?? "Static";

            await Task.Run(() =>
            {
                foreach (var entry in profile.Devices)
                {
                    var match = devices.FirstOrDefault(d =>
                        string.Equals(d.Name, entry.DeviceName, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                        continue;

                    var color = new RgbColor(entry.R, entry.G, entry.B);
                    var mode = string.IsNullOrWhiteSpace(entry.ModeName) ? profileMode : entry.ModeName!;
                    var speed = entry.Speed is double s
                        ? Math.Clamp(s / 100.0, 0, 1)
                        : profileSpeed;
                    var bright = entry.Brightness is double b
                        ? Math.Clamp(b, 0, 1)
                        : profileBright;

                    RememberDeviceEffect(match.Name, color, mode, speed * 100.0, bright * 100.0);
                    _openRgb.ApplyEffect(match.Index, color, mode, speed, bright);
                }
            });

            StatusText = AppendLastStatus($"{announcePrefix} profile \"{profile.Name}\" (per-device modes).");
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

    private void ApplyProfileToPicker(ColorProfile profile)
    {
        Brightness = Math.Clamp(profile.Brightness * 100.0, 0, 100);
        if (profile.Speed is double spd)
            EffectSpeed = Math.Clamp(spd, 0, 100);
        if (!string.IsNullOrWhiteSpace(profile.ModeName))
            SelectedEffectMode = profile.ModeName;

        if (profile.Devices.Count > 0)
        {
            var first = profile.Devices[0];
            Red = first.R;
            Green = first.G;
            Blue = first.B;
            if (!string.IsNullOrWhiteSpace(first.ModeName))
                SelectedEffectMode = first.ModeName;
            if (first.Speed is double s)
                EffectSpeed = Math.Clamp(s, 0, 100);
            if (first.Brightness is double b)
                Brightness = Math.Clamp(b * 100.0, 0, 100);

            foreach (var entry in profile.Devices)
            {
                RememberDeviceEffect(
                    entry.DeviceName,
                    new RgbColor(entry.R, entry.G, entry.B),
                    entry.ModeName ?? profile.ModeName ?? "Static",
                    entry.Speed ?? profile.Speed ?? EffectSpeed,
                    (entry.Brightness ?? profile.Brightness) * 100.0);
            }
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
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private string AppendLastStatus(string baseStatus)
    {
        var note = _openRgb.LastStatus;
        return string.IsNullOrWhiteSpace(note) ? baseStatus : $"{baseStatus} ({note})";
    }

    private RgbColor CurrentColor() => new((byte)Red, (byte)Green, (byte)Blue);

    private void RebuildEffectModeOptions(DeviceInfo? device)
    {
        var previous = SelectedEffectMode;
        var list = EffectModes.BuildPickerList(device?.Modes);
        EffectModeOptions.Clear();
        foreach (var name in list)
            EffectModeOptions.Add(name);

        if (!string.IsNullOrWhiteSpace(previous) &&
            EffectModeOptions.Any(n => string.Equals(n, previous, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedEffectMode = EffectModeOptions.First(n =>
                string.Equals(n, previous, StringComparison.OrdinalIgnoreCase));
        }
        else if (device?.ActiveModeName is string active &&
                 EffectModeOptions.Any(n => string.Equals(n, active, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedEffectMode = EffectModeOptions.First(n =>
                string.Equals(n, active, StringComparison.OrdinalIgnoreCase));
        }
        else if (EffectModeOptions.Count > 0 &&
                 (SelectedEffectMode is null ||
                  !EffectModeOptions.Any(n => string.Equals(n, SelectedEffectMode, StringComparison.OrdinalIgnoreCase))))
        {
            SelectedEffectMode = EffectModeOptions.Contains("Static")
                ? "Static"
                : EffectModeOptions[0];
        }

        UpdateEffectSpeedEnabled();
    }

    private void UpdateEffectSpeedEnabled()
    {
        var modeName = SelectedEffectMode ?? "Static";
        // Off has no meaningful speed; Static often ignores it — still allow slider for CM Breathing etc.
        if (modeName.Equals("Off", StringComparison.OrdinalIgnoreCase))
        {
            EffectSpeedEnabled = false;
            return;
        }

        if (SelectedDevice?.Modes is { Count: > 0 } modes)
        {
            var match = modes.FirstOrDefault(m =>
                string.Equals(m.Name, modeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                EffectSpeedEnabled = match.SupportsSpeed ||
                    CmArgbGen2HidController.IsCoolerMasterArgbGen2(SelectedDevice.Name);
                return;
            }
        }

        // Curated hardware-style modes / CM: enable speed except Off.
        EffectSpeedEnabled = true;
    }

    private void PersistCurrentDeviceEffectState()
    {
        if (string.IsNullOrWhiteSpace(_effectStateDeviceKey))
            return;

        RememberDeviceEffect(
            _effectStateDeviceKey,
            CurrentColor(),
            SelectedEffectMode ?? "Static",
            EffectSpeed,
            Brightness);
    }

    private void LoadDeviceEffectState(DeviceInfo? device)
    {
        _effectStateDeviceKey = device?.Name;

        if (device is null)
            return;

        _suppressDeviceEffectSync = true;
        try
        {
            if (_deviceEffects.TryGetValue(device.Name, out var state))
            {
                Red = state.R;
                Green = state.G;
                Blue = state.B;
                EffectSpeed = state.Speed;
                Brightness = state.Brightness;
                if (!string.IsNullOrWhiteSpace(state.ModeName) &&
                    EffectModeOptions.Any(n => string.Equals(n, state.ModeName, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedEffectMode = EffectModeOptions.First(n =>
                        string.Equals(n, state.ModeName, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    SelectedEffectMode = state.ModeName;
                }
            }
            else if (device.CurrentColor is RgbColor c)
            {
                Red = c.R;
                Green = c.G;
                Blue = c.B;
                if (!string.IsNullOrWhiteSpace(device.ActiveModeName) &&
                    EffectModeOptions.Any(n => string.Equals(n, device.ActiveModeName, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedEffectMode = EffectModeOptions.First(n =>
                        string.Equals(n, device.ActiveModeName, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        finally
        {
            _suppressDeviceEffectSync = false;
            UpdateEffectSpeedEnabled();
        }
    }

    private void RememberDeviceEffect(
        string deviceName,
        RgbColor color,
        string modeName,
        double speed0to100,
        double brightness0to100)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return;

        _deviceEffects[deviceName] = new DeviceEffectState
        {
            R = color.R,
            G = color.G,
            B = color.B,
            ModeName = string.IsNullOrWhiteSpace(modeName) ? "Static" : modeName,
            Speed = Math.Clamp(speed0to100, 0, 100),
            Brightness = Math.Clamp(brightness0to100, 0, 100)
        };
    }

    public void Dispose()
    {
        try { _startupCts?.Cancel(); } catch { /* ignore */ }
        _startupCts?.Dispose();
        PersistSettings();
        _openRgb.ConnectionChanged -= OnConnectionChanged;
        _openRgb.Dispose();
    }
}
