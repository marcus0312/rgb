# Unified RGB

Windows desktop RGB controller for Marcus Lee. Talks **only** to a local [OpenRGB](https://openrgb.org/) SDK server via **OpenRGB.NET** (+ CM Gen2 HID Static / protocol-6 backends) — no vendor RGB apps, no proprietary DLLs.

**Phase 3:** installable win-x64 build, Start with Windows, system tray, settings persistence, vendor-conflict warnings.

## Prerequisites (Windows PC)

1. **OpenRGB 1.0+** with **SDK Server** enabled (default `127.0.0.1:6742`).
2. **PawnIO** (or equivalent OpenRGB kernel helper) when devices need SMBus/I²C (GPU / some motherboard paths).
3. Close conflicting vendor software (see [Vendor conflict runbook](#vendor-conflict-runbook)).
4. **.NET 8 SDK** only if you build from source; the published self-contained build does not need a machine-wide runtime.

This client does **not** install OpenRGB and does **not** require Administrator rights itself.

## Marcus’s devices (expected)

| Gear | Notes |
|------|--------|
| GALAX RTX 2070 Super | GPU SMBus RGB via OpenRGB Galax detector; needs PawnIO. Backend: OpenRGB **protocol 6** Direct + `UpdateLeds` (unique ID). |
| Cooler Master ARGB Gen2 A1 V2 | USB HID (VID `0x2516` / PID `0x01C9`); quit MasterPlus+ first. Channels often report **0 LEDs** — use **ConfigureZone** for size. **Solid color** uses raw **HID Static** only (not OpenRGB). |
| ASRock B450 Steel Legend | Polychrome (SMBus or USB). No Direct mode — Static is per-LED (`color_mode` PER_LED). Backend: OpenRGB **protocol 6** Static + `UpdateLeds` (unique ID). |
| Logitech G502 | Mouse in OpenRGB. **Quit G HUB** first. Backend: OpenRGB **protocol 6** Direct + `UpdateLeds` (unique ID). |

Exact OpenRGB names vary; use **Refresh** after connecting.

## Solution layout

```
marcus0312-rgb/
  UnifiedRgb.sln
  src/UnifiedRgb.Core/     # OpenRGB, CM HID, profiles, settings, autostart, vendor checks
  src/UnifiedRgb.App/      # Avalonia desktop UI + tray
  installer/               # PowerShell Install / Uninstall
  scripts/publish-win-x64.sh
  dist/                    # publish output (after script)
```

## How to run (dev)

```bash
cd marcus0312-rgb
dotnet restore
dotnet build
dotnet run --project src/UnifiedRgb.App
```

On Windows, start OpenRGB → **SDK Server** → **Start Server**, then click **Connect** (defaults `127.0.0.1:6742`). On launch the app also **retries connect** automatically and can **auto-apply the last profile**.

### Connect notes

- Host/port are configurable in the UI and persisted in settings JSON.
- If the server is down, status shows a clear error and the device list clears.
- After connect, **Refresh** reloads controllers (name, zones, LED counts, active mode).

## Install (Windows)

Preferred path from this Linux CI/box: **self-contained win-x64 publish + PowerShell installer** (no Inno/Velopack cross-compile).

### 1. Publish (Linux or Windows)

```bash
./scripts/publish-win-x64.sh
```

Produces:

| Artifact | Purpose |
|----------|---------|
| `dist/win-x64/` | Self-contained folder with `UnifiedRgb.App.exe` |
| `dist/UnifiedRgb-win-x64.zip` | Zip of publish + install scripts |
| `dist/Install-UnifiedRgb.ps1` | Copied installer for convenience |

### 2. Install on the Windows PC

Copy the zip (or `dist/` folder) to the PC, then in PowerShell:

```powershell
Expand-Archive .\UnifiedRgb-win-x64.zip -DestinationPath .\UnifiedRgb-setup
cd .\UnifiedRgb-setup
powershell -ExecutionPolicy Bypass -File .\Install-UnifiedRgb.ps1 -SourceDir .\win-x64
```

What the installer does:

- Copies to `%LocalAppData%\Programs\UnifiedRgb\`
- Creates Start Menu shortcut **Unified RGB**
- Registers **HKCU** uninstall entry + `Uninstall.ps1`
- Registers **Start with Windows** via `HKCU\...\Run\UnifiedRgb` (default ON; pass `-StartWithWindows:$false` to skip)

Uninstall:

```powershell
powershell -ExecutionPolicy Bypass -File "%LocalAppData%\Programs\UnifiedRgb\Uninstall.ps1"
```

Profiles/settings under `%LocalAppData%\UnifiedRgb\` are kept.

### Optional: Inno Setup / Velopack (Windows-only)

If you want a classic `.exe` setup later, run the same `dotnet publish -r win-x64 --self-contained` on Windows and point Inno Setup / Velopack at `dist/win-x64`. This repo ships the zip+PowerShell path because it builds cleanly from Linux.

## Auto-launch on login

- UI checkbox **Start with Windows** (default **ON** for installed builds under `LocalAppData\Programs\UnifiedRgb` or when `installed.marker` is present).
- Implementation: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `UnifiedRgb` → quoted path to `UnifiedRgb.App.exe` (adds `--minimized` when **Start minimized** is on).
- On startup the app waits/retries OpenRGB at the configured host/port (default `127.0.0.1:6742`), then **auto-applies the last-used profile** when **Auto-apply last profile on launch** is enabled.

## System tray

- Avalonia 11 built-in `TrayIcon` (Windows).
- **Close to tray** (default ON): closing the window hides to tray instead of exiting.
- **Start minimized**: starts in tray (also via `--minimized` CLI flag used by autostart).
- Tray menu: **Show**, **Sync all** (last profile if set, else current color), **Load profile** submenu, **Exit**.

## Settings

Persisted at `%LocalAppData%/UnifiedRgb/settings.json`:

| Key | Meaning |
|-----|---------|
| `host` / `port` | OpenRGB SDK endpoint |
| `startWithWindows` | HKCU Run registration |
| `startMinimized` | Launch hidden to tray |
| `closeToTray` | Close button → tray |
| `lastProfileName` | Last saved/loaded/applied profile |
| `autoApplyOnLaunch` | After connect on startup, apply last profile |

Profiles remain under `%LocalAppData%/UnifiedRgb/profiles/`.

## Vendor conflict runbook

Vendor RGB suites often hold exclusive access to the same USB/SMBus devices OpenRGB needs. **Quit them before connecting** (or leave them closed at login if you rely on autostart).

| Process / product | Why it conflicts |
|-------------------|------------------|
| **Logitech G HUB** (`lghub`, agents) | Fights OpenRGB for G502 / Logitech HID |
| **Cooler Master MasterPlus+** (`MasterPlus`, `MPIV`) | Claims CM ARGB Gen2 hub |
| **ASRock Polychrome Sync** (`Polychrome*`) | Claims motherboard RGB |
| **GALAX / KFA2 Xtreme Tuner** / GALAX RGB | Claims GALAX GPU RGB |

**Checklist**

1. Exit G HUB from its tray icon (not just close the window).
2. Exit MasterPlus+, Polychrome Sync, Xtreme Tuner / GALAX RGB the same way.
3. Start **OpenRGB** → enable **SDK Server** on `127.0.0.1:6742`.
4. Optional: run OpenRGB as a Windows service / scheduled task so it is up before Unified RGB autostart (Unified RGB will retry for ~30s either way).
5. Launch **Unified RGB** — if a conflict is still running, a **non-blocking amber banner** and status text warn you (devices may be missing until you quit the vendor app and **Refresh**).

This app never kills vendor processes for you; it only detects and warns.

## Features

1. Connect to OpenRGB SDK (manual + startup retry)  
2. List devices (name, zones, LED counts, mode)  
3. **Channel/zone picker** + **LED count** + **Apply size** (ResizeZone / ConfigureZone for CM Gen2)  
4. Color picker → apply solid color to **selected** device/zone or **sync all** (CM Gen2 → Windows **HID Static**; ASRock/GALAX/G502 → OpenRGB **protocol 6** unique-ID `UpdateLeds`)  
5. When applying to a selected zone with `LedCount == 0`, auto-applies size (default **24**) then zone LEDs  
6. Brightness: client-side RGB scaling  
7. Save / load / delete named profiles (JSON)  
8. **Install + Start with Windows + tray + settings + vendor warnings** (Phase 3)

Out of scope (this PR): music sync, full effect engine canvas, bundling/installing OpenRGB, fan curves.

## Cooler Master ARGB Gen2 (size + color)

### LED count — ConfigureZone

OpenRGB's UI **does not show Edit Zone** for Cooler Master ARGB Gen2 A1 V2: the driver never sets `ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE`, so `ResizeZone` is a no-op. This app talks **ConfigureZone** (`NET_PACKET_ID_RGBCONTROLLER_CONFIGUREZONE = 1003`, protocol 6) over a short-lived TCP connection (OpenRGB.NET 3.1.1 only speaks protocol 4 and has no ConfigureZone API). Payload `data_size` is the full packet length, including itself; flags include `ZONE_FLAG_MANUALLY_CONFIGURED_SIZE` (1<<12) and `ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE` (1<<1).

1. Select the CM device in the list.  
2. Pick the channel/zone that matches the strip (often still `0` LEDs).  
3. Set **LED count** to **24** (or your strip length) and click **Apply size**, *or* just **Apply to selected** — it will apply size automatically when the zone is empty.

### Solid color — HID Static (not OpenRGB mode alone)

OpenRGB `UpdateMode` / Direct often **ACKs OK** but fans stay on Spectrum/rainbow on this hub. When the device name contains **"Cooler Master ARGB"**, **Apply** / **Apply to all** / profile load drive color via raw USB **HID Static** on Windows (`CmArgbGen2HidController`, HidSharp):

- VID `0x2516` PID `0x01C9`, packet length 65, report id byte0 = 0  
- Prefer HID interface **0 or 1** (skip MI_02 mouse)  
- Sequence: `LIGHTNING_CONTROL` → `HW_MODE_SETUP` Static with RGB (+ brightness) → optional `APPLY_CHANGES`  

**Important:** CM Gen2 color is **HID-only** — do **not** follow HID Static with OpenRGB `Direct`/`SetCustomMode`/`UpdateLeds` on this device. Gen2 `SetupDirectMode()` resets the hub and blacks LEDs, which looks like a ~1s flash-then-revert after Apply.

Other devices use **per-device OpenRGB protocol-6 backends** (not CM HID): mode enter best-effort via OpenRGB.NET, then colors via raw TCP `UpdateLeds` / `UpdateZoneLeds` with **unique controller IDs**. Per-LED Direct channel colors on CM Gen2 remain future work.

## Per-device Apply backends

| Device | Backend | Notes |
|--------|---------|--------|
| Cooler Master ARGB Gen2 | Windows **HID Static** only | Never follow with OpenRGB Direct/`UpdateLeds` |
| ASRock Polychrome | OpenRGB protocol 6 | No Direct; Static is PER_LED — `UpdateLeds` all zone LEDs |
| GALAX GPU | OpenRGB protocol 6 | Direct + `UpdateLeds` (often 1 LED) |
| Logitech G502 | OpenRGB protocol 6 | Direct + `UpdateLeds`; **quit G HUB** |

OpenRGB.NET remains used for device listing and best-effort mode enter. Color writes for non-CM devices go through `OpenRgbProtocol6Client`.

## Packages

| Package | Version | Role |
|---------|---------|------|
| OpenRGB.NET | 3.1.1 | OpenRGB SDK client |
| HidSharp | 2.1.0 | Windows HID Static for Cooler Master ARGB Gen2 |
| Microsoft.Win32.Registry | 5.0.0 | HKCU Run autostart (Windows) |
| Avalonia (+ Desktop, Fluent, Inter) | 11.2.5 | Desktop UI + built-in TrayIcon |
| CommunityToolkit.Mvvm | 8.x | MVVM helpers |

## OpenRGB.NET API quirks

- Namespace types live in `OpenRGB.NET` (`OpenRgbClient`, `Color`, `Device`, …) — not a separate `Models` namespace in 3.1.1.
- Prefer `autoConnect: false`, then `Connect()`, so connection failures surface cleanly.
- Solid color flow: for **Cooler Master ARGB Gen2** on Windows, **HID Static only** (optionally sent twice ~70ms apart) — never OpenRGB Direct afterward. For **ASRock / GALAX / G502**: enter Direct/Custom/Static best-effort via OpenRGB.NET, then **always** apply colors with protocol-6 unique-ID `UpdateLeds` / `UpdateZoneLeds` (OpenRGB.NET 3.1.1 protocol 4 ordinals are ignored / mis-target on protocol-6 servers). Do not rely on OpenRGB mode API alone to leave Spectrum on CM Gen2.
- **Protocol 6 unique IDs:** OpenRGB 1.0 SDK returns controller unique IDs (e.g. `[4,5,6,7]`) from `REQUEST_CONTROLLER_COUNT`. This app maps OpenRGB.NET ordinal indices → unique IDs (same list order, preferring name match via `REQUEST_CONTROLLER_DATA`) and sends `UPDATELEDS` (1050) with `pkt_dev_id` = unique ID and `data_size` equal to the full payload.
- ARGB hubs: call `ResizeZone(deviceId, zoneId, size)` before LEDs exist; zone exposes `LedsMin` / `LedsMax`.
- **CM Gen2 / missing Edit Zone:** `ResizeZone` is a no-op without `ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE`. OpenRGB.NET 3.1.1 max protocol is **4** (`CommandId` has no 1003; `OpenRgbConnection.Send` is internal). This app therefore uses a dedicated raw TCP path that negotiates protocol 6 and sends ConfigureZone; it does **not** inject packets into OpenRGB.NET’s socket (the server would parse Zone Data as protocol 4, dropping flags).
- **Brightness:** `Mode.SupportsBrightness` / `SetBrightness` exist, but `UpdateMode` **re-fetches** the device and only applies optional `speed` / `direction` / `colors`. There is **no brightness parameter**, so hardware brightness cannot be set through the public API. This app scales RGB client-side instead.
- Server-side OpenRGB profiles (`SaveProfile` / `LoadProfile`) are separate from this app’s on-disk JSON profiles.

## License / ownership

Personal MVP for Marcus Lee. OpenRGB and OpenRGB.NET are third-party projects under their own licenses.
