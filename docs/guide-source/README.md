# Guide source

`guide.html` plus the screenshots in `shots/` are the source of `../Photo-Print-Layout-Guide.pdf`.

To rebuild the PDF after editing, open a terminal in this folder and run (Microsoft Edge is included with Windows):

```
"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless=new --no-pdf-header-footer --print-to-pdf="%CD%\..\Photo-Print-Layout-Guide.pdf" "%CD%\guide.html"
```
