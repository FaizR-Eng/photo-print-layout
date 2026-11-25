# Guide source (version 1.5)

`guide.html` plus the pictures in `shots/` make `../Photo-Print-Layout-Guide.pdf`.

To refresh the guide for a new version:

1. Take new screenshots (uses drawn sample pictures and its own settings folder, never prints):
   `powershell -STA -ExecutionPolicy Bypass -File make-guide-shots.ps1 -Exe <path to PhotoPrintLayout.exe> -OutDir <new folder>`
2. Add the numbered callouts: `powershell -ExecutionPolicy Bypass -File annotate-main.ps1 -Dir <that folder>`
3. Copy the pictures you want into `shots/` and edit `guide.html`.
4. Make the PDF with Microsoft Edge (included with Windows):
   `msedge --headless=new --no-pdf-header-footer --print-to-pdf="<new pdf path>" "<full path to guide.html>"`
