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
