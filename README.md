# Photo Print Layout

A simple, free tool for printing several photos on one page. You choose how many per page (1–20) and then **rotate, resize and position each photo yourself**. The Windows "Print Pictures" dialog can't do that: it rotates photos automatically and you can't change it.

![layout idea](https://img.shields.io/badge/Windows-10%20%2F%2011-blue) ![no install](https://img.shields.io/badge/install-none%20needed-green)

## Download

Get **PhotoPrintLayout.exe** from the [Releases](../../releases) page. It's one small file with no installer: just double-click it.

> The first time, Windows may show "Windows protected your PC". Click **More info → Run anyway** (this happens with any app that isn't commercially signed).

## Features

- **Photos per page:** 1, 2, 3, 4, 6, 8, 9, 12, 16, 20, or any columns × rows
- **Per-photo controls:**
  - Rotate left/right (nothing rotates automatically)
  - **Fit** (whole photo) or **Fill** (crop to fill the box)
  - **Manual size:** drag the corner handle, type an exact width/height in mm, use a % / slider, or Ctrl + mouse wheel
  - **Manual position:** drag the photo inside its box
  - Drag a photo onto another box to swap places
  - Duplicate a photo, or repeat it to fill the page (e.g. passport photos)
- **Page:** paper size and printer taken from Windows, portrait/landscape, margin, gap between photos, optional thin cutting lines
- **Printing:** the normal Windows Print dialog (copies, printer Preferences for quality/paper type), plus Print preview
- **Adding photos:** drag & drop files or whole folders, or paste with Ctrl+V. Supports JPG/PNG/BMP/GIF/TIFF and WebP (HEIC needs Microsoft's free "HEIF Image Extensions")
- Remembers your layout settings

**Keyboard:** `R` rotate (Shift+R other way) · `F` fit/fill · `+`/`−` size · `Del` remove · `←`/`→` change order · `Ctrl+O` add · `Ctrl+P` print

## Project layout

| Folder | What it is |
|---|---|
| `windows-app/` | The Windows program: `PhotoPrintLayout.cs` (all source code), `app.ico`, `build.bat` |
| `web-version/` | An earlier browser-based version (single HTML file, open it in Edge/Chrome) |

## Building the .exe yourself

You don't need Visual Studio. Windows already includes the C# compiler (.NET Framework 4).

1. Open the `windows-app` folder
2. Double-click `build.bat`
3. `PhotoPrintLayout.exe` appears in the same folder
