# Unified RGB

Windows desktop MVP for Marcus Lee’s unified RGB controller. Talks **only** to a local [OpenRGB](https://openrgb.org/) SDK server via **OpenRGB.NET** — no vendor RGB apps, no proprietary DLLs.

## Prerequisites (Windows PC)

1. **OpenRGB 1.0+** with **SDK Server** enabled (default `127.0.0.1:6742`).
2. **PawnIO** (or equivalent OpenRGB kernel helper) installed when devices need SMBus/I²C (GPU / some motherboard paths). OpenRGB’s installer/docs cover this.
3. Close or disable conflicting vendor software so OpenRGB can claim devices:
   - GALAX / KFA2 Xtreme Tuner  
   - Cooler Master MasterPlus+  
   - ASRock Polychrome Sync  
4. **.NET 8 SDK** (or run a published build).

This client does **not** install OpenRGB and does **not** require Administrator rights itself.

## Marcus’s devices (expected)

| Gear | Notes |
|------|--------|
| GALAX RTX 2070 Super | GPU SMBus RGB via OpenRGB Galax detector; needs PawnIO |
| Cooler Master ARGB Gen2 A1 V2 | USB HID; quit MasterPlus+ first |
| ASRock B450 Steel Legend | Polychrome (SMBus or USB depending on board revision) |
| Logitech G502 | Often detected as a mouse device in OpenRGB |

Exact OpenRGB names vary; use **Refresh** after connecting.

## Solution layout

```
marcus0312-rgb/
  UnifiedRgb.sln
  src/UnifiedRgb.Core/     # OpenRGB service + JSON profiles
  src/UnifiedRgb.App/      # Avalonia desktop UI
```

## How to run

```bash
cd marcus0312-rgb
dotnet restore
dotnet build
dotnet run --project src/UnifiedRgb.App
```

On Windows, start OpenRGB → **SDK Server** → **Start Server**, then click **Connect** in the app (defaults `127.0.0.1:6742`).

### Connect notes

- Host/port are configurable in the UI.
- If the server is down, status shows a clear error and the device list clears.
- After connect, **Refresh** reloads controllers (name, zones, LED counts, active mode).

## Features (MVP)

1. Connect to OpenRGB SDK  
2. List devices (name, zones, LED counts, mode)  
3. Color picker → apply solid color to **selected** device or **sync all**  
4. Brightness: client-side RGB scaling (see API quirk below)  
5. Save / load / delete named profiles as JSON under  
   `%LocalAppData%/UnifiedRgb/profiles/`  
6. Clear connection status / errors when the server is unavailable  

Out of scope: music sync, effect engines, hardware reverse engineering, installing OpenRGB.

## Packages

| Package | Version | Role |
|---------|---------|------|
| OpenRGB.NET | 3.1.1 | OpenRGB SDK client ([nuget.org](https://www.nuget.org/packages/OpenRGB.NET)) |
| Avalonia (+ Desktop, Fluent, Inter) | 11.2.5 | Cross-platform desktop UI (pinned to 11.x for .NET 8 SDK Roslyn) |
| CommunityToolkit.Mvvm | 8.x | MVVM helpers |

## OpenRGB.NET API quirks

- Namespace types live in `OpenRGB.NET` (`OpenRgbClient`, `Color`, `Device`, …) — not a separate `Models` namespace in 3.1.1.
- Prefer `autoConnect: false`, then `Connect()`, so connection failures surface cleanly.
- Solid color flow: best-effort `SetCustomMode(deviceId)` (or Direct/Custom/Static mode), then `UpdateLeds(deviceId, colors)`.
- **Brightness:** `Mode.SupportsBrightness` / `SetBrightness` exist, but `UpdateMode` **re-fetches** the device and only applies optional `speed` / `direction` / `colors`. There is **no brightness parameter**, so hardware brightness cannot be set through the public API. This app scales RGB client-side instead.
- Server-side OpenRGB profiles (`SaveProfile` / `LoadProfile`) are separate from this app’s on-disk JSON profiles.

## License / ownership

Personal MVP for Marcus Lee. OpenRGB and OpenRGB.NET are third-party projects under their own licenses.
