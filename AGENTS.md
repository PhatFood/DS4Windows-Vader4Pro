# AGENTS.md

## Project Overview

DS4Windows is a WPF application that maps DualShock 4, DualSense, Switch Pro, and JoyCon controllers to virtual Xbox 360 controllers on Windows. Built with .NET 8.0 and WPF.

## Build

```powershell
dotnet build DS4WindowsWPF.sln -c Debug -p:Platform=x64
dotnet build DS4WindowsWPF.sln -c Release -p:Platform=x64
```

Supported platforms: `x64`, `x86`.

## Test

```powershell
dotnet test DS4WindowsTests\DS4WindowsTests.csproj -c Debug
```

Uses MSTest framework (MSTest.TestAdapter + MSTest.TestFramework).

## Project Structure

- `DS4Windows/` — Main WPF application (DS4WinWPF.csproj)
- `DS4WindowsTests/` — Unit tests (MSTest)
- `DS4Windows/DS4Control/` — Controller device handling (HID input, output, etc.)
- `DS4Windows/HidLibrary/` — HID device library
- `DS4Windows/DS4Forms/` — WPF UI forms and controls
- `DS4Windows/DS4Library/` — Core library logic
- `DS4Windows/VJoyFeeder/` — vJoy virtual joystick integration
- `DS4Windows/Translations/` — Localized string resources (Strings.resx + language variants)
- `DS4Windows/Resources/` — Icons and images
- `libs/` — External native DLLs (ViGEm, FakerInput, SharpOSC)

## Key Configuration

- Target framework: `net8.0-windows`
- Allow unsafe blocks for some platform-specific code
- Server GC enabled
- Solution: Visual Studio 17 (2022)

## Feature Notes

### Touchpad Swipe Mapping

Allows any controller button to be mapped to a touchpad swipe gesture (Up/Down/Left/Right) via the binding UI.

**Key files:**
- `DS4Windows/DS4Control/FakeSwipeInjector.cs` — Per-device state machine that injects fake touchpad touch data into `DS4State`
- `DS4Windows/DS4Control/ScpUtil.cs` — `X360Controls` enum contains `SwipeTouchUp/Down/Left/Right` (placed after `Unbound`)
- `DS4Windows/DS4Control/Mapping.cs` — Routes swipe button presses to `FakeSwipeInjector.SetSwipeState()`
- `DS4Windows/DS4Control/ControlService.cs` — Calls `FakeSwipeInjector.ApplyToState()` after touch data copy in `On_Report`
- `DS4Windows/DS4Forms/BindingWindow.xaml(.cs)` — Swipe buttons in the Abs Mouse tab

**How it works:** Three-phase approach:
1. Center hold (3 frames) — finger at center (960, 471) to establish contact
2. Move (24 frames) — finger drifts toward endpoint (±400px) at ~17 units/frame
3. Release — finger lifts past endpoint (+40px) so the game sees clear velocity vector

Total ~27 active frames (~68ms at 400Hz). The finger never parks stationary — it keeps drifting past the endpoint (clamped to touchpad edges) so velocity is always present at lift, even after a long button hold. Presses arriving during an active swipe are dropped (no queue, no conflicts).
