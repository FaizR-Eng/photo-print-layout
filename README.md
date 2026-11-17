# Photo Print Layout

A simple, free tool for printing several photos on one page. You choose how many per page (1–20) and then **rotate, resize and position each photo yourself**. The Windows "Print Pictures" dialog can't do that: it rotates photos automatically and you can't change it.

![layout idea](https://img.shields.io/badge/Windows-10%20%2F%2011-blue) ![no install](https://img.shields.io/badge/install-none%20needed-green)

## Download

Get **PhotoPrintLayout.exe** from the [Releases](../../releases) page. It's one small file with no installer: just double-click it.

> The first time, Windows may show "Windows protected your PC". Click **More info → Run anyway** (this happens with any app that isn't commercially signed).

⭐ **If this helped you, please give it a star** (the ☆ Star button at the top right of this page). It's free, and it shows the tool is useful and helps others find it. Found a problem or have an idea? [Open an issue](../../issues).

## Features

- **Photos per page:** 1, 2, 3, 4, 6, 8, 9, 12, 16, 20, or any columns × rows
- **Free layout (preset "0"):** drop photos on the page and place them anywhere. Drag to move (even onto another page), drag the corner to resize, or type an exact size and position in cm. Hover a photo to see its size in cm
- **Select several photos:** Ctrl+click, Shift+click, drag a box from an empty spot, Ctrl+A, or "Select all photos on this page". Rotate, resize, delete, duplicate etc. then apply to all of them, and in free layout they move together
- **Arrange tools (free layout):** align left / center / right / top / middle / bottom, space evenly (horizontal or vertical), same width / same height, center on page. Align to each other or to the page margins
- **Smart guides:** while dragging or resizing, photos snap to other photos' edges and centers and to the page margins and center, with a thin dotted line showing the alignment (hold Alt to turn snapping off). Optional 1 cm grid
- **Undo / Redo** (Ctrl+Z / Ctrl+Y)
- **Save / Open layout** (`.photolayout` files) to reprint or edit a layout later. You can also drop a layout file onto the window or onto the .exe
- **Per-photo controls:**
  - Rotate left/right (nothing rotates automatically)
  - **Fit** (whole photo) or **Fill** (crop to fill the box)
  - **Manual size:** drag the corner handle, type an exact width/height in cm, use a % / slider, or Ctrl + mouse wheel
  - **Manual position:** drag the photo inside its box
  - Drag a photo onto another box to swap places
  - Duplicate a photo, or repeat it to fill the page (e.g. passport photos)
  - Bring to front / send to back (free layout)
- **Page:** paper size and printer taken from Windows, portrait/landscape, margin, gap between photos, optional thin cutting lines
- **Printing:** the normal Windows Print dialog (copies, printer Preferences for quality/paper type), plus Print preview
- **Adding photos:** drag & drop files or whole folders, or paste with Ctrl+V. Supports JPG/PNG/BMP/GIF/TIFF and WebP (HEIC needs Microsoft's free "HEIF Image Extensions")
- Remembers your layout settings
- Shows its version in the title bar. **Check for updates** (under the title) asks GitHub if a newer version exists, only when you click it

**Keyboard:** `Ctrl+A` select all · `Ctrl+Z` / `Ctrl+Y` undo / redo · `Ctrl+D` duplicate · `Ctrl+S` save layout · `R` rotate (Shift+R other way) · `F` fit/fill · `+`/`−` size · `Del` remove · `←`/`→` change order · `Ctrl+O` add · `Ctrl+P` print · in free layout, arrow keys nudge 1 mm (Shift = 1 cm)

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

## License

Free to use, share and modify under the [MIT License](LICENSE).
