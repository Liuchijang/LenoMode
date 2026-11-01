# LenoMode

Switch the BIOS **Thermal mode** of a Lenovo ThinkBook from Windows and watch what it does while gaming.
UI in the NotiGuard / Nothing OS style (monochrome, dot matrix, red accent), English or Vietnamese.

![LenoMode main window](docs/Screenshot.png)

## Download

Get `LenoMode.exe` from [Releases](https://github.com/Liuchijang/LenoMode/releases) and run it (no install;
needs .NET Framework 4.8, built into Windows 10/11). Made for and tested on a ThinkBook 14 G6+ AHP only.

## Features

- **Thermal mode**: Quiet / Balanced / Performance = Lenovo ITS Intelligent Cooling / Battery Saving / Extreme
  Performance, switched live through the Lenovo energy driver (DYTC, same path as Fn+Q / Vantage) and read back.
  The BIOS `SystemPerformanceMode` value only applies at the next boot; it is shown and can be saved as boot default.
- **Windows power mode**: the Settings slider (power overlay), for the current power source only.
- **FPS**: frames per process from the DXGI / D3D9 Present ETW events (same source as PresentMon), no extra
  tool. FPS over 2 s, 1% low over 10 s, frame time. DirectX games only (Vulkan/OpenGL not seen).
- **Overlay**: small always-on-top, click-through panel. Pick the fields (mode, FPS, 1% low/frame time, GPU, CPU clock,
  temp, power, RAM, VRAM), size S/M/L, background 40/70/90 %, panel or one-line bar, a corner preset or
  drag it anywhere (DRAG… → drag → DONE). Not visible over exclusive fullscreen.
- **Layout**: thermal mode, power mode, live monitor, mode comparison, then the settings (in-game overlay,
  app settings) at the bottom. Live monitor, mode comparison and overlay start collapsed
  (click the header to open); collapsed live monitor still shows FPS, CPU clock and temperature.
- **App settings** (bottom): start with Windows, restart as admin.
- **Hotkeys**: Ctrl+Alt+1/2/3 thermal mode, Ctrl+Alt+O overlay, working inside the game.
- **Live** (1 s): GPU % of the top GPU app (the game), total 3D GPU %, CPU load, effective CPU clock and
  throttling, ACPI temperature, system power (battery only), RAM, GPU memory. Dot-matrix history per metric.
- **Session**: REC averages per thermal mode; AUTO TEST runs all 3 modes (10 s warm-up + 30/60/120 s each)
  and restores the original mode; CPU LOAD: ON adds a fixed all-core load during the test (compare system power
  per mode on battery); the CSV is saved automatically when the test finishes; EXPORT CSV to `Documents\LenoMode`.

Limits: no GPU/CPU package power (the iGPU exposes no power sensor to Windows), and
the temperature is the ACPI zone, not the CPU die.

## Build

No SDK needed, uses the C# 5 compiler that ships with .NET Framework 4.8:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1   # -> bin\LenoMode.exe
```

Runs without admin (no UAC). Only the temperature tile needs admin: use RESTART AS ADMIN. Saving the BIOS boot
default asks for UAC once. START WITH WINDOWS adds an HKCU Run entry that starts it hidden in the tray; closing the
window hides it to the tray (Exit is in the tray menu).
`assets\make-icon.ps1` regenerates the icon.

## Layout

- `src/Its.cs`: live thermal mode via EnergyDrv DYTC, `Thermal` facade
- `src/LenovoWmi.cs`: BIOS boot-default thermal mode + Windows power overlay
- `src/Tray.cs`: tray icon, owner-drawn menu, HKCU Run autostart
- `src/FrameCounter.cs`: real-time ETW session `LenoMode-Presents` (x64 struct offsets)
- `src/Overlay.cs`: in-game overlay window
- `src/CpuStress.cs`: fixed all-core load for the auto test
- `src/Sensors.cs`: perf counters (one `ReadCategory` per category), WMI temperature and battery
- `src/Main.cs`: window logic, session stats, auto test, CSV
- `src/Ui.cs`: palette, `DotField`, `DotChart`, `Tile`
- `src/MainWindow.xaml`: layout and styles (embedded, loaded at runtime)
- `src/Strings.cs`: EN / VI strings, settings in `%APPDATA%\LenoMode`
