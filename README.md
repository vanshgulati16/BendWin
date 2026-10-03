# BendWin

> The BendMac lid-fold animation, brought to Windows.

BendWin renders a GPU-accelerated page-fold effect on your desktop as you close your laptop lid — the same satisfying animation as [BendMac](https://bendmac.app/), built natively for Windows with Direct3D 11 and Windows Graphics Capture.

---

## Features

- **Live lid tracking** — reads your laptop's lid-angle sensor (HID or Inclinometer). Falls back to a smooth animated transition on power events if no sensor is available.
- **Three styles** — Silk, Shade, and Frost presets.
- **Adjustable** — Perspective, Blur, and Shadow sliders with live preview.
- **System tray** — runs quietly in the background; press **Escape** to pause.
- **Start with Windows** — optional auto-launch at login.
- MIT License · Open source.

---

## Requirements

- Windows 10 version 2004 (build 19041) or later
- .NET 8 runtime ([download](https://dotnet.microsoft.com/download/dotnet/8.0))
- A GPU with Direct3D 11 support (any GPU since ~2009)

---

## Build

```powershell
git clone https://github.com/vanshgulati16/BendWin.git
cd BendWin
dotnet build BendWin.sln -c Release
```

Or open `BendWin.sln` in Visual Studio 2022 / Rider and build from there.

### Publish a self-contained exe

```powershell
dotnet publish BendWin/BendWin.csproj -c Release -r win-x64 --self-contained -o publish/
```

---

## How it works

| Component | Technology |
|---|---|
| Screen capture | Windows.Graphics.Capture API |
| GPU rendering | Direct3D 11 (Vortice.Windows) |
| Shaders | HLSL (port of BendMac's Metal shaders) |
| Lid sensor | Windows HID API → Inclinometer → Power events |
| UI | WPF + WriteableBitmap overlay |

The fold effect uses an inverse homography (same math as BendMac) to simulate the screen bending around a bottom hinge, with five levels of progressive Gaussian blur applied toward the top edge.

---

## Inspired by

- [BendMac](https://github.com/IuCC123/BendMac) by IuCC123 — the original macOS app.

---

## License

MIT — see [LICENSE](LICENSE).
