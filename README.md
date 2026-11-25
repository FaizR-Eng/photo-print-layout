# Photo Print Layout

A simple, free tool for printing several photos on one page. You choose how many per page (1–20), an exact photo size (10×15, passport…) or a free layout, and then **rotate, resize and position each photo yourself**. The Windows "Print Pictures" dialog can't do that: it rotates photos automatically and you can't change it.

![Windows 10/11](https://img.shields.io/badge/Windows-10%20%2F%2011-blue) ![no admin](https://img.shields.io/badge/install-no%20admin%20needed-green)

## Download

From the [Releases](../../releases) page:

- **PhotoPrintLayout-1.5-installer.zip** (recommended): unzip it and double-click **Install.cmd**. It installs for your user only, with no admin rights. You get a Start menu entry, a right-click **"Open in Photo Print Layout"** menu for photos and PDFs, **Send to**, and an entry in Settings › Apps. Running a newer Install.cmd updates in place and keeps your settings. **Uninstall.cmd** removes everything except your settings.
- **PhotoPrintLayout.exe**: the program on its own. Just double-click it, no install.

> The first time, Windows may show "Windows protected your PC". Click **More info → Run anyway** (this happens with any app that isn't commercially signed).

📘 **User guide:** [Photo-Print-Layout-Guide.pdf](docs/Photo-Print-Layout-Guide.pdf). It was written for version 1.3, so the basics are the same, but in 1.5 **Print** is in the toolbar at the top and opens a Print window with a live preview.

⭐ **If this helped you, please give it a star** (the ☆ Star button at the top right of this page). Found a problem or have an idea? [Open an issue](../../issues).

## Features

**Layouts**
- **Photos per page:** 1, 2, 3, 4, 6, 8, 9, 12, 16, 20, or any columns × rows
- **Exact photo sizes:** 9×13, 10×15, 13×18, 15×20, 20×25 cm, 4×6, 5×7, 8×10 in, passport 3.5×4.5 cm, 2×2 in, wallet, or custom. As many as fit are placed on each page
- **Free layout (preset "0"):** place photos anywhere. Drag to move (even onto another page), drag the corner to resize, or type an exact size and position in cm
- **Gap measurements:** red lines show the gap in mm to the nearest photos (and to the paper edges), live while you drag and after arranging
- **Arrange tools:** align left / center / right / top / middle / bottom, space evenly (or with an exact gap in mm), same width / height, center on page. Smart guides snap photos to each other and to the margins (hold Alt to place freely)
- **Select several photos** (Ctrl+click, Shift+click, drag a box, Ctrl+A) and change them together

**Each photo**
- Rotate (nothing rotates by itself), **Fit** (whole photo) or **Fill** (crop), size in % or cm, move inside its box, swap places, duplicate, repeat to fill the page
- **Low-resolution warning** when a photo would print below 150 dpi
- Optional **captions**: file name or your own text

**Printing**
- **Print window with a live preview** of the real pages. Printer, paper, portrait/landscape, copies and page range are there too, and the preview updates at once. **Printer preferences…** opens the printer's own settings (quality, paper type)
- Shows the area **the printer can't print** (hatched) and warns if the margin is too small
- **Cut guides:** thin cut lines or crop marks
- **Save as PDF** without needing a PDF printer
- Sizes are exact on paper: a 10×15 cm photo prints at 10×15 cm

**Files**
- Opens JPG, PNG, BMP, GIF, TIFF, WebP and **PDF pages**; also HEIC and PSD when Windows has a codec for them
- Drag & drop files or folders, paste with Ctrl+V, right-click › Open in Photo Print Layout, or Send to. Several files open together in one window
- **Save / Open layout** (`.photolayout`), **Recent** list, undo / redo, and it remembers your printer, paper and window
- **Check for updates** (under the title) asks GitHub for a newer version, only when you click it

**Keyboard:** `Ctrl+O` add · `Ctrl+P` print · `Ctrl+S` save layout · `Ctrl+A` select all · `Ctrl+Z` / `Ctrl+Y` undo / redo · `Ctrl+D` duplicate · `R` rotate (Shift+R other way) · `F` fit/fill · `+`/`−` size · `Del` remove · `←`/`→` order · in free layout, arrow keys nudge 1 mm (Shift = 1 cm)

## Project layout

| Folder | What it is |
|---|---|
| `src/` | The Windows program: `PhotoPrintLayout.cs` (all source code) and `app.ico` |
| `installer/` | `Install.cmd`, `Uninstall.cmd`, `RightClick-Add.cmd`, `RightClick-Remove.cmd` and `installer.ps1` (per user, no admin) |
| `.tools/` | `build.ps1` (used by `build.cmd`) and `fileops.ps1` (safe copy/move helper with a log) |
| `test/` | Automatic tests (`run-tests.ps1`), sample images, and a manual checklist |
| `docs/` | The user guide PDF, and its source (`guide-source/`) |

Older versions (1.3.1 and the browser version) stay in the git history.

## Building it yourself

You don't need Visual Studio. Windows already includes the C# compiler (.NET Framework 4.8).

1. Double-click **build.cmd**
2. The program and the install scripts appear in a new folder `builds\yyyyMMdd-HHmmss\`

To run the automatic tests (they never print to a real printer and use their own settings folder):

```
powershell -STA -ExecutionPolicy Bypass -File test\run-tests.ps1 -Exe builds\<folder>\PhotoPrintLayout.exe -OutDir test\run-1
```

## License

Free to use, share and modify under the [MIT License](LICENSE).
