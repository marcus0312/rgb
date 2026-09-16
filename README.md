# Unified RGB

Windows desktop RGB controller for Marcus Lee. Talks **only** to a local [OpenRGB](https://openrgb.org/) SDK server via **OpenRGB.NET** (+ CM Gen2 HID hardware modes / protocol-6 backends) — effect modes, speed, per-device colors, no vendor RGB apps, no proprietary DLLs.

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
| Cooler Master ARGB Gen2 A1 V2 | USB HID (VID `0x2516` / PID `0x01C9`); quit MasterPlus+ first. Channels often report **0 LEDs** — use **ConfigureZone** for size. **Effects** use raw **HID HW_MODE_SETUP** (Static/Spectrum/Rainbow/Breathing/Off/…) — never OpenRGB Direct follow-up. |
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
4. Color + **effect mode** + **speed** → apply to **selected** device/zone or **sync all** (CM Gen2 → Windows **HID hardware modes**; ASRock/GALAX/G502 → OpenRGB **protocol 6** `UpdateMode` + `UpdateLeds` when needed)  
5. When applying to a selected zone with `LedCount == 0`, auto-applies size (default **24**) then zone LEDs  
6. Brightness: client-side RGB scaling  
7. Save / load / delete named profiles (JSON) with **per-device** color/mode/speed (backward compatible with older solid-only profiles)  
8. **Install + Start with Windows + tray + settings + vendor warnings** (Phase 3)

Out of scope: music sync, pixel canvas / custom sequences, bundling/installing OpenRGB, fan curves.

## Cooler Master ARGB Gen2 (size + color)

### LED count — ConfigureZone

OpenRGB's UI **does not show Edit Zone** for Cooler Master ARGB Gen2 A1 V2: the driver never sets `ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE`, so `ResizeZone` is a no-op. This app talks **ConfigureZone** (`NET_PACKET_ID_RGBCONTROLLER_CONFIGUREZONE = 1003`, protocol 6) over a short-lived TCP connection (OpenRGB.NET 3.1.1 only speaks protocol 4 and has no ConfigureZone API). Payload `data_size` is the full packet length, including itself; flags include `ZONE_FLAG_MANUALLY_CONFIGURED_SIZE` (1<<12) and `ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE` (1<<1).

1. Select the CM device in the list.  
2. Pick the channel/zone that matches the strip (often still `0` LEDs).  
3. Set **LED count** to **24** (or your strip length) and click **Apply size**, *or* just **Apply to selected** — it will apply size automatically when the zone is empty.

### Effects — HID hardware modes (not OpenRGB Direct)

OpenRGB `UpdateMode` / Direct often **ACKs OK** but leaves Spectrum/rainbow on this hub. When the device name contains **"Cooler Master ARGB"**, **Apply** / **Sync all** / profile load drive effects via raw USB HID on Windows (`CmArgbGen2HidController`, HidSharp):

- VID `0x2516` PID `0x01C9`, packet length 65, report id byte0 = 0  
- Prefer HID interface **0 or 1** (skip MI_02 mouse)  
- Sequence: `LIGHTNING_CONTROL` → `HW_MODE_SETUP` (mode + speed + brightness + RGB) → optional `APPLY_CHANGES`  

Mode bytes from OpenRGB `CMARGBGen2A1Controller.h`:

| UI / OpenRGB name | HID byte |
|-------------------|----------|
| Spectrum | `0x00` |
| Static (and Direct/Custom → solid) | `0x01` |
| Reload | `0x02` |
| Recoil | `0x03` |
| Breathing | `0x04` |
| Refill | `0x05` |
| Demo | `0x06` |
| Fill Flow | `0x07` |
| Rainbow | `0x08` |
| Off | `0x09` |

Speed slider **0–100** maps to **5 shared Sync tiers** (0=slow … 4=fast) → HID speed `0x00`–`0x04` (mid ≈ `0x02`). Brightness maps to `0x00`–`0xFF`. See [Speed sync tiers](#speed-sync-tiers).

**Important:** CM Gen2 effects are **HID-only** — do **not** follow with OpenRGB `Direct`/`SetCustomMode`/`UpdateLeds` on this device. Gen2 `SetupDirectMode()` resets the hub and blacks LEDs (~1s flash-then-revert).

Other devices use **OpenRGB protocol-6** `UpdateMode` (unique IDs, with speed + mode-specific colors when needed), then `UpdateLeds` / `UpdateZoneLeds` for Direct or per-LED Static.

## Per-device Apply backends

| Device | Backend | Notes |
|--------|---------|--------|
| Cooler Master ARGB Gen2 | Windows **HID HW_MODE_SETUP** | Static/Spectrum/Rainbow/Breathing/Off/… — never OpenRGB Direct follow-up |
| ASRock Polychrome | OpenRGB protocol 6 | No Direct; Static is PER_LED — `UpdateLeds` all zone LEDs |
| GALAX GPU | OpenRGB protocol 6 | Direct + `UpdateLeds` (often 1 LED) |
| Logitech G502 | OpenRGB protocol 6 | Direct + `UpdateLeds`; **quit G HUB** |

OpenRGB.NET remains used for device listing and best-effort mode enter. Color writes for non-CM devices go through `OpenRgbProtocol6Client`.

## Effect modes + speed

Main window **Effect** combo + **Speed** slider (0–100) + **RGB NumericUpDown** / live **hex** (`#RGB` / `#RRGGBB`):

- Always offers a curated list: Static, Direct, Breathing, Spectrum, Rainbow, Off, Demo, Reload, Recoil, Refill, Fill Flow, Custom.
- When a device is selected, also lists every mode OpenRGB reports for that controller.
- **Apply to selected** / **Sync all** send **mode + color + speed** (not solid `UpdateLeds` only).
- Status line reports which backend ran (e.g. `HID Breathing (mode=0x04, …)` or `proto6 UpdateMode(Spectrum) + proto6 UpdateLeds(n)`), and Sync all lists per-device mode (including closest-mode fallbacks).

### Speed sync tiers

**Sync all matches shared speed tiers, not perfect hardware clocks.** UI 0–100 → discrete tiers 0–4 via `EffectSpeedSync`:

| UI (approx) | Tier | CM HID byte | OpenRGB mapping |
|-------------|------|-------------|-----------------|
| 0–12 | 0 (slow) | `0x00` | `SpeedMin` (slow end) |
| 13–37 | 1 | `0x01` | 25% toward `SpeedMax` |
| 38–62 | 2 (mid) | `0x02` | midpoint |
| 63–87 | 3 | `0x03` | 75% toward `SpeedMax` |
| 88–100 | 4 (fast) | `0x04` | `SpeedMax` (fast end) |

- **CM Gen2 (OpenRGB CMARGBGen2A1):** `SPEED_MIN=0x00` … `SPEED_MAX=0x04` — **higher byte = faster**.
- **OpenRGB devices (ASRock/GALAX/G502):** map tier into each mode’s `SpeedMin`→`SpeedMax` **without swapping**. ASRock Polychrome USB uses inverted ranges (`SpeedMin=0xFF` slow, `SpeedMax=0x00` fast) — lower byte = faster; we preserve that.
- **Sync all** skips the usual 50ms inter-device delay so Breathing starts closer in phase, then optionally **re-asserts** the same mode+speed in a second pass. Devices missing the requested mode get the **closest** related effect (or Static/Direct); status lists who got what.
- Hardware effects (Breathing, Spectrum, …) do **not** get an `UpdateLeds` / Direct follow-up (`ModeWantsPerLedFollowUp`).

### Non-CM backend

1. Resolve the named mode on the device (exact, contains, then closest fallback).  
2. `OpenRgbProtocol6Client.UpdateMode` with protocol-6 unique ID and Mode Data (speed from shared tiers → `speed_min`/`speed_max`; mode-specific colors filled when required).  
3. If the mode is Direct / Custom / per-LED Static → `UpdateLeds` / `UpdateZoneLeds` with the solid color.  
4. Pure hardware effects (Breathing, Spectrum, …) stop after `UpdateMode`.

### Per-device colors

- Selecting a device restores that device’s remembered color / mode / speed (edited independently).  
- **Sync all** pushes the **current global** color+mode+speed to every device and updates each remembered entry.  
- Profiles store per-device entries (see below).

## Profiles (per-device modes)

JSON under `%LocalAppData%/UnifiedRgb/profiles/`. Shape:

```json
{
  "name": "Desk",
  "brightness": 1.0,
  "modeName": "Static",
  "speed": 50,
  "devices": [
    {
      "deviceName": "Cooler Master ARGB Controller Gen 2 A1 V2",
      "r": 255, "g": 0, "b": 40,
      "modeName": "Breathing",
      "speed": 70,
      "brightness": 1.0
    },
    {
      "deviceName": "ASRock Polychrome USB",
      "r": 0, "g": 120, "b": 255,
      "modeName": "Static",
      "speed": 50
    }
  ]
}
```

- **Backward compatible:** older profiles with only `r/g/b` (+ profile `brightness`) still load; missing `modeName` → Static, missing `speed` → UI/default.  
- **Load / auto-apply** matches by device name and applies each entry with the correct backend (CM HID vs protocol-6).

## Packages

| Package | Version | Role |
|---------|---------|------|
| OpenRGB.NET | 3.1.1 | OpenRGB SDK client |
| HidSharp | 2.1.0 | Windows HID hardware modes for Cooler Master ARGB Gen2 |
| Microsoft.Win32.Registry | 5.0.0 | HKCU Run autostart (Windows) |
| Avalonia (+ Desktop, Fluent, Inter) | 11.2.5 | Desktop UI + built-in TrayIcon |
| CommunityToolkit.Mvvm | 8.x | MVVM helpers |

## OpenRGB.NET API quirks

- Namespace types live in `OpenRGB.NET` (`OpenRgbClient`, `Color`, `Device`, …) — not a separate `Models` namespace in 3.1.1.
- Prefer `autoConnect: false`, then `Connect()`, so connection failures surface cleanly.
- Effect flow: for **Cooler Master ARGB Gen2** on Windows, **HID HW_MODE_SETUP** only (Static optionally sent twice ~70ms apart) — never OpenRGB Direct afterward. For **ASRock / GALAX / G502**: protocol-6 unique-ID `UpdateMode` (shared speed tiers + mode colors), then `UpdateLeds` / `UpdateZoneLeds` when the mode is Direct or per-LED Static. Sync all uses minimal inter-device delay + optional speed re-assert. Do not rely on OpenRGB mode API alone for CM Gen2.
- **Protocol 6 unique IDs:** OpenRGB 1.0 SDK returns controller unique IDs (e.g. `[4,5,6,7]`) from `REQUEST_CONTROLLER_COUNT`. This app maps OpenRGB.NET ordinal indices → unique IDs (same list order, preferring name match via `REQUEST_CONTROLLER_DATA`) and sends `UPDATELEDS` (1050) with `pkt_dev_id` = unique ID and `data_size` equal to the full payload.
- ARGB hubs: call `ResizeZone(deviceId, zoneId, size)` before LEDs exist; zone exposes `LedsMin` / `LedsMax`.
- **CM Gen2 / missing Edit Zone:** `ResizeZone` is a no-op without `ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE`. OpenRGB.NET 3.1.1 max protocol is **4** (`CommandId` has no 1003; `OpenRgbConnection.Send` is internal). This app therefore uses a dedicated raw TCP path that negotiates protocol 6 and sends ConfigureZone; it does **not** inject packets into OpenRGB.NET’s socket (the server would parse Zone Data as protocol 4, dropping flags).
- **Brightness:** `Mode.SupportsBrightness` / `SetBrightness` exist, but `UpdateMode` **re-fetches** the device and only applies optional `speed` / `direction` / `colors`. There is **no brightness parameter**, so hardware brightness cannot be set through the public API. This app scales RGB client-side instead.
- Server-side OpenRGB profiles (`SaveProfile` / `LoadProfile`) are separate from this app’s on-disk JSON profiles.

## License / ownership

Personal MVP for Marcus Lee. OpenRGB and OpenRGB.NET are third-party projects under their own licenses.
