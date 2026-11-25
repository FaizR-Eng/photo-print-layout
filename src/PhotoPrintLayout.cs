// Photo Print Layout - a small Windows app for printing several photos per page.
// Build (no install needed, uses the C# compiler that ships with Windows): run build.bat
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Photo Print Layout")]
[assembly: System.Reflection.AssemblyProduct("Photo Print Layout")]
[assembly: System.Reflection.AssemblyVersion("1.5.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.5.0.0")]

namespace PhotoPrintLayout
{
    class Photo
    {
        public string Path;
        public string Name;
        public Bitmap Preview;      // reduced-size copy for the screen
        public int W, H;            // full image size (after EXIF orientation)
        public int Rot;             // 0, 90, 180, 270
        public bool Fill;           // false = fit whole photo, true = fill box (crop)
        public double Scale = 1.0;  // manual size, 1.0 = fits/fills the box exactly
        public double Dx, Dy;       // manual position offset in mm
        // free layout ("0"): position/size on the page in mm
        public bool FreeSet;
        public int FPage;
        public double FX, FY, FW;
        public string Caption;      // own caption text (captions = "My own text")
        public int PdfPage = -1;    // page number when the picture is a page of a PDF file
        public Photo Copy() { return (Photo)MemberwiseClone(); }
    }

    struct Geo { public double Base, IW, IH, EW, EH, Dx, Dy, MaxDx, MaxDy; }

    class LoadResult { public Bitmap Bmp; public int FullW, FullH; }

    class PaperItem
    {
        public PaperSize P;
        public PaperItem(PaperSize p) { P = p; }
        public override string ToString()
        {
            string n = P.PaperName;
            string l = n.ToLowerInvariant();
            if (l.Contains("mm") || l.Contains(" in") || l.Contains("\"")) return n;
            return string.Format(CultureInfo.InvariantCulture, "{0}  ({1:0} × {2:0} mm)", n, P.Width * 0.254, P.Height * 0.254);
        }
    }

    static class Loader
    {
        static RotateFlipType FlipFor(int o)
        {
            switch (o)
            {
                case 2: return RotateFlipType.RotateNoneFlipX;
                case 3: return RotateFlipType.Rotate180FlipNone;
                case 4: return RotateFlipType.Rotate180FlipX;
                case 5: return RotateFlipType.Rotate90FlipX;
                case 6: return RotateFlipType.Rotate90FlipNone;
                case 7: return RotateFlipType.Rotate270FlipX;
                case 8: return RotateFlipType.Rotate270FlipNone;
                default: return RotateFlipType.RotateNoneFlipNone;
            }
        }

        static Bitmap Resize(Image img, int maxSide)
        {
            int w = img.Width, h = img.Height;
            double f = 1;
            if (maxSide > 0 && Math.Max(w, h) > maxSide) f = (double)maxSide / Math.Max(w, h);
            int nw = Math.Max(1, (int)Math.Round(w * f)), nh = Math.Max(1, (int)Math.Round(h * f));
            var b = new Bitmap(nw, nh, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            using (var ia = new ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, new Rectangle(0, 0, nw, nh), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }
            return b;
        }

        // GDI+ loader (jpg, png, bmp, gif, tiff). Thread-safe.
        public static LoadResult LoadGdi(string path, int maxSide)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var img = Image.FromStream(fs, false, false))
                {
                    RotateFlipType t = RotateFlipType.RotateNoneFlipNone;
                    int o = 1;
                    if (Array.IndexOf(img.PropertyIdList, 0x0112) >= 0)
                    {
                        var pi = img.GetPropertyItem(0x0112);
                        if (pi.Value != null && pi.Value.Length >= 2) o = BitConverter.ToUInt16(pi.Value, 0);
                        else if (pi.Value != null && pi.Value.Length == 1) o = pi.Value[0];
                        t = FlipFor(o);
                    }
                    var r = new LoadResult();
                    bool swap = o >= 5 && o <= 8;
                    r.FullW = swap ? img.Height : img.Width;
                    r.FullH = swap ? img.Width : img.Height;
                    r.Bmp = Resize(img, maxSide);
                    if (t != RotateFlipType.RotateNoneFlipNone) r.Bmp.RotateFlip(t);
                    return r;
                }
            }
            catch { return null; }
        }

        // Windows Imaging Component fallback (webp, heic if the codec is installed, ...). Must run on the UI thread.
        public static LoadResult LoadWic(string path, int maxSide)
        {
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bi));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    ms.Position = 0;
                    using (var img = Image.FromStream(ms))
                    {
                        var r = new LoadResult();
                        r.FullW = img.Width; r.FullH = img.Height;
                        r.Bmp = Resize(img, maxSide);
                        return r;
                    }
                }
            }
            catch { return null; }
        }

        public static LoadResult LoadAny(string path, int maxSide)
        {
            var r = LoadGdi(path, maxSide);
            if (r == null) r = LoadWic(path, maxSide);
            return r;
        }
    }

    // Opens PDF pages as pictures through Windows' built-in PDF engine (Windows.Data.Pdf). No extra software.
    static class PdfLoader
    {
        static bool inited, ok;
        static System.Reflection.MethodInfo asOp, asAct, asStream;
        static Type tFile, tDoc, tPage, tOpt, tRas;

        static void Init()
        {
            if (inited) return;
            inited = true;
            try
            {
                var rt = System.Reflection.Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                foreach (var m in rt.GetType("System.WindowsRuntimeSystemExtensions").GetMethods())
                {
                    if (m.Name != "AsTask" || m.GetParameters().Length != 1) continue;
                    string pn = m.GetParameters()[0].ParameterType.Name;
                    if (pn == "IAsyncOperation`1") asOp = m; else if (pn == "IAsyncAction") asAct = m;
                }
                foreach (var m in rt.GetType("System.IO.WindowsRuntimeStreamExtensions").GetMethods())
                    if (m.Name == "AsStreamForRead" && m.GetParameters().Length == 1) asStream = m;
                tFile = Type.GetType("Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime");
                tDoc = Type.GetType("Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType=WindowsRuntime");
                tPage = Type.GetType("Windows.Data.Pdf.PdfPage, Windows.Data.Pdf, ContentType=WindowsRuntime");
                tOpt = Type.GetType("Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType=WindowsRuntime");
                tRas = Type.GetType("Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime");
                ok = asOp != null && asAct != null && asStream != null && tFile != null && tDoc != null && tPage != null && tOpt != null && tRas != null;
            }
            catch { ok = false; }
        }

        static object Await(object op, Type result)
        {
            var t = (Task)asOp.MakeGenericMethod(result).Invoke(null, new[] { op });
            t.Wait();
            return t.GetType().GetProperty("Result").GetValue(t, null);
        }

        static object Open(string path)
        {
            var file = Await(tFile.GetMethod("GetFileFromPathAsync").Invoke(null, new object[] { Path.GetFullPath(path) }), tFile);
            System.Reflection.MethodInfo load = null;
            foreach (var m in tDoc.GetMethods()) if (m.Name == "LoadFromFileAsync" && m.GetParameters().Length == 1) load = m;
            return Await(load.Invoke(null, new[] { file }), tDoc);
        }

        public static int PageCount(string path)
        {
            Init();
            if (!ok) return 0;
            try { return (int)(uint)tDoc.GetProperty("PageCount").GetValue(Open(path), null); }
            catch { return 0; }
        }

        // renders one page so its longest side is about maxSide pixels; FullW/FullH = page size at 300 dpi
        public static LoadResult Load(string path, int page, int maxSide)
        {
            Init();
            if (!ok) return null;
            try
            {
                var doc = Open(path);
                if (page < 0 || page >= (int)(uint)tDoc.GetProperty("PageCount").GetValue(doc, null)) return null;
                var pg = tDoc.GetMethod("GetPage").Invoke(doc, new object[] { (uint)page });
                try
                {
                    var size = tPage.GetProperty("Size").GetValue(pg, null);
                    double wDip = Convert.ToDouble(size.GetType().GetProperty("Width").GetValue(size, null));
                    double hDip = Convert.ToDouble(size.GetType().GetProperty("Height").GetValue(size, null));
                    int fullW = Math.Max(1, (int)Math.Round(wDip / 96 * 300)), fullH = Math.Max(1, (int)Math.Round(hDip / 96 * 300));
                    double f = maxSide > 0 && Math.Max(fullW, fullH) > maxSide ? (double)maxSide / Math.Max(fullW, fullH) : 1;
                    int dw = Math.Max(1, (int)Math.Round(fullW * f)), dh = Math.Max(1, (int)Math.Round(fullH * f));
                    var opt = Activator.CreateInstance(tOpt);
                    tOpt.GetProperty("DestinationWidth").SetValue(opt, (uint)dw, null);
                    tOpt.GetProperty("DestinationHeight").SetValue(opt, (uint)dh, null);
                    var ras = Activator.CreateInstance(tRas);
                    System.Reflection.MethodInfo render = null;
                    foreach (var m in tPage.GetMethods()) if (m.Name == "RenderToStreamAsync" && m.GetParameters().Length == 2) render = m;
                    ((Task)asAct.Invoke(null, new[] { render.Invoke(pg, new[] { ras, opt }) })).Wait();
                    var input = tRas.GetMethod("GetInputStreamAt").Invoke(ras, new object[] { (ulong)0 });
                    using (var st = (Stream)asStream.Invoke(null, new[] { input }))
                    using (var ms = new MemoryStream())
                    {
                        st.CopyTo(ms);
                        ms.Position = 0;
                        using (var img = Image.FromStream(ms))
                        {
                            var b = new Bitmap(dw, dh, PixelFormat.Format32bppPArgb);
                            using (var g = Graphics.FromImage(b))
                            {
                                g.Clear(Color.White);
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(img, new Rectangle(0, 0, dw, dh));
                            }
                            return new LoadResult { Bmp = b, FullW = fullW, FullH = fullH };
                        }
                    }
                }
                finally { var d = pg as IDisposable; if (d != null) d.Dispose(); }
            }
            catch { return null; }
        }
    }

    class PreviewPanel : Panel
    {
        public Func<MouseEventArgs, bool> CtrlWheel;
        public PreviewPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AutoScroll = true;
        }
        protected override bool IsInputKey(Keys k)
        {
            Keys kc = k & Keys.KeyCode;
            if (kc == Keys.Left || kc == Keys.Right || kc == Keys.Up || kc == Keys.Down) return true;
            return base.IsInputKey(k);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control && CtrlWheel != null && CtrlWheel(e)) return;
            base.OnMouseWheel(e);
            Invalidate();
        }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    }

    class MainForm : Form
    {
        static readonly Color Accent = Color.FromArgb(37, 99, 235);
        static readonly Color Muted = Color.FromArgb(107, 114, 128);
        static readonly Color LineCol = Color.FromArgb(217, 222, 231);
        static readonly Color BtnBack = Color.FromArgb(248, 250, 252);
        static readonly Color TextCol = Color.FromArgb(31, 41, 55);
        static readonly int[] PresetN = { 0, 1, 2, 3, 4, 6, 8, 9, 12, 16, 20 };
        static readonly int[,] PresetCR = { { 0, 0 }, { 1, 1 }, { 1, 2 }, { 1, 3 }, { 2, 2 }, { 2, 3 }, { 2, 4 }, { 3, 3 }, { 3, 4 }, { 4, 4 }, { 4, 5 } };
        static readonly string[] ImageExt = { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif" };

        readonly List<Photo> items = new List<Photo>();
        Photo sel;
        string printerName = "";
        string savedPaperName = "";
        PaperSize paper;
        bool landscape;
        int cols = 2, rows = 2;
        double margin = 5, gap = 3;
        bool cut;
        bool free;          // free layout: place photos anywhere
        float zoom = 0.6f;
        float dpi = 1f;
        bool updating;
        int printPage;
        int printFrom, printTo = int.MaxValue;   // page range for printing (0-based, end exclusive)
        double boxW, boxH;           // fixed photo size in mm (0 = boxes fill the page grid)
        int guideMode;               // 0 none, 1 thin cut lines, 2 crop marks
        int captionMode;             // 0 none, 1 file name, 2 own text
        double captionPt = 9;
        bool showEdge = true;        // show the printer's unprintable edge on screen
        bool previewEdge = true;     // ... and in the Print window preview
        double hmL, hmT, hmR, hmB;   // printer's unprintable edge (mm) for the current printer / paper / orientation
        PrinterSettings printSettings;   // driver preferences chosen in the Print window
        readonly List<string> recentLayouts = new List<string>(), recentPhotos = new List<string>();
        Rectangle savedBounds;
        bool savedMax;
        readonly System.Collections.Concurrent.ConcurrentQueue<string> incoming = new System.Collections.Concurrent.ConcurrentQueue<string>();
        readonly List<string> pendingFiles = new List<string>();
        DateTime lastIncoming;
        ComboBox cbSize, cbGuides, cbCaptions;
        NumericUpDown nBoxW, nBoxH, nCapPt;
        Control rowCustomSize, rowCaptionText;
        TextBox txtCaption;
        Label lblMarginWarn;
        ToolStripDropDownButton tbRecent;
        static readonly string[] SizeNames = {
            "Fill the page (use the grid)", "9 × 13 cm", "10 × 15 cm", "13 × 18 cm", "15 × 20 cm", "20 × 25 cm",
            "4 × 6 in (10.2 × 15.2 cm)", "5 × 7 in (12.7 × 17.8 cm)", "8 × 10 in (20.3 × 25.4 cm)",
            "Passport 3.5 × 4.5 cm", "2 × 2 in (US passport)", "Wallet 2.5 × 3.5 in", "Custom size…" };
        static readonly double[,] SizeMM = {
            { 0, 0 }, { 90, 130 }, { 100, 150 }, { 130, 180 }, { 150, 200 }, { 200, 250 },
            { 101.6, 152.4 }, { 127, 177.8 }, { 203.2, 254 }, { 35, 45 }, { 50.8, 50.8 }, { 63.5, 88.9 }, { -1, -1 } };

        PreviewPanel preview;
        ComboBox cbPrinter, cbPaper;
        RadioButton rbPort, rbLand, rbFit, rbFill;
        NumericUpDown nCols, nRows, nMargin, nGap, nSize, nWmm, nHmm;
        TrackBar tbSize;
        Label lblSel;
        Control selBox;
        ToolStripStatusLabel lblInfo, lblZoom;
        readonly List<Button> presetButtons = new List<Button>();
        ContextMenuStrip ctx;
        readonly Dictionary<string, ToolStripMenuItem> ctxItems = new Dictionary<string, ToolStripMenuItem>();
        readonly ToolTip tips = new ToolTip();
        NumericUpDown nX, nY;
        Control rowMode, rowSlider, rowPos;
        Button btnFillPage, btnResetAll;
        int hoverIndex = -1;
        // multi-selection: selList holds all selected photos, sel is the last one clicked ("primary")
        readonly List<Photo> selList = new List<Photo>();
        bool snapOn = true, showGrid, alignToPage;
        bool showGaps = true;        // red gap measurements in free layout
        double alignGap;             // exact gap (mm) for Space evenly; 0 = spread to fill
        CheckBox chkGaps;
        NumericUpDown nAlignGap;
        Control arrangeBox;
        ToolStripButton btnUndo, btnRedo;
        CheckBox chkAlignPage, chkSnap, chkGrid;
        readonly Dictionary<Photo, double[]> dragStartPos = new Dictionary<Photo, double[]>();
        bool pendingSingle;
        bool marquee;
        PointF marqA, marqB;
        List<Photo> marqBase = new List<Photo>();
        readonly List<double[]> guides = new List<double[]>();   // { 1 = vertical / 0 = horizontal, position mm, page }
        class Snap { public List<Photo> Items; public int[] SelIdx; public int Primary; public bool Free; public int Cols, Rows; public double BoxW, BoxH; }
        readonly List<Snap> undo = new List<Snap>(), redo = new List<Snap>();
        string lastUndoKind = "";
        DateTime lastUndoTime;
        double grabX, grabY;

        enum DragMode { None, Move, Resize }
        DragMode drag = DragMode.None;
        Point dragStart;
        bool dragMoved;
        double dragDx, dragDy, dragScale;
        int dragIndex = -1, dropTarget = -1;
        PointF dragCenter;

        int SideW { get { return D(272); } }

        public MainForm()
        {
            Text = "Photo Print Layout " + VersionText;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            using (var g = CreateGraphics()) dpi = g.DpiX / 96f;
            Size = new Size(D(1250), D(860));
            MinimumSize = new Size(D(820), D(560));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildUi();
            LoadSettings();
            if (savedBounds.Width > 300 && savedBounds.Height > 300 && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(savedBounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = savedBounds;
                if (savedMax) WindowState = FormWindowState.Maximized;
            }
            FillPrinters();
            SyncControls();
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            Shown += (s, e) => { FitZoom(); preview.Focus(); };
            FormClosing += (s, e) =>
            {
                savedMax = WindowState == FormWindowState.Maximized;
                savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                SaveSettings();
            };
            var inTimer = new System.Windows.Forms.Timer { Interval = 150 };
            inTimer.Tick += (s, e) => PumpIncoming();
            inTimer.Start();
        }

        int D(float v) { return (int)Math.Round(v * dpi); }

        // ---- version & update check (contacts GitHub only when the user clicks "Check for updates")
        const string LatestReleaseApi = "https://api.github.com/repos/FaizR-Eng/photo-print-layout/releases/latest";
        const string ReleasesPage = "https://github.com/FaizR-Eng/photo-print-layout/releases/latest";

        static Version Norm(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)); }
        static Version MyVersion { get { return Norm(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version); } }
        static string Short(Version v) { return v.Build > 0 ? v.Major + "." + v.Minor + "." + v.Build : v.Major + "." + v.Minor; }
        static string VersionText { get { return Short(MyVersion); } }

        // returns the newest release version number on GitHub, e.g. "1.3.0"
        static string FetchLatestVersion()
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;   // TLS 1.2
            using (var wc = new System.Net.WebClient())
            {
                wc.Headers.Add("User-Agent", "PhotoPrintLayout-UpdateCheck");
                wc.Headers.Add("Accept", "application/vnd.github+json");
                string json = wc.DownloadString(LatestReleaseApi);
                var m = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9.]*)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
        }

        static bool IsNewer(string latest, Version mine)
        {
            Version v;
            return latest != null && Version.TryParse(latest, out v) && Norm(v) > Norm(mine);
        }

        void CheckForUpdates(LinkLabel link)
        {
            link.Enabled = false;
            Cursor = Cursors.AppStarting;
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task.Factory.StartNew(() => FetchLatestVersion()).ContinueWith(t =>
            {
                link.Enabled = true;
                Cursor = Cursors.Default;
                if (t.IsFaulted)
                {
                    MessageBox.Show(this, "Could not check for updates. Please check your internet connection and try again.\n\n" + t.Exception.GetBaseException().Message,
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string latest = t.Result;
                if (latest == null) { MessageBox.Show(this, "Could not read the latest version from GitHub.", Text); return; }
                if (IsNewer(latest, MyVersion))
                {
                    var r = MessageBox.Show(this, "A new version is available: " + Short(Norm(Version.Parse(latest))) + "\nYou have: " + VersionText +
                        "\n\nOpen the download page?", "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (r == DialogResult.Yes) System.Diagnostics.Process.Start(ReleasesPage);
                }
                else MessageBox.Show(this, "You have the latest version (" + VersionText + ").", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }, ui);
        }
        static double Clamp(double v, double a, double b) { return v < a ? a : (v > b ? b : v); }

        // ------------------------------------------------------------------ UI
        void BuildUi()
        {
            preview = new PreviewPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(233, 237, 243), AllowDrop = true };
            preview.Paint += PreviewPaint;
            preview.MouseDown += PreviewMouseDown;
            preview.MouseMove += PreviewMouseMove;
            preview.MouseUp += PreviewMouseUp;
            preview.MouseLeave += (s, e) => { if (hoverIndex != -1) { hoverIndex = -1; preview.Invalidate(); } };
            preview.MouseDoubleClick += (s, e) => { if (free && e.Button == MouseButtons.Left && FreeHit(World(e.Location)) < 0) BrowseAdd(); };
            preview.KeyDown += OnPreviewKey;
            preview.Resize += (s, e) => { UpdateScroll(); preview.Invalidate(); };
            preview.DragEnter += OnDragEnter;
            preview.DragDrop += OnDragDrop;
            preview.CtrlWheel = CtrlWheel;

            var side = new FlowLayoutPanel
            {
                Dock = DockStyle.Left, Width = D(318), FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(D(14), D(10), D(8), D(16)), BackColor = Color.White, AllowDrop = true
            };
            side.DragEnter += OnDragEnter;
            side.DragDrop += OnDragDrop;

            var status = new StatusStrip { SizingGrip = false, BackColor = Color.White };
            lblInfo = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted };
            var bOut = new ToolStripButton("  −  ") { ToolTipText = "Zoom out" };
            lblZoom = new ToolStripStatusLabel { AutoSize = false, Width = D(54) };
            var bIn = new ToolStripButton("  +  ") { ToolTipText = "Zoom in" };
            var bFit = new ToolStripButton("Fit page") { ToolTipText = "Zoom so a whole page fits" };
            bOut.Click += (s, e) => { zoom = (float)Clamp(zoom / 1.2f, 0.1, 4); AfterChange(); };
            bIn.Click += (s, e) => { zoom = (float)Clamp(zoom * 1.2f, 0.1, 4); AfterChange(); };
            bFit.Click += (s, e) => FitZoom();
            status.Items.AddRange(new ToolStripItem[] { lblInfo, bOut, lblZoom, bIn, bFit });

            // Toolbar: always visible, never scrolls
            var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(D(8), D(4), D(8), D(4)), BackColor = Color.White, RenderMode = ToolStripRenderMode.System, CanOverflow = true };
            Func<string, string, EventHandler, ToolStripButton> TB = (text, tip, click) =>
            {
                var b = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(D(6), D(3), D(6), D(3)), Margin = new Padding(D(1), 0, D(1), 0) };
                b.Click += click;
                return b;
            };
            tb.Items.Add(TB("+ Add photos", "Add photos (Ctrl+O)", (s, e) => BrowseAdd()));
            tb.Items.Add(TB("Open layout…", "Open a layout you saved before", (s, e) => OpenLayout()));
            tb.Items.Add(TB("Save layout…", "Save this arrangement to reprint or edit later (Ctrl+S)", (s, e) => SaveLayout()));
            tb.Items.Add(TB("Save as PDF…", "Save the pages as a PDF file (no printer needed)", (s, e) => SaveAsPdf()));
            tbRecent = new ToolStripDropDownButton("Recent") { ToolTipText = "Recently used layouts and photos", Padding = new Padding(D(6), D(3), D(6), D(3)) };
            tbRecent.DropDownOpening += (s, e) => FillRecentMenu();
            tbRecent.DropDownItems.Add("(none yet)");
            tb.Items.Add(tbRecent);
            tb.Items.Add(new ToolStripSeparator());
            btnUndo = TB("↶ Undo", "Undo the last change (Ctrl+Z)", (s, e) => Undo());
            btnRedo = TB("↷ Redo", "Redo (Ctrl+Y)", (s, e) => Redo());
            tb.Items.Add(btnUndo);
            tb.Items.Add(btnRedo);
            var printBtn = PrimaryButton("Print…");
            printBtn.Width = D(110); printBtn.Height = D(30); printBtn.Margin = new Padding(0);
            printBtn.Click += (s, e) => DoPrint();
            tips.SetToolTip(printBtn, "Print with a live preview (Ctrl+P)");
            var hostPrint = new ToolStripControlHost(printBtn) { Alignment = ToolStripItemAlignment.Right, Margin = new Padding(D(4), D(1), 0, D(1)), AutoSize = false, Size = new Size(D(110), D(30)) };
            tb.Items.Add(hostPrint);
            var tPrev = TB("Print preview…", "See the pages exactly as they will print", (s, e) => DoPreview());
            tPrev.Alignment = ToolStripItemAlignment.Right;
            tb.Items.Add(tPrev);

            Controls.Add(preview);
            Controls.Add(side);
            Controls.Add(tb);
            Controls.Add(status);

            int W = SideW;
            side.Controls.Add(new Label { Text = "Photo Print Layout", Font = new Font("Segoe UI Semibold", 13f), AutoSize = false, Width = W, Height = D(30), ForeColor = TextCol });
            side.Controls.Add(Hint("Several photos per page — rotate, size and place each one yourself."));
            const string checkText = "Check for updates";
            var ver = new LinkLabel
            {
                Text = "Version " + VersionText + "   ·   " + checkText, AutoSize = false, Width = W, Height = D(22),
                ForeColor = Muted, LinkColor = Accent, ActiveLinkColor = Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 0, 0, D(2))
            };
            ver.LinkArea = new LinkArea(ver.Text.IndexOf(checkText), checkText.Length);
            ver.LinkClicked += (s, e) => CheckForUpdates(ver);
            tips.SetToolTip(ver, "Asks GitHub if a newer version exists (only when you click)");
            side.Controls.Add(ver);
            var add = PrimaryButton("+   Add photos…");
            add.Click += (s, e) => BrowseAdd();
            side.Controls.Add(add);
            side.Controls.Add(Hint("Or drag & drop photos (or a folder) here, or paste with Ctrl+V."));

            // Printer & paper
            AddHeader(side, "PRINTER & PAPER");
            cbPrinter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cbPaper = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cbPrinter.SelectedIndexChanged += (s, e) =>
            {
                if (updating) return;
                printerName = cbPrinter.SelectedItem as string ?? "";
                printSettings = null;
                FillPapers();
                FitZoom();
            };
            cbPaper.SelectedIndexChanged += (s, e) =>
            {
                if (updating) return;
                var pi = cbPaper.SelectedItem as PaperItem;
                if (pi != null) { paper = pi.P; FitZoom(); }
            };
            side.Controls.Add(Row(32, new float[] { 30, 70 }, SmallLabel("Printer"), cbPrinter));
            side.Controls.Add(Row(32, new float[] { 30, 70 }, SmallLabel("Paper"), cbPaper));
            rbPort = Toggle("Portrait");
            rbLand = Toggle("Landscape");
            EventHandler orient = (s, e) =>
            {
                if (updating) return;
                bool l = rbLand.Checked;
                if (l == landscape) return;
                landscape = l;
                int t = cols; cols = rows; rows = t;
                SyncControls();
                FitZoom();
            };
            rbPort.CheckedChanged += orient;
            rbLand.CheckedChanged += orient;
            side.Controls.Add(Row(32, null, rbPort, rbLand));

            // Layout
            AddHeader(side, "PHOTOS PER PAGE");
            var grid = new TableLayoutPanel { ColumnCount = 6, RowCount = 2, Width = W, Height = D(68), Margin = new Padding(0, D(2), 0, D(4)) };
            for (int i = 0; i < 6; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            for (int i = 0; i < PresetN.Length; i++)
            {
                int idx = i;
                var b = Btn(PresetN[i].ToString(), (s, e) =>
                {
                    if (PresetN[idx] == 0) { EnterFree(); return; }
                    PushUndo("");
                    free = false;
                    boxW = boxH = 0;
                    int c = PresetCR[idx, 0], r = PresetCR[idx, 1];
                    if (landscape) { cols = r; rows = c; } else { cols = c; rows = r; }
                    SyncControls();
                    AfterChange();
                });
                b.Dock = DockStyle.Fill;
                b.Margin = new Padding(D(2));
                tips.SetToolTip(b, PresetN[i] == 0 ? "0 = Free layout: put photos anywhere and size them yourself" : PresetN[i] + " photos per page");
                presetButtons.Add(b);
                grid.Controls.Add(b, i % 6, i / 6);
            }
            side.Controls.Add(grid);
            side.Controls.Add(Hint("0 = free layout: drop photos on the page, then move and resize them freely."));
            cbSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = D(260) };
            foreach (var nm in SizeNames) cbSize.Items.Add(nm);
            side.Controls.Add(Row(32, new float[] { 32, 68 }, SmallLabel("Photo size"), cbSize));
            tips.SetToolTip(cbSize, "Print every photo at an exact size (as many as fit per page). In free layout it sizes the selected photos.");
            nBoxW = Num(0.5m, 100, 1, 0.1m);
            nBoxH = Num(0.5m, 100, 1, 0.1m);
            rowCustomSize = Row(32, new float[] { 13, 37, 13, 37 }, SmallLabel("W"), WithSuffix(nBoxW, "cm"), SmallLabel("H"), WithSuffix(nBoxH, "cm"));
            side.Controls.Add(rowCustomSize);
            cbSize.SelectedIndexChanged += (s, e) => { if (updating) return; rowCustomSize.Visible = cbSize.SelectedIndex == SizeNames.Length - 1; ApplySizeChoice(); };
            nBoxW.ValueChanged += (s, e) => { if (updating) return; if (cbSize.SelectedIndex == SizeNames.Length - 1) ApplySizeChoice(); };
            nBoxH.ValueChanged += (s, e) => { if (updating) return; if (cbSize.SelectedIndex == SizeNames.Length - 1) ApplySizeChoice(); };
            nCols = Num(1, 12, 0, 1);
            nRows = Num(1, 12, 0, 1);
            side.Controls.Add(Row(32, new float[] { 46, 24, 6, 24 }, SmallLabel("Columns × Rows"), nCols, CenterLabel("×"), nRows));
            nMargin = Num(0, 50, 1, 0.5m);
            nGap = Num(0, 50, 1, 0.5m);
            side.Controls.Add(Row(32, new float[] { 70, 30 }, SmallLabel("Page margin (mm)"), nMargin));
            side.Controls.Add(Row(32, new float[] { 70, 30 }, SmallLabel("Gap between photos (mm)"), nGap));
            lblMarginWarn = Hint("This printer can't print the outer 0.0 mm of this paper. Photos closer to the edge will be cut.");
            lblMarginWarn.ForeColor = Color.FromArgb(180, 83, 9);
            lblMarginWarn.Visible = false;
            side.Controls.Add(lblMarginWarn);
            cbGuides = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cbGuides.Items.AddRange(new object[] { "None", "Thin cut lines", "Crop marks (corners)" });
            side.Controls.Add(Row(32, new float[] { 40, 60 }, SmallLabel("Cut guides"), cbGuides));
            cbCaptions = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cbCaptions.Items.AddRange(new object[] { "None", "File name", "My own text" });
            side.Controls.Add(Row(32, new float[] { 40, 60 }, SmallLabel("Captions"), cbCaptions));
            nCapPt = Num(5, 36, 0, 1);
            side.Controls.Add(Row(32, new float[] { 70, 30 }, SmallLabel("Caption text size (pt)"), nCapPt));
            cbGuides.SelectedIndexChanged += (s, e) => { if (updating) return; guideMode = cbGuides.SelectedIndex; cut = guideMode == 1; preview.Invalidate(); };
            cbCaptions.SelectedIndexChanged += (s, e) => { if (updating) return; PushUndo(""); captionMode = cbCaptions.SelectedIndex; SyncControls(); AfterChange(); };
            nCapPt.ValueChanged += (s, e) => { if (updating) return; captionPt = (double)nCapPt.Value; AfterChange(); };
            nCols.ValueChanged += (s, e) => { if (updating) return; PushUndo("cols"); cols = (int)nCols.Value; SyncControls(); AfterChange(); };
            nRows.ValueChanged += (s, e) => { if (updating) return; PushUndo("rows"); rows = (int)nRows.Value; SyncControls(); AfterChange(); };
            nMargin.ValueChanged += (s, e) => { if (updating) return; margin = (double)nMargin.Value; UpdateMarginWarning(); AfterChange(); };
            nGap.ValueChanged += (s, e) => { if (updating) return; gap = (double)nGap.Value; AfterChange(); };

            // Selected photo
            AddHeader(side, "SELECTED PHOTO");
            var sb = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            selBox = sb;
            lblSel = new Label { AutoSize = false, Width = W, Height = D(38), ForeColor = Muted, AutoEllipsis = true };
            sb.Controls.Add(lblSel);
            sb.Controls.Add(Row(34, null, Btn("⟲  Rotate left", (s, e) => Act("rl")), Btn("⟳  Rotate right", (s, e) => Act("rr"))));
            rbFit = Toggle("Fit whole photo");
            rbFill = Toggle("Fill box (crop)");
            EventHandler mode = (s, e) =>
            {
                var rb = (RadioButton)s;
                if (updating || sel == null || !rb.Checked) return;
                Act(rb == rbFill ? "fill" : "fit");
            };
            rbFit.CheckedChanged += mode;
            rbFill.CheckedChanged += mode;
            rowMode = Row(32, null, rbFit, rbFill);
            sb.Controls.Add(rowMode);

            sb.Controls.Add(SubHeader("Size"));
            tbSize = new TrackBar { Minimum = 10, Maximum = 300, TickStyle = TickStyle.None, AutoSize = false, SmallChange = 5, LargeChange = 10, Value = 100 };
            nSize = Num(5, 2000, 0, 5);
            rowSlider = Row(32, new float[] { 66, 34 }, tbSize, WithSuffix(nSize, "%"));
            sb.Controls.Add(rowSlider);
            nWmm = Num(0.3m, 200, 1, 0.1m);   // shown in cm
            nHmm = Num(0.3m, 200, 1, 0.1m);
            sb.Controls.Add(Row(32, new float[] { 13, 37, 13, 37 }, SmallLabel("W"), WithSuffix(nWmm, "cm"), SmallLabel("H"), WithSuffix(nHmm, "cm")));
            nX = Num(0, 200, 1, 0.1m);
            nY = Num(0, 200, 1, 0.1m);
            rowPos = Row(32, new float[] { 13, 37, 13, 37 }, SmallLabel("X"), WithSuffix(nX, "cm"), SmallLabel("Y"), WithSuffix(nY, "cm"));
            tips.SetToolTip(nX, "Distance from the left edge of the paper");
            tips.SetToolTip(nY, "Distance from the top edge of the paper");
            sb.Controls.Add(rowPos);
            txtCaption = new TextBox();
            rowCaptionText = Row(30, new float[] { 30, 70 }, SmallLabel("Caption"), txtCaption);
            sb.Controls.Add(rowCaptionText);
            txtCaption.TextChanged += (s, e) =>
            {
                if (updating || sel == null) return;
                PushUndo("caption");
                string t = txtCaption.Text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                foreach (var ph in Targets()) ph.Caption = t;
                preview.Invalidate();
            };
            nX.ValueChanged += (s, e) => { if (updating || sel == null || !free) return; MoveSelTo((double)nX.Value * 10, true); };
            nY.ValueChanged += (s, e) => { if (updating || sel == null || !free) return; MoveSelTo((double)nY.Value * 10, false); };
            sb.Controls.Add(Row(34, null, Btn("Reset size & position", (s, e) => Act("reset"))));
            tbSize.ValueChanged += (s, e) => { if (updating || sel == null) return; SetScaleAll(tbSize.Value / 100.0); };
            nSize.ValueChanged += (s, e) => { if (updating || sel == null) return; SetScaleAll((double)nSize.Value / 100.0); };
            nWmm.ValueChanged += (s, e) => { if (updating || sel == null) return; SetSizeMm((double)nWmm.Value * 10, true); };
            nHmm.ValueChanged += (s, e) => { if (updating || sel == null) return; SetSizeMm((double)nHmm.Value * 10, false); };

            sb.Controls.Add(SubHeader("Order & copies"));
            sb.Controls.Add(Row(34, null, Btn("◀  Move earlier", (s, e) => Act("prev")), Btn("Move later  ▶", (s, e) => Act("next"))));
            btnFillPage = Btn("Repeat to fill page", (s, e) => Act("fillpage"));
            sb.Controls.Add(Row(34, null, Btn("Duplicate", (s, e) => Act("dup")), btnFillPage));
            sb.Controls.Add(Row(34, null, Btn("✕  Remove from layout", (s, e) => Act("del"))));
            side.Controls.Add(sb);
            side.Controls.Add(Hint("On the page: drag a photo to move it inside its box, drag the blue corner square to resize it, " +
                                   "or drag it onto another box to swap places. Ctrl + mouse wheel also resizes. Right-click for more. Ctrl+click, or drag a box from an empty spot, to select several. " +
                                   "Hover a photo to see its size in cm.\n" +
                                   "In free layout (0) drag photos anywhere, even onto another page; arrow keys nudge 1 mm (Shift = 1 cm).\n" +
                                   "Keys: R rotate · F fit/fill · + / − size · Del remove · ← → order"));

            // Arrange (free layout only)
            var ab = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            arrangeBox = ab;
            AddHeader(ab, "ARRANGE  (free layout)");
            ab.Controls.Add(SubHeader("Align"));
            ab.Controls.Add(Row(32, null, AB("Left", "left", "Line up the left edges"), AB("Center", "hcenter", "Line up the centers (left–right)"), AB("Right", "right", "Line up the right edges")));
            ab.Controls.Add(Row(32, null, AB("Top", "top", "Line up the top edges"), AB("Middle", "vcenter", "Line up the middles (up–down)"), AB("Bottom", "bottom", "Line up the bottom edges")));
            ab.Controls.Add(SubHeader("Spacing & size"));
            ab.Controls.Add(Row(32, null, AB("Space evenly ↔", "disth", "Equal gaps left–right (3 or more photos, or 2+ with page margins)"), AB("Space evenly ↕", "distv", "Equal gaps top–bottom (3 or more photos, or 2+ with page margins)")));
            nAlignGap = Num(0, 200, 1, 0.5m);
            ab.Controls.Add(Row(32, new float[] { 70, 30 }, SmallLabel("Exact gap (mm, 0 = spread out)"), nAlignGap));
            tips.SetToolTip(nAlignGap, "With a number here, Space evenly puts the photos exactly this far apart, starting from the first one. 0 = spread them over the space they use.");
            nAlignGap.ValueChanged += (s, e) => { if (updating) return; alignGap = (double)nAlignGap.Value; };
            ab.Controls.Add(Row(32, null, AB("Same width", "samew", "Make the selected photos as wide as the last one you clicked"), AB("Same height", "sameh", "Make the selected photos as tall as the last one you clicked")));
            ab.Controls.Add(Row(32, null, AB("Center on page", "centerpage", "Move the selection to the middle of the page")));
            chkAlignPage = new CheckBox { Text = "Align to page margins (not to each other)", AutoSize = false, Width = W, Height = D(24) };
            chkSnap = new CheckBox { Text = "Snap and show guide lines while dragging", Checked = true, AutoSize = false, Width = W, Height = D(24) };
            chkGrid = new CheckBox { Text = "Show 1 cm grid", AutoSize = false, Width = W, Height = D(24) };
            chkAlignPage.CheckedChanged += (s, e) => alignToPage = chkAlignPage.Checked;
            chkSnap.CheckedChanged += (s, e) => snapOn = chkSnap.Checked;
            chkGrid.CheckedChanged += (s, e) => { showGrid = chkGrid.Checked; preview.Invalidate(); };
            ab.Controls.Add(chkAlignPage);
            ab.Controls.Add(chkSnap);
            ab.Controls.Add(chkGrid);
            chkGaps = new CheckBox { Text = "Show gaps between photos (mm)", Checked = true, AutoSize = false, Width = W, Height = D(24) };
            chkGaps.CheckedChanged += (s, e) => { showGaps = chkGaps.Checked; preview.Invalidate(); };
            ab.Controls.Add(chkGaps);
            ab.Controls.Add(Hint("Select several photos with Ctrl+click or Shift+click, or drag a box around them from an empty spot. Ctrl+A selects all. " +
                                 "With one photo selected, Align uses the page margins. Hold Alt while dragging to turn snapping off. " +
                                 "Red lines show the gap to the nearest photo (and, with one photo selected, to the paper edges) in mm."));
            side.Controls.Add(ab);

            // All photos
            AddHeader(side, "ALL PHOTOS");
            side.Controls.Add(Row(34, null, Btn("Auto-rotate all", (s, e) => AllPhotos("autorot")), Btn("Reset rotation", (s, e) => AllPhotos("rot0"))));
            side.Controls.Add(Row(34, null, Btn("All: Fit", (s, e) => AllPhotos("fit")), Btn("All: Fill", (s, e) => AllPhotos("fill"))));
            side.Controls.Add(Row(34, null, btnResetAll = Btn("Reset all sizes", (s, e) => AllPhotos("size")), Btn("Clear all", (s, e) => AllPhotos("clear"))));

            side.Controls.Add(Hint("Print and Print preview are in the toolbar at the top (Ctrl+P)."));

            // Context menu
            ctx = new ContextMenuStrip();
            AddCtx("Rotate left", "rl"); AddCtx("Rotate right", "rr");
            ctx.Items.Add(new ToolStripSeparator());
            AddCtx("Fit whole photo", "fit"); AddCtx("Fill box (crop)", "fill"); AddCtx("Reset size && position", "reset");
            ctx.Items.Add(new ToolStripSeparator());
            AddCtx("Duplicate", "dup"); AddCtx("Repeat to fill page", "fillpage");
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add(new ToolStripSeparator());
            AddCtx("Bring to front", "front"); AddCtx("Send to back", "back");
            var selPage = new ToolStripMenuItem("Select all photos on this page");
            selPage.Click += (s, e) => SelectAll(true);
            ctx.Items.Add(selPage);
            ctx.Items.Add(new ToolStripSeparator());
            AddCtx("Remove", "del");
            ctx.Opening += (s, e) =>
            {
                foreach (var k in new[] { "fit", "fill", "fillpage" }) ctxItems[k].Visible = !free;
                foreach (var k in new[] { "front", "back" }) ctxItems[k].Visible = free;
            };
        }

        void AddCtx(string text, string action)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Click += (s, e) => Act(action);
            ctx.Items.Add(mi);
            ctxItems[action] = mi;
        }

        void AddHeader(Control parent, string text)
        {
            parent.Controls.Add(new Panel { Width = SideW, Height = 1, BackColor = LineCol, Margin = new Padding(0, D(12), 0, 0) });
            parent.Controls.Add(new Label
            {
                Text = text, UseMnemonic = false, Font = new Font("Segoe UI", 8.25f, FontStyle.Bold), ForeColor = Muted, AutoSize = false,
                Width = SideW, Height = D(26), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, D(4), 0, D(2))
            });
        }

        Label SubHeader(string text)
        {
            return new Label { Text = text, UseMnemonic = false, ForeColor = Muted, AutoSize = false, Width = SideW, Height = D(22), TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(0, D(4), 0, 0) };
        }

        Label Hint(string text)
        {
            var l = new Label { Text = text, UseMnemonic = false, AutoSize = false, Width = SideW, ForeColor = Muted, Font = new Font("Segoe UI", 8.25f), Margin = new Padding(0, D(4), 0, D(4)) };
            l.Height = TextRenderer.MeasureText(text, l.Font, new Size(SideW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + D(12);
            return l;
        }

        Label SmallLabel(string text) { return new Label { Text = text, TextAlign = ContentAlignment.MiddleLeft, AutoSize = false }; }
        Label CenterLabel(string text) { return new Label { Text = text, TextAlign = ContentAlignment.MiddleCenter, AutoSize = false }; }

        Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, UseMnemonic = false, FlatStyle = FlatStyle.Flat, BackColor = BtnBack, ForeColor = TextCol, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = LineCol;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 242, 255);
            if (click != null) b.Click += click;
            return b;
        }

        Button AB(string text, string how, string tip)
        {
            var b = Btn(text, (s, e) => Align(how));
            tips.SetToolTip(b, tip);
            return b;
        }

        Button PrimaryButton(string text)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI Semibold", 10f), Width = SideW, Height = D(40), Cursor = Cursors.Hand, Margin = new Padding(0, D(6), 0, D(2))
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(29, 78, 216);
            return b;
        }

        RadioButton Toggle(string text)
        {
            var rb = new RadioButton { Text = text, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter, BackColor = BtnBack, ForeColor = TextCol, Cursor = Cursors.Hand };
            rb.FlatAppearance.BorderColor = LineCol;
            rb.FlatAppearance.CheckedBackColor = Accent;
            rb.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 242, 255);
            rb.CheckedChanged += (s, e) => rb.ForeColor = rb.Checked ? Color.White : TextCol;
            return rb;
        }

        NumericUpDown Num(decimal min, decimal max, int decimals, decimal inc)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = inc, TextAlign = HorizontalAlignment.Right };
        }

        Control WithSuffix(NumericUpDown n, string suffix)
        {
            var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            n.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            n.Margin = new Padding(0);
            var l = new Label { Text = suffix, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Muted, Margin = new Padding(D(2), 0, 0, 0) };
            t.Controls.Add(n, 0, 0);
            t.Controls.Add(l, 1, 0);
            return t;
        }

        TableLayoutPanel Row(int height, float[] pct, params Control[] cs)
        {
            var t = new TableLayoutPanel { ColumnCount = cs.Length, RowCount = 1, Width = SideW, Height = D(height), Margin = new Padding(0, D(2), 0, D(2)), Padding = new Padding(0) };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < cs.Length; i++)
            {
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, pct == null ? 100f / cs.Length : pct[i]));
                var c = cs[i];
                if (c is NumericUpDown || c is ComboBox || c is CheckBox) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                else c.Dock = DockStyle.Fill;
                c.Margin = new Padding(i == 0 ? 0 : D(3), 0, i == cs.Length - 1 ? 0 : D(3), 0);
                t.Controls.Add(c, i, 0);
            }
            return t;
        }

        static void SetNum(NumericUpDown n, double v)
        {
            decimal d;
            try { d = (decimal)v; } catch { d = n.Minimum; }
            if (d < n.Minimum) d = n.Minimum;
            if (d > n.Maximum) d = n.Maximum;
            d = Math.Round(d, n.DecimalPlaces);
            if (n.Value != d) n.Value = d;
        }

        void SyncControls()
        {
            updating = true;
            rbPort.Checked = !landscape;
            rbLand.Checked = landscape;
            SetNum(nCols, cols); SetNum(nRows, rows);
            SetNum(nMargin, margin); SetNum(nGap, gap);
            cbGuides.SelectedIndex = guideMode;
            cbCaptions.SelectedIndex = captionMode;
            SetNum(nCapPt, captionPt);
            chkGaps.Checked = showGaps;
            SetNum(nAlignGap, alignGap);
            if (!free)
            {
                int si = 0;
                if (boxW > 0)
                {
                    si = SizeNames.Length - 1;
                    for (int i = 1; i < SizeNames.Length - 1; i++)
                        if ((Math.Abs(SizeMM[i, 0] - boxW) < 0.01 && Math.Abs(SizeMM[i, 1] - boxH) < 0.01) || (Math.Abs(SizeMM[i, 0] - boxH) < 0.01 && Math.Abs(SizeMM[i, 1] - boxW) < 0.01)) { si = i; break; }
                    SetNum(nBoxW, boxW / 10); SetNum(nBoxH, boxH / 10);
                }
                cbSize.SelectedIndex = si;
            }
            else if (cbSize.SelectedIndex < 0) cbSize.SelectedIndex = 0;
            rowCustomSize.Visible = cbSize.SelectedIndex == SizeNames.Length - 1;
            nCols.Enabled = nRows.Enabled = !free && boxW <= 0;
            for (int i = 0; i < presetButtons.Count; i++)
            {
                int c = PresetCR[i, 0], r = PresetCR[i, 1];
                if (landscape) { int t = c; c = r; r = t; }
                bool on = PresetN[i] == 0 ? free : (!free && boxW <= 0 && c == cols && r == rows);
                presetButtons[i].BackColor = on ? Accent : BtnBack;
                presetButtons[i].ForeColor = on ? Color.White : TextCol;
            }
            btnResetAll.Text = free ? "Auto-arrange all" : "Reset all sizes";
            updating = false;
            UpdateSel();
            UpdateInfo();
        }

        void UpdateInfo()
        {
            double w, h; CellSize(out w, out h);
            int n = items.Count, p = PrintPageCount;
            string counts = string.Format("{0} photo{1}  ·  {2} page{3}", n, n == 1 ? "" : "s", p, p == 1 ? "" : "s");
            if (free) lblInfo.Text = (n == 0 ? "No photos yet — click “Add photos”." : counts) + "  ·  Free layout: drag to move, drag the corner square to resize";
            else if (n == 0) lblInfo.Text = string.Format("No photos yet — click “Add photos”.   Each box {0:0} × {1:0} mm", w, h);
            else if (boxW > 0) lblInfo.Text = counts + string.Format("  ·  {0} per page, each photo {1:0.#} × {2:0.#} cm", PerPage, w / 10, h / 10);
            else lblInfo.Text = counts + string.Format("  ·  each box {0:0} × {1:0} mm", w, h);
            lblZoom.Text = Math.Round(zoom * 100) + "%";
        }

        void UpdateSel()
        {
            selList.RemoveAll(p => !items.Contains(p));
            if (sel != null && !items.Contains(sel)) sel = null;
            if (sel != null && !selList.Contains(sel)) { selList.Clear(); selList.Add(sel); }
            if (sel == null && selList.Count > 0) sel = selList[selList.Count - 1];
            bool has = sel != null;
            int n = selList.Count;
            selBox.Enabled = has;
            if (rowMode.Visible == free) rowMode.Visible = !free;
            if (rowSlider.Visible == free) rowSlider.Visible = !free;
            if (rowPos.Visible != free) rowPos.Visible = free;
            if (arrangeBox.Visible != free) arrangeBox.Visible = free;
            btnFillPage.Enabled = !free && n == 1;
            if (rowCaptionText.Visible != (captionMode == 2)) rowCaptionText.Visible = captionMode == 2;
            btnUndo.Enabled = undo.Count > 0;
            btnRedo.Enabled = redo.Count > 0;
            updating = true;
            if (has)
            {
                if (n > 1)
                    lblSel.Text = n + " photos selected — changes apply to all\nNumbers show the last one clicked: " + sel.Name;
                else
                    lblSel.Text = sel.Name + "\n" + sel.W + " × " + sel.H + " px  ·  rotated " + sel.Rot + "°" + (free ? "  ·  page " + (sel.FPage + 1) : "");
                txtCaption.Text = sel.Caption ?? "";
                if (free)
                {
                    SetNum(nWmm, sel.FW / 10);
                    SetNum(nHmm, FH(sel) / 10);
                    SetNum(nX, sel.FX / 10);
                    SetNum(nY, sel.FY / 10);
                }
                else
                {
                    rbFit.Checked = !sel.Fill;
                    rbFill.Checked = sel.Fill;
                    double W, H; CellSize(out W, out H);
                    var q = GetGeo(sel, W, H);
                    SetNum(nSize, sel.Scale * 100);
                    tbSize.Value = (int)Clamp(Math.Round(sel.Scale * 100), tbSize.Minimum, tbSize.Maximum);
                    SetNum(nWmm, q.EW / 10);
                    SetNum(nHmm, q.EH / 10);
                }
            }
            else
            {
                lblSel.Text = "Click a photo on the page to select it.\nCtrl+click or drag a box to select several.";
                rbFit.Checked = false;
                rbFill.Checked = false;
            }
            updating = false;
        }

        // ------------------------------------------------------------ printers
        void FillPrinters()
        {
            updating = true;
            cbPrinter.Items.Clear();
            string def = "";
            try { def = new PrinterSettings().PrinterName; } catch { }
            try { foreach (string p in PrinterSettings.InstalledPrinters) cbPrinter.Items.Add(p); } catch { }
            string want = (printerName != "" && cbPrinter.Items.Contains(printerName)) ? printerName : def;
            if (cbPrinter.Items.Contains(want)) cbPrinter.SelectedItem = want;
            else if (cbPrinter.Items.Count > 0) cbPrinter.SelectedIndex = 0;
            printerName = cbPrinter.SelectedItem as string ?? "";
            updating = false;
            FillPapers();
        }

        static PaperSize MakePaper(string name, int w, int h, PaperKind kind)
        {
            var p = new PaperSize(name, w, h);
            p.RawKind = (int)kind;
            return p;
        }

        void FillPapers()
        {
            updating = true;
            string keepName = paper != null ? paper.PaperName : savedPaperName;
            PaperKind keepKind = paper != null ? paper.Kind : PaperKind.Custom;
            cbPaper.Items.Clear();
            var list = new List<PaperSize>();
            if (printerName != "")
            {
                try
                {
                    var ps = new PrinterSettings { PrinterName = printerName };
                    foreach (PaperSize p in ps.PaperSizes) if (p.Width > 50 && p.Height > 50) list.Add(p);
                }
                catch { }
            }
            if (list.Count == 0)
            {
                list.Add(MakePaper("A4 210 x 297 mm", 827, 1169, PaperKind.A4));
                list.Add(MakePaper("A3 297 x 420 mm", 1169, 1654, PaperKind.A3));
                list.Add(MakePaper("A5 148 x 210 mm", 583, 827, PaperKind.A5));
                list.Add(MakePaper("Letter 8.5 x 11 in", 850, 1100, PaperKind.Letter));
                list.Add(MakePaper("10 x 15 cm (4 x 6 in)", 400, 600, PaperKind.Custom));
                list.Add(MakePaper("13 x 18 cm (5 x 7 in)", 500, 700, PaperKind.Custom));
            }
            foreach (var p in list) cbPaper.Items.Add(new PaperItem(p));
            int pick = -1;
            for (int i = 0; i < list.Count && pick < 0; i++) if (list[i].PaperName == keepName) pick = i;
            if (keepKind != PaperKind.Custom) for (int i = 0; i < list.Count && pick < 0; i++) if (list[i].Kind == keepKind) pick = i;
            for (int i = 0; i < list.Count && pick < 0; i++) if (list[i].Kind == PaperKind.A4) pick = i;
            if (pick < 0) pick = 0;
            cbPaper.SelectedIndex = pick;
            paper = list[pick];
            updating = false;
        }

        static PaperSize MatchPaper(PrinterSettings s, PaperSize want)
        {
            try
            {
                foreach (PaperSize p in s.PaperSizes) if (want.Kind != PaperKind.Custom && p.Kind == want.Kind) return p;
                foreach (PaperSize p in s.PaperSizes) if (p.PaperName == want.PaperName) return p;
                foreach (PaperSize p in s.PaperSizes) if (Math.Abs(p.Width - want.Width) <= 8 && Math.Abs(p.Height - want.Height) <= 8) return p;
            }
            catch { }
            return new PaperSize(want.PaperName, want.Width, want.Height);
        }

        // ------------------------------------------------------------ geometry
        double PW { get { return paper == null ? 210 : (landscape ? paper.Height : paper.Width) * 0.254; } }
        double PH { get { return paper == null ? 297 : (landscape ? paper.Width : paper.Height) * 0.254; } }
        int PerPage { get { int gc, gr; double cw, ch, ox, oy; GridMetrics(out gc, out gr, out cw, out ch, out ox, out oy); return gc * gr; } }
        int FreeLastPage { get { int m = 0; foreach (var p in items) if (p.FPage > m) m = p.FPage; return m; } }
        // pages shown on screen (free layout shows one extra empty page to drag photos onto)
        int PageCount { get { if (free) return items.Count == 0 ? 1 : FreeLastPage + 2; return Math.Max(1, (items.Count + PerPage - 1) / PerPage); } }
        // pages that get printed
        int PrintPageCount { get { if (free) return items.Count == 0 ? 1 : FreeLastPage + 1; return PageCount; } }
        float Ppm { get { return zoom * 96f * dpi / 25.4f; } }
        int LabelH { get { return D(26); } }
        int GapPx { get { return D(22); } }
        int Pad { get { return D(20); } }

        // height of the caption band under each photo (mm)
        double CapH { get { return captionMode == 0 ? 0 : captionPt * 0.3528 * 1.6; } }

        static int FitCount(double avail, double size, double gap) { return size <= 0 ? 0 : (int)Math.Floor((avail + gap + 1e-6) / (size + gap)); }

        // grid geometry in mm: columns, rows, full cell size (photo + caption band), top-left of the first cell
        void GridMetrics(out int gc, out int gr, out double cw, out double ch, out double ox, out double oy)
        {
            double cap = CapH;
            if (boxW > 0 && boxH > 0)
            {
                double aw = PW - 2 * margin, ah = PH - 2 * margin;
                int c1 = FitCount(aw, boxW, gap), r1 = FitCount(ah, boxH + cap, gap);
                int c2 = FitCount(aw, boxH, gap), r2 = FitCount(ah, boxW + cap, gap);
                if (c2 * r2 > c1 * r1) { cw = boxH; ch = boxW + cap; gc = c2; gr = r2; }
                else { cw = boxW; ch = boxH + cap; gc = c1; gr = r1; }
                gc = Math.Max(1, gc); gr = Math.Max(1, gr);
                ox = (PW - (gc * cw + (gc - 1) * gap)) / 2;
                oy = (PH - (gr * ch + (gr - 1) * gap)) / 2;
                return;
            }
            gc = cols; gr = rows;
            cw = (PW - 2 * margin - (gc - 1) * gap) / gc;
            ch = (PH - 2 * margin - (gr - 1) * gap) / gr;
            ox = margin; oy = margin;
        }

        // size of the photo area of one cell (without the caption band)
        void CellSize(out double w, out double h)
        {
            int gc, gr; double cw, ch, ox, oy;
            GridMetrics(out gc, out gr, out cw, out ch, out ox, out oy);
            w = Math.Max(1, cw);
            h = Math.Max(1, ch - CapH);
        }

        static void EffDims(Photo p, out double ew, out double eh)
        {
            bool sw = p.Rot % 180 != 0;
            ew = sw ? p.H : p.W;
            eh = sw ? p.W : p.H;
        }

        static Geo GetGeo(Photo p, double W, double H)
        {
            var q = new Geo();
            double ew, eh; EffDims(p, out ew, out eh);
            q.Base = p.Fill ? Math.Max(W / ew, H / eh) : Math.Min(W / ew, H / eh);
            double s = q.Base * p.Scale;
            q.IW = p.W * s; q.IH = p.H * s; q.EW = ew * s; q.EH = eh * s;
            q.MaxDx = Math.Abs(q.EW - W) / 2;
            q.MaxDy = Math.Abs(q.EH - H) / 2;
            q.Dx = Clamp(p.Dx, -q.MaxDx, q.MaxDx);
            q.Dy = Clamp(p.Dy, -q.MaxDy, q.MaxDy);
            return q;
        }

        // ---- free layout helpers
        static double FH(Photo p) { double ew, eh; EffDims(p, out ew, out eh); return p.FW * eh / ew; }

        void ClampFree(Photo p)
        {
            double ew, eh; EffDims(p, out ew, out eh);
            double maxW = Math.Min(PW, PH * ew / eh);
            p.FW = Clamp(p.FW, 3, maxW);
            p.FX = Clamp(p.FX, 0, PW - p.FW);
            p.FY = Clamp(p.FY, 0, PH - FH(p));
            if (p.FPage < 0) p.FPage = 0;
        }

        // default size = fits a quarter of the printable area
        void DefaultFreeSize(Photo p)
        {
            double ew, eh; EffDims(p, out ew, out eh);
            double bw = Math.Max(10, (PW - 2 * margin - gap) / 2), bh = Math.Max(10, (PH - 2 * margin - gap) / 2);
            p.FW = ew * Math.Min(bw / ew, bh / eh);
        }

        // place photos in rows inside the margins, starting below the photos already on the last page
        void PackFree(IList<Photo> list, bool fromStart)
        {
            double m = margin, pw = PW, ph = PH;
            int page = 0; double x = m, y = m, rowH = 0;
            if (!fromStart)
            {
                var others = items.Where(o => o.FreeSet && !list.Contains(o)).ToList();
                if (others.Count > 0)
                {
                    page = others.Max(o => o.FPage);
                    double bottom = 0;
                    foreach (var o in others) if (o.FPage == page) bottom = Math.Max(bottom, o.FY + FH(o));
                    y = Math.Max(m, bottom + gap);
                }
            }
            foreach (var p in list)
            {
                DefaultFreeSize(p);
                double h = FH(p);
                if (x > m && x + p.FW > pw - m + 0.01) { x = m; y += rowH + gap; rowH = 0; }
                if (y > m && y + h > ph - m + 0.01) { page++; x = m; y = m; rowH = 0; }
                p.FX = x; p.FY = y; p.FPage = page; p.FreeSet = true;
                x += p.FW + gap; rowH = Math.Max(rowH, h);
            }
        }

        void CompactFreePages()
        {
            var used = items.Select(o => o.FPage).Distinct().OrderBy(v => v).ToList();
            foreach (var p in items) p.FPage = used.IndexOf(p.FPage);
        }

        // switch to free layout; photos keep the place they had in the grid
        void EnterFree()
        {
            if (!free)
            {
                PushUndo("");
                double W, H; CellSize(out W, out H);
                int per = PerPage;
                for (int i = 0; i < items.Count; i++)
                {
                    var p = items[i];
                    if (p.FreeSet) continue;
                    var fit = p.Copy(); fit.Fill = false;
                    var q = GetGeo(fit, W, H);
                    var cell = CellRectMm(i % per);
                    p.FW = q.EW;
                    p.FX = cell.X + W / 2 + q.Dx - q.EW / 2;
                    p.FY = cell.Y + H / 2 + q.Dy - q.EH / 2;
                    p.FPage = i / per;
                    p.FreeSet = true;
                }
                free = true;
                CompactFreePages();
            }
            SyncControls();
            AfterChange();
        }

        RectangleF FreeRectPx(Photo p)
        {
            var pr = PageRectPx(p.FPage);
            float k = Ppm;
            return new RectangleF(pr.X + (float)p.FX * k, pr.Y + (float)p.FY * k, (float)p.FW * k, (float)FH(p) * k);
        }

        int FreeHit(PointF wp)
        {
            for (int i = items.Count - 1; i >= 0; i--) if (FreeRectPx(items[i]).Contains(wp)) return i;
            return -1;
        }

        int PageAt(PointF wp)
        {
            int n = PageCount;
            for (int p = 0; p < n; p++)
            {
                var r = PageRectPx(p);
                r.Inflate(0, GapPx / 2f + LabelH / 2f);
                if (r.Contains(wp)) return p;
            }
            return -1;
        }

        // index of the photo under the mouse in either layout, or -1
        int PhotoAt(Point client)
        {
            if (free) return FreeHit(World(client));
            int h = HitTest(client);
            return h < items.Count ? h : -1;
        }

        void ScaleFree(Photo p, double f)
        {
            double cx = p.FX + p.FW / 2, cy = p.FY + FH(p) / 2;
            p.FW *= f;
            p.FX = cx - p.FW / 2; p.FY = cy - FH(p) / 2;
            ClampFree(p);
        }

        void RotateFree(Photo p, int delta)
        {
            double cx = p.FX + p.FW / 2, cy = p.FY + FH(p) / 2;
            double ew, eh; EffDims(p, out ew, out eh);
            double s = p.FW / ew;
            p.Rot = (p.Rot + delta) % 360;
            EffDims(p, out ew, out eh);
            p.FW = ew * s;
            p.FX = cx - p.FW / 2; p.FY = cy - FH(p) / 2;
            ClampFree(p);
        }

        RectangleF PageRectPx(int p)
        {
            float pw = (float)(PW * Ppm), ph = (float)(PH * Ppm);
            float x = Math.Max(Pad, (preview.ClientSize.Width - pw) / 2f);
            float y = Pad + LabelH + p * (ph + LabelH + GapPx);
            return new RectangleF(x, y, pw, ph);
        }

        // photo area of a grid cell (mm on the page)
        RectangleF CellRectMm(int slot)
        {
            int gc, gr; double cw, ch, ox, oy;
            GridMetrics(out gc, out gr, out cw, out ch, out ox, out oy);
            int c = slot % gc, r = slot / gc;
            return new RectangleF((float)(ox + c * (cw + gap)), (float)(oy + r * (ch + gap)), (float)Math.Max(1, cw), (float)Math.Max(1, ch - CapH));
        }

        // whole cell including the caption band
        RectangleF FullCellRectMm(int slot) { var r = CellRectMm(slot); r.Height += (float)CapH; return r; }

        int PageOf(int i) { return free ? items[i].FPage : i / PerPage; }

        // where photo i lands on its page (mm), clipped to its box
        RectangleF PhotoRectMm(int i)
        {
            var p = items[i];
            if (free) return RectangleF.Intersect(new RectangleF((float)p.FX, (float)p.FY, (float)p.FW, (float)FH(p)), new RectangleF(0, 0, (float)PW, (float)PH));
            double W, H; CellSize(out W, out H);
            var cell = CellRectMm(i % PerPage);
            var q = GetGeo(p, W, H);
            float cx = (float)(cell.X + cell.Width / 2 + q.Dx), cy = (float)(cell.Y + cell.Height / 2 + q.Dy);
            return RectangleF.Intersect(new RectangleF(cx - (float)q.EW / 2, cy - (float)q.EH / 2, (float)q.EW, (float)q.EH), cell);
        }

        // print resolution of photo i in dots per inch
        double EffDpi(int i)
        {
            var p = items[i];
            double mmW;   // printed width of the whole (unrotated) image
            if (free) { double ew, eh; EffDims(p, out ew, out eh); mmW = p.W * (p.FW / ew); }
            else { double W, H; CellSize(out W, out H); mmW = GetGeo(p, W, H).IW; }
            return mmW <= 0 ? 0 : p.W / (mmW / 25.4);
        }

        string CaptionText(Photo p)
        {
            if (captionMode == 1) return Path.GetFileNameWithoutExtension(p.Name);
            if (captionMode == 2) return p.Caption ?? "";
            return "";
        }

        // printer's unprintable edge for the current printer, paper and orientation
        void RefreshHardMargins()
        {
            hmL = hmT = hmR = hmB = 0;
            try
            {
                if (string.IsNullOrEmpty(printerName) || paper == null) return;
                var prs = new PrinterSettings { PrinterName = printerName };
                if (!prs.IsValid) return;
                var pg = new PageSettings(prs) { Landscape = landscape };
                pg.PaperSize = MatchPaper(prs, paper);
                RectangleF pa = pg.PrintableArea;
                float bw = (float)(PW / 0.254), bh = (float)(PH / 0.254);
                if ((pa.Width > pa.Height) != (bw > bh) && Math.Abs(bw - bh) > 1) pa = new RectangleF(pa.Y, pa.X, pa.Height, pa.Width);
                hmL = Math.Max(0, pa.X * 0.254); hmT = Math.Max(0, pa.Y * 0.254);
                hmR = Math.Max(0, (bw - pa.X - pa.Width) * 0.254); hmB = Math.Max(0, (bh - pa.Y - pa.Height) * 0.254);
                if (hmL > 30 || hmT > 30 || hmR > 30 || hmB > 30) hmL = hmT = hmR = hmB = 0;   // nonsense from the driver
            }
            catch { hmL = hmT = hmR = hmB = 0; }
        }

        double MaxHardMargin { get { return Math.Max(Math.Max(hmL, hmR), Math.Max(hmT, hmB)); } }

        void UpdateMarginWarning()
        {
            double mx = MaxHardMargin;
            bool show = mx > 0.05 && margin + 0.01 < mx;
            if (show) lblMarginWarn.Text = string.Format("This printer can't print the outer {0:0.0} mm of this paper (hatched on the page). Photos closer to the edge will be cut.", mx);
            if (lblMarginWarn.Visible != show) lblMarginWarn.Visible = show;
        }

        RectangleF CellRectPx(int index)
        {
            int p = index / PerPage, slot = index % PerPage;
            var pr = PageRectPx(p);
            var c = CellRectMm(slot);
            float k = Ppm;
            return new RectangleF(pr.X + c.X * k, pr.Y + c.Y * k, c.Width * k, c.Height * k);
        }

        RectangleF VisibleImageRect(int index)
        {
            if (free) { var fp = items[index]; return RectangleF.Intersect(FreeRectPx(fp), PageRectPx(fp.FPage)); }
            var cr = CellRectPx(index);
            double W, H; CellSize(out W, out H);
            var q = GetGeo(items[index], W, H);
            float k = Ppm;
            float cx = (float)(cr.X + cr.Width / 2 + q.Dx * k), cy = (float)(cr.Y + cr.Height / 2 + q.Dy * k);
            float ew = (float)(q.EW * k), eh = (float)(q.EH * k);
            return RectangleF.Intersect(new RectangleF(cx - ew / 2, cy - eh / 2, ew, eh), cr);
        }

        RectangleF HandleRect(int index)
        {
            var vr = VisibleImageRect(index);
            float hs = D(12);
            return new RectangleF(vr.Right - hs / 2, vr.Bottom - hs / 2, hs, hs);
        }

        PointF World(Point client) { return new PointF(client.X - preview.AutoScrollPosition.X, client.Y - preview.AutoScrollPosition.Y); }

        int HitTest(Point client)
        {
            var pt = World(client);
            int total = PageCount * PerPage;
            for (int i = 0; i < total; i++) if (CellRectPx(i).Contains(pt)) return i;
            return -1;
        }

        bool OnHandle(PointF wp)
        {
            int si = sel == null ? -1 : items.IndexOf(sel);
            if (si < 0 || selList.Count != 1) return false;
            var r = HandleRect(si);
            r.Inflate(D(5), D(5));
            return r.Contains(wp);
        }

        void UpdateScroll()
        {
            float pw = (float)(PW * Ppm), ph = (float)(PH * Ppm);
            preview.AutoScrollMinSize = new Size((int)(pw + 2 * Pad), (int)(Pad * 2 + PageCount * (ph + LabelH + GapPx)));
        }

        void AfterChange()
        {
            if (free) foreach (var p in items) ClampFree(p);
            UpdateScroll();
            preview.Invalidate();
            UpdateSel();
            UpdateInfo();
        }

        void FitZoom()
        {
            RefreshHardMargins();
            UpdateMarginWarning();
            float b0 = 96f * dpi / 25.4f;
            float zw = (preview.ClientSize.Width - 2 * Pad - SystemInformation.VerticalScrollBarWidth) / (float)(PW * b0);
            float zh = (preview.ClientSize.Height - 2 * Pad - LabelH) / (float)(PH * b0);
            zoom = (float)Clamp(Math.Min(zw, zh), 0.1, 4);
            AfterChange();
        }

        // ------------------------------------------------------------- drawing
        static void DrawPhoto(Graphics g, Photo p, Image img, RectangleF cell, double W, double H, float u)
        {
            var q = GetGeo(p, W, H);
            var st = g.Save();
            g.SetClip(cell, CombineMode.Intersect);
            g.TranslateTransform((float)(cell.X + cell.Width / 2 + q.Dx * u), (float)(cell.Y + cell.Height / 2 + q.Dy * u));
            g.RotateTransform(p.Rot);
            float iw = (float)(q.IW * u), ih = (float)(q.IH * u);
            var pts = new[] { new PointF(-iw / 2, -ih / 2), new PointF(iw / 2, -ih / 2), new PointF(-iw / 2, ih / 2) };
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, pts, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            }
            g.Restore(st);
        }

        static void DrawFree(Graphics g, Photo p, Image img, float ox, float oy, float u)
        {
            double ew, eh; EffDims(p, out ew, out eh);
            double s = p.FW / ew;
            float iw = (float)(p.W * s * u), ih = (float)(p.H * s * u);
            var st = g.Save();
            g.TranslateTransform(ox + (float)((p.FX + p.FW / 2) * u), oy + (float)((p.FY + FH(p) / 2) * u));
            g.RotateTransform(p.Rot);
            var pts = new[] { new PointF(-iw / 2, -ih / 2), new PointF(iw / 2, -ih / 2), new PointF(-iw / 2, ih / 2) };
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, pts, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            }
            g.Restore(st);
        }

        // Draws one page's photos, cut guides and captions. ox/oy = page origin, u = drawing units per mm.
        // full = full-resolution images (printing and PDF); otherwise the screen previews are used.
        void RenderPageContent(Graphics g, int page, float ox, float oy, float u, bool full)
        {
            double W, H; CellSize(out W, out H);
            var idx = new List<int>();
            for (int i = 0; i < items.Count; i++) if (PageOf(i) == page) idx.Add(i);
            if (guideMode == 2)
            {
                using (var pen = new Pen(Color.Black, full ? 0.2f * u : Math.Max(1f, 0.2f * u)))
                    foreach (var i in idx) DrawCropMarks(g, pen, PhotoRectMm(i), ox, oy, u);
            }
            foreach (var i in idx)
            {
                var p = items[i];
                Image img = p.Preview;
                LoadResult r = null;
                if (full)
                {
                    int need;
                    if (free) { double ew, eh; EffDims(p, out ew, out eh); need = (int)Math.Ceiling(Math.Max(p.W, p.H) * (p.FW / ew) / 25.4 * 360); }
                    else { var q = GetGeo(p, W, H); need = (int)Math.Ceiling(Math.Max(q.IW, q.IH) / 25.4 * 360); }   // ~360 dpi is plenty for inkjet photos
                    r = LoadPhoto(p, Math.Max(need, 64));
                    if (r != null) img = r.Bmp;
                }
                if (img != null)
                {
                    if (free) DrawFree(g, p, img, ox, oy, u);
                    else
                    {
                        var c = CellRectMm(i % PerPage);
                        DrawPhoto(g, p, img, new RectangleF(ox + c.X * u, oy + c.Y * u, c.Width * u, c.Height * u), W, H, u);
                    }
                }
                if (r != null) r.Bmp.Dispose();
            }
            if (guideMode == 1)
            {
                using (var pen = new Pen(Color.FromArgb(150, 150, 150), full ? 0.2f * u : Math.Max(1f, 0.25f * u)))
                    foreach (var i in idx)
                    {
                        var p = items[i];
                        RectangleF c = free ? new RectangleF((float)p.FX, (float)p.FY, (float)p.FW, (float)(FH(p) + CapH)) : FullCellRectMm(i % PerPage);
                        g.DrawRectangle(pen, ox + c.X * u, oy + c.Y * u, c.Width * u, c.Height * u);
                    }
            }
            if (captionMode != 0)
            {
                float em = (float)(captionPt * 0.3528 * u);
                if (em < 2f && !full) return;   // too small to read on screen
                using (var font = new Font("Segoe UI", em, FontStyle.Regular, GraphicsUnit.World))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    foreach (var i in idx)
                    {
                        var p = items[i];
                        string t = CaptionText(p);
                        if (t.Length == 0) continue;
                        RectangleF c;
                        if (free) c = new RectangleF((float)p.FX, (float)(p.FY + FH(p)), (float)p.FW, (float)CapH);
                        else { var cr = CellRectMm(i % PerPage); c = new RectangleF(cr.X, cr.Bottom, cr.Width, (float)CapH); }
                        g.DrawString(t, font, Brushes.Black, new RectangleF(ox + c.X * u, oy + c.Y * u, c.Width * u, c.Height * u), sf);
                    }
            }
        }

        static LoadResult LoadPhoto(Photo p, int maxSide)
        {
            return p.PdfPage >= 0 ? PdfLoader.Load(p.Path, p.PdfPage, maxSide) : Loader.LoadAny(p.Path, maxSide);
        }

        static int SpecPage(string[] f) { int v; return f.Length >= 14 && int.TryParse(f[13], out v) ? v : -1; }

        static void DrawCropMarks(Graphics g, Pen pen, RectangleF r, float ox, float oy, float u)
        {
            float L = 4f * u, o = 1.5f * u;
            float x1 = ox + r.Left * u, x2 = ox + r.Right * u, y1 = oy + r.Top * u, y2 = oy + r.Bottom * u;
            g.DrawLine(pen, x1 - o - L, y1, x1 - o, y1); g.DrawLine(pen, x2 + o, y1, x2 + o + L, y1);
            g.DrawLine(pen, x1 - o - L, y2, x1 - o, y2); g.DrawLine(pen, x2 + o, y2, x2 + o + L, y2);
            g.DrawLine(pen, x1, y1 - o - L, x1, y1 - o); g.DrawLine(pen, x2, y1 - o - L, x2, y1 - o);
            g.DrawLine(pen, x1, y2 + o, x1, y2 + o + L); g.DrawLine(pen, x2, y2 + o, x2, y2 + o + L);
        }

        // hatched band where the printer cannot print
        void DrawEdgeBand(Graphics g, float ox, float oy, float u)
        {
            if (hmL + hmT + hmR + hmB <= 0.01) return;
            float w = (float)PW * u, h = (float)PH * u;
            using (var hb = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(150, 140, 140, 140), Color.FromArgb(50, 210, 210, 210)))
            using (var region = new Region(new RectangleF(ox, oy, w, h)))
            {
                region.Exclude(new RectangleF(ox + (float)hmL * u, oy + (float)hmT * u, w - (float)(hmL + hmR) * u, h - (float)(hmT + hmB) * u));
                g.FillRegion(hb, region);
            }
        }

        static void SetQuality(Graphics g)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        }

        // ---- gap measurements (free layout)
        public class GapLine { public int Dir; public double Mm; public double X1, Y1, X2, Y2; public bool Edge; }

        // gaps around photo i in mm. Dir 0 = right, 1 = left, 2 = above, 3 = below.
        // Measured to the nearest photo that overlaps in the other direction, otherwise to the paper edge (Edge = true).
        List<GapLine> FindGaps(int i)
        {
            var res = new List<GapLine>();
            var p = items[i];
            double L = p.FX, T = p.FY, R = p.FX + p.FW, B = p.FY + FH(p);
            for (int dir = 0; dir < 4; dir++)
            {
                double best = double.MaxValue;
                int bj = -1;
                for (int j = 0; j < items.Count; j++)
                {
                    if (j == i || items[j].FPage != p.FPage) continue;
                    var q = items[j];
                    double l = q.FX, t = q.FY, r = q.FX + q.FW, b = q.FY + FH(q), ov, d;
                    if (dir < 2) { ov = Math.Min(B, b) - Math.Max(T, t); d = dir == 0 ? l - R : L - r; }
                    else { ov = Math.Min(R, r) - Math.Max(L, l); d = dir == 2 ? T - b : t - B; }
                    if (ov <= 0.01 || d < -0.01) continue;
                    if (d < best) { best = d; bj = j; }
                }
                var g = new GapLine { Dir = dir };
                if (bj >= 0)
                {
                    var q = items[bj];
                    double l = q.FX, t = q.FY, r = q.FX + q.FW, b = q.FY + FH(q);
                    g.Mm = Math.Max(0, best);
                    if (dir < 2) { g.Y1 = g.Y2 = (Math.Max(T, t) + Math.Min(B, b)) / 2; g.X1 = dir == 0 ? R : r; g.X2 = dir == 0 ? l : L; }
                    else { g.X1 = g.X2 = (Math.Max(L, l) + Math.Min(R, r)) / 2; g.Y1 = dir == 2 ? b : B; g.Y2 = dir == 2 ? T : t; }
                }
                else
                {
                    g.Edge = true;
                    double cy = (T + B) / 2, cx = (L + R) / 2;
                    if (dir == 0) { g.Mm = PW - R; g.X1 = R; g.X2 = PW; g.Y1 = g.Y2 = cy; }
                    else if (dir == 1) { g.Mm = L; g.X1 = 0; g.X2 = L; g.Y1 = g.Y2 = cy; }
                    else if (dir == 2) { g.Mm = T; g.Y1 = 0; g.Y2 = T; g.X1 = g.X2 = cx; }
                    else { g.Mm = PH - B; g.Y1 = B; g.Y2 = PH; g.X1 = g.X2 = cx; }
                    if (g.Mm < 0.05) continue;
                }
                res.Add(g);
            }
            return res;
        }

        void DrawGaps(Graphics g, float k)
        {
            if (!free || !showGaps || marquee) return;
            var who = new List<int>();
            if (drag == DragMode.Resize) { if (sel != null) who.Add(items.IndexOf(sel)); }
            else foreach (var sp in selList) who.Add(items.IndexOf(sp));
            if (who.Count == 0) return;
            bool single = who.Count == 1;
            var done = new HashSet<string>();
            using (var pen = new Pen(Color.FromArgb(225, 29, 72), 1))
            using (var penEdge = new Pen(Color.FromArgb(170, 225, 29, 72), 1) { DashStyle = DashStyle.Dash })
            using (var font = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var bg = new SolidBrush(Color.FromArgb(225, 29, 72)))
            using (var bgEdge = new SolidBrush(Color.FromArgb(215, 140, 140, 150)))
            {
                foreach (int i in who)
                {
                    if (i < 0) continue;
                    var pr = PageRectPx(items[i].FPage);
                    foreach (var gl in FindGaps(i))
                    {
                        if (gl.Edge && !single) continue;
                        string key = string.Format(CultureInfo.InvariantCulture, "{0:0.0}|{1:0.0}|{2:0.0}|{3:0.0}|{4}", Math.Min(gl.X1, gl.X2), Math.Min(gl.Y1, gl.Y2), Math.Max(gl.X1, gl.X2), Math.Max(gl.Y1, gl.Y2), items[i].FPage);
                        if (!done.Add(key)) continue;   // the same gap seen from both photos
                        float x1 = pr.X + (float)gl.X1 * k, y1 = pr.Y + (float)gl.Y1 * k, x2 = pr.X + (float)gl.X2 * k, y2 = pr.Y + (float)gl.Y2 * k;
                        bool horiz = gl.Dir < 2;
                        var pn = gl.Edge ? penEdge : pen;
                        g.DrawLine(pn, x1, y1, x2, y2);
                        float tk = D(4);
                        if (horiz) { g.DrawLine(pen, x1, y1 - tk, x1, y1 + tk); g.DrawLine(pen, x2, y2 - tk, x2, y2 + tk); }
                        else { g.DrawLine(pen, x1 - tk, y1, x1 + tk, y1); g.DrawLine(pen, x2 - tk, y2, x2 + tk, y2); }
                        string t = gl.Mm < 0.05 ? "touching" : gl.Mm.ToString("0.0") + " mm";
                        var sz = g.MeasureString(t, font);
                        float cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
                        var box = horiz ? new RectangleF(cx - sz.Width / 2 - D(3), cy - sz.Height - D(3), sz.Width + D(6), sz.Height + D(1))
                                        : new RectangleF(cx + D(5), cy - sz.Height / 2, sz.Width + D(6), sz.Height + D(1));
                        g.FillRectangle(gl.Edge ? bgEdge : bg, box);
                        g.DrawString(t, font, Brushes.White, box.X + D(3), box.Y);
                    }
                }
            }
        }

        void DrawSizeLabel(Graphics g, int index, bool showPos)
        {
            var p = items[index];
            var vr = VisibleImageRect(index);
            double wmm, hmm;
            if (free) { wmm = p.FW; hmm = FH(p); }
            else { wmm = vr.Width / Ppm; hmm = vr.Height / Ppm; }   // printed (visible) part
            string t = string.Format("{0:0.0} × {1:0.0} cm  ·  {2:0} dpi", wmm / 10, hmm / 10, EffDpi(index));
            if (showPos) t += string.Format("\nfrom edge: {0:0.0} cm left, {1:0.0} cm top", p.FX / 10, p.FY / 10);
            var sz = g.MeasureString(t, Font);
            var box = new RectangleF(vr.X + D(5), vr.Y + D(5), sz.Width + D(8), sz.Height + D(4));
            using (var b = new SolidBrush(Color.FromArgb(215, 17, 24, 39))) g.FillRectangle(b, box);
            g.DrawString(t, Font, Brushes.White, box.X + D(4), box.Y + D(2));
        }

        RectangleF MarqRect()
        {
            return RectangleF.FromLTRB(Math.Min(marqA.X, marqB.X), Math.Min(marqA.Y, marqB.Y), Math.Max(marqA.X, marqB.X), Math.Max(marqA.Y, marqB.Y));
        }

        void PreviewPaint(object s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TranslateTransform(preview.AutoScrollPosition.X, preview.AutoScrollPosition.Y);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var clip = e.ClipRectangle;
            clip.Offset(-preview.AutoScrollPosition.X, -preview.AutoScrollPosition.Y);
            int n = PageCount, per = PerPage, nPrint = PrintPageCount;
            float k = Ppm;
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var mutedBrush = new SolidBrush(Muted))
            using (var emptyBrush = new SolidBrush(Color.FromArgb(154, 163, 178)))
            using (var shadow = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
            using (var dash = new Pen(Color.FromArgb(197, 203, 214), 1) { DashStyle = DashStyle.Dash })
            using (var guide = new Pen(Color.FromArgb(147, 197, 253), 1) { DashStyle = DashStyle.Dot })
            using (var gridPen = new Pen(Color.FromArgb(228, 233, 242), 1))
            using (var badgeFont = new Font("Segoe UI", 8f, FontStyle.Bold))
            {
                for (int p = 0; p < n; p++)
                {
                    var pr = PageRectPx(p);
                    if (pr.Bottom < clip.Top - LabelH || pr.Top - LabelH > clip.Bottom) continue;
                    g.FillRectangle(shadow, pr.X + D(2), pr.Y + D(3), pr.Width, pr.Height);
                    g.FillRectangle(Brushes.White, pr);
                    string label = (free && p >= nPrint) ? "Extra page (not printed unless you put a photo on it)" : "Page " + (p + 1) + " of " + nPrint;
                    g.DrawString(label, Font, mutedBrush, new RectangleF(pr.X - D(150), pr.Y - LabelH, pr.Width + D(300), LabelH), center);
                    var st = g.Save();
                    g.SetClip(pr, CombineMode.Intersect);
                    bool any = false;
                    if (free)
                    {
                        if (showGrid)
                        {
                            for (double x = 10; x < PW; x += 10) g.DrawLine(gridPen, pr.X + (float)x * k, pr.Y, pr.X + (float)x * k, pr.Bottom);
                            for (double y = 10; y < PH; y += 10) g.DrawLine(gridPen, pr.X, pr.Y + (float)y * k, pr.Right, pr.Y + (float)y * k);
                        }
                        if (margin > 0)
                            g.DrawRectangle(guide, pr.X + (float)margin * k, pr.Y + (float)margin * k, pr.Width - 2 * (float)margin * k, pr.Height - 2 * (float)margin * k);
                        foreach (var ph in items) if (ph.FPage == p) { any = true; break; }
                    }
                    else
                    {
                        for (int sl = 0; sl < per; sl++)
                        {
                            int idx = p * per + sl;
                            if (idx < items.Count) continue;
                            var cr = CellRectPx(idx);
                            g.DrawRectangle(dash, cr.X + 1, cr.Y + 1, cr.Width - 2, cr.Height - 2);
                            g.DrawString("+ Add photo", Font, emptyBrush, cr, center);
                        }
                    }
                    RenderPageContent(g, p, pr.X, pr.Y, k, false);
                    if (showEdge) DrawEdgeBand(g, pr.X, pr.Y, k);
                    g.Restore(st);
                    if (free && !any) g.DrawString(items.Count == 0 ? "Double-click or drop photos here" : "Drag a photo here, or double-click to add photos", Font, emptyBrush, pr, center);
                    // low-resolution badges (screen only)
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (PageOf(i) != p) continue;
                        double d = EffDpi(i);
                        if (d >= 150 || d <= 0) continue;
                        var r = PhotoRectMm(i);
                        var vr = new RectangleF(pr.X + r.X * k, pr.Y + r.Y * k, r.Width * k, r.Height * k);
                        string t = "Low resolution " + Math.Round(d) + " dpi";
                        var sz = g.MeasureString(t, badgeFont);
                        var box = new RectangleF(vr.Right - sz.Width - D(10), vr.Top + D(5), sz.Width + D(6), sz.Height + D(2));
                        if (box.X < vr.X + D(2)) box.X = vr.X + D(2);
                        using (var b = new SolidBrush(d < 100 ? Color.FromArgb(235, 220, 38, 38) : Color.FromArgb(235, 217, 119, 6))) g.FillRectangle(b, box);
                        g.DrawString(t, badgeFont, Brushes.White, box.X + D(3), box.Y + D(1));
                    }
                }
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var selPen = new Pen(Accent, D(2)))
            using (var dashSel = new Pen(Accent, 1) { DashStyle = DashStyle.Dash })
            {
                foreach (var sp in selList)
                {
                    int i = items.IndexOf(sp);
                    if (i < 0) continue;
                    var vr = VisibleImageRect(i);
                    if (free) g.DrawRectangle(selPen, vr.X, vr.Y, vr.Width, vr.Height);
                    else
                    {
                        var cr = CellRectPx(i);
                        g.DrawRectangle(selPen, cr.X, cr.Y, cr.Width, cr.Height);
                        if (sp == sel) g.DrawRectangle(dashSel, vr.X, vr.Y, vr.Width, vr.Height);
                    }
                }
            }
            int si = sel == null ? -1 : items.IndexOf(sel);
            if (si >= 0 && selList.Count == 1)
            {
                var hr = HandleRect(si);
                g.FillRectangle(Brushes.White, hr);
                using (var pen = new Pen(Accent, D(2))) g.DrawRectangle(pen, hr.X, hr.Y, hr.Width, hr.Height);
            }
            if (dropTarget >= 0)
            {
                var cr = CellRectPx(dropTarget);
                using (var b = new SolidBrush(Color.FromArgb(50, Accent))) g.FillRectangle(b, cr);
                using (var pen = new Pen(Accent, D(3))) g.DrawRectangle(pen, cr.X, cr.Y, cr.Width, cr.Height);
            }
            g.SmoothingMode = SmoothingMode.None;
            if (guides.Count > 0)
            {
                using (var gp = new Pen(Color.FromArgb(236, 72, 153), 1) { DashStyle = DashStyle.Dot })
                {
                    foreach (var gd in guides)
                    {
                        var pr = PageRectPx((int)gd[2]);
                        if (gd[0] == 1) { float x = pr.X + (float)gd[1] * k; g.DrawLine(gp, x, pr.Top, x, pr.Bottom); }
                        else { float y = pr.Y + (float)gd[1] * k; g.DrawLine(gp, pr.Left, y, pr.Right, y); }
                    }
                }
            }
            DrawGaps(g, k);
            if (marquee)
            {
                var r = MarqRect();
                using (var b = new SolidBrush(Color.FromArgb(28, Accent))) g.FillRectangle(b, r);
                using (var pen = new Pen(Accent, 1) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }
            int li = (drag != DragMode.None && dragMoved) ? si : hoverIndex;
            if (!marquee && li >= 0 && li < items.Count && dropTarget < 0) DrawSizeLabel(g, li, free && drag == DragMode.Move && dragMoved);
        }

        // --------------------------------------------------------------- mouse
        void Select(Photo p)
        {
            selList.Clear();
            if (p != null) selList.Add(p);
            sel = p;
            preview.Invalidate();
            UpdateSel();
        }

        void ToggleSel(Photo p)
        {
            if (selList.Contains(p))
            {
                selList.Remove(p);
                if (sel == p) sel = selList.Count > 0 ? selList[selList.Count - 1] : null;
            }
            else { selList.Add(p); sel = p; }
            preview.Invalidate();
            UpdateSel();
        }

        void AddSel(Photo p)
        {
            if (!selList.Contains(p)) selList.Add(p);
            sel = p;
            preview.Invalidate();
            UpdateSel();
        }

        void SelectAll(bool pageOnly)
        {
            int page = sel == null ? 0 : (free ? sel.FPage : items.IndexOf(sel) / PerPage);
            selList.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                int pg = free ? items[i].FPage : i / PerPage;
                if (!pageOnly || pg == page) selList.Add(items[i]);
            }
            if (sel == null || !selList.Contains(sel)) sel = selList.Count > 0 ? selList[0] : null;
            preview.Invalidate();
            UpdateSel();
        }

        // selected photos in layout order
        List<Photo> Targets() { return items.Where(p => selList.Contains(p)).ToList(); }

        void PreviewMouseDown(object s, MouseEventArgs e)
        {
            var wp = World(e.Location);
            bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
            pendingSingle = false;
            if (e.Button == MouseButtons.Left && !ctrl && !shift && OnHandle(wp))
            {
                int si = items.IndexOf(sel);
                drag = DragMode.Resize;
                dragMoved = false;
                dragIndex = si;
                dragStart = e.Location;
                dragScale = sel.Scale;
                if (!free)
                {
                    var cr = CellRectPx(si);
                    double W, H; CellSize(out W, out H);
                    var q = GetGeo(sel, W, H);
                    dragCenter = new PointF((float)(cr.X + cr.Width / 2 + q.Dx * Ppm), (float)(cr.Y + cr.Height / 2 + q.Dy * Ppm));
                }
                return;
            }
            int hit = free ? FreeHit(wp) : HitTest(e.Location);
            bool onPhoto = hit >= 0 && hit < items.Count;
            if (!onPhoto)
            {
                if (!free && hit >= items.Count && e.Button == MouseButtons.Left && !ctrl && !shift) { Select(null); BrowseAdd(); return; }
                if (e.Button == MouseButtons.Left)
                {
                    marquee = true;
                    marqA = marqB = wp;
                    marqBase = (ctrl || shift) ? new List<Photo>(selList) : new List<Photo>();
                    if (!ctrl && !shift) Select(null);
                }
                else Select(null);
                return;
            }
            var ph = items[hit];
            if (e.Button == MouseButtons.Right)
            {
                if (!selList.Contains(ph)) Select(ph);
                else { sel = ph; UpdateSel(); preview.Invalidate(); }
                ctx.Show(preview, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (ctrl) { ToggleSel(ph); return; }
            if (shift) AddSel(ph);
            else if (selList.Contains(ph) && selList.Count > 1) { sel = ph; pendingSingle = true; UpdateSel(); preview.Invalidate(); }
            else Select(ph);
            drag = DragMode.Move;
            dragMoved = false;
            dragIndex = hit;
            dragStart = e.Location;
            if (free)
            {
                var pr = PageRectPx(sel.FPage);
                grabX = (wp.X - pr.X) / Ppm - sel.FX;
                grabY = (wp.Y - pr.Y) / Ppm - sel.FY;
                dragStartPos.Clear();
                foreach (var p in selList) dragStartPos[p] = new double[] { p.FX, p.FY, p.FPage };
            }
            else
            {
                double W, H; CellSize(out W, out H);
                var q = GetGeo(sel, W, H);
                dragDx = q.Dx; dragDy = q.Dy;
            }
        }

        void PreviewMouseMove(object s, MouseEventArgs e)
        {
            var wp = World(e.Location);
            bool noSnap = !snapOn || (ModifierKeys & Keys.Alt) != 0;
            if (marquee)
            {
                marqB = wp;
                var r = MarqRect();
                var set = new List<Photo>(marqBase);
                for (int i = 0; i < items.Count; i++)
                {
                    var pr = free ? FreeRectPx(items[i]) : CellRectPx(i);
                    if (r.IntersectsWith(pr) && !set.Contains(items[i])) set.Add(items[i]);
                }
                selList.Clear();
                selList.AddRange(set);
                sel = set.Count > 0 ? set[set.Count - 1] : null;
                preview.Invalidate();
                UpdateSel();
                return;
            }
            if (drag == DragMode.Resize && sel != null)
            {
                if (!dragMoved) { PushUndo(""); dragMoved = true; }
                if (free)
                {
                    var r = FreeRectPx(sel);
                    double ew, eh; EffDims(sel, out ew, out eh);
                    double wmm = (wp.X - r.X) / Ppm, hmm = (wp.Y - r.Y) / Ppm;
                    double maxW = Math.Max(3, Math.Min(PW - sel.FX, (PH - sel.FY) * ew / eh));
                    double fw = Clamp(Math.Max(wmm, hmm * ew / eh), 3, maxW);
                    guides.Clear();
                    if (!noSnap) fw = SnapResize(sel, fw, maxW);
                    sel.FW = fw;
                }
                else
                {
                    var p0 = World(dragStart);
                    double d0 = Math.Sqrt(Math.Pow(p0.X - dragCenter.X, 2) + Math.Pow(p0.Y - dragCenter.Y, 2));
                    double d1 = Math.Sqrt(Math.Pow(wp.X - dragCenter.X, 2) + Math.Pow(wp.Y - dragCenter.Y, 2));
                    if (d0 < 1) d0 = 1;
                    sel.Scale = Clamp(dragScale * d1 / d0, 0.05, 20);
                }
                preview.Invalidate();
                UpdateSel();
                return;
            }
            if (drag == DragMode.Move && sel != null)
            {
                if (!dragMoved && Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) < D(4)) return;
                if (!dragMoved) { PushUndo(""); dragMoved = true; pendingSingle = false; }
                if (free) { FreeDragMove(wp, noSnap); return; }
                int hit = HitTest(e.Location);
                if (hit >= 0 && hit != dragIndex)
                {
                    dropTarget = hit;
                    sel.Dx = dragDx; sel.Dy = dragDy;
                    preview.Cursor = Cursors.Hand;
                }
                else
                {
                    dropTarget = -1;
                    double W, H; CellSize(out W, out H);
                    var q = GetGeo(sel, W, H);
                    sel.Dx = Clamp(dragDx + (e.X - dragStart.X) / Ppm, -q.MaxDx, q.MaxDx);
                    sel.Dy = Clamp(dragDy + (e.Y - dragStart.Y) / Ppm, -q.MaxDy, q.MaxDy);
                    preview.Cursor = Cursors.SizeAll;
                }
                preview.Invalidate();
                return;
            }
            int hov = PhotoAt(e.Location);
            if (hov != hoverIndex) { hoverIndex = hov; preview.Invalidate(); }
            if (OnHandle(wp)) preview.Cursor = Cursors.SizeNWSE;
            else if (free) preview.Cursor = hov >= 0 ? Cursors.SizeAll : Cursors.Default;
            else
            {
                int h = HitTest(e.Location);
                preview.Cursor = h < 0 ? Cursors.Default : (h >= items.Count ? Cursors.Hand : Cursors.SizeAll);
            }
        }

        // move every selected photo together, keeping their arrangement; snap the group to guides
        void FreeDragMove(PointF wp, bool noSnap)
        {
            double[] st0;
            if (!dragStartPos.TryGetValue(sel, out st0)) return;
            int startPage = (int)st0[2];
            bool samePage = dragStartPos.Values.All(v => (int)v[2] == startPage);
            int page = startPage;
            if (samePage) { int pg = PageAt(wp); if (pg >= 0) page = pg; }
            var pr = PageRectPx(page);
            double dx = (wp.X - pr.X) / Ppm - grabX - st0[0];
            double dy = (wp.Y - pr.Y) / Ppm - grabY - st0[1];
            double L = double.MaxValue, T = double.MaxValue, R = double.MinValue, B = double.MinValue;
            foreach (var kv in dragStartPos)
            {
                L = Math.Min(L, kv.Value[0]); T = Math.Min(T, kv.Value[1]);
                R = Math.Max(R, kv.Value[0] + kv.Key.FW); B = Math.Max(B, kv.Value[1] + FH(kv.Key));
            }
            dx = Clamp(dx, -L, PW - R);
            dy = Clamp(dy, -T, PH - B);
            guides.Clear();
            if (!noSnap && samePage)
            {
                List<double> xs, ys; SnapLines(page, out xs, out ys);
                dx += Best(xs, new[] { L + dx, (L + R) / 2 + dx, R + dx });
                dy += Best(ys, new[] { T + dy, (T + B) / 2 + dy, B + dy });
                dx = Clamp(dx, -L, PW - R);
                dy = Clamp(dy, -T, PH - B);
                AddGuides(xs, new[] { L + dx, (L + R) / 2 + dx, R + dx }, true, page);
                AddGuides(ys, new[] { T + dy, (T + B) / 2 + dy, B + dy }, false, page);
            }
            foreach (var kv in dragStartPos)
            {
                kv.Key.FX = kv.Value[0] + dx;
                kv.Key.FY = kv.Value[1] + dy;
                if (samePage) kv.Key.FPage = page;
            }
            UpdateScroll();
            preview.Cursor = Cursors.SizeAll;
            preview.Invalidate();
            UpdateSel();
        }

        // lines photos can snap to: page edges, margins and center, plus edges and centers of other photos on the page
        void SnapLines(int page, out List<double> xs, out List<double> ys)
        {
            xs = new List<double> { 0, margin, PW / 2, PW - margin, PW };
            ys = new List<double> { 0, margin, PH / 2, PH - margin, PH };
            foreach (var p in items)
            {
                if (p.FPage != page) continue;
                if (drag == DragMode.Move && dragStartPos.ContainsKey(p)) continue;
                if (drag == DragMode.Resize && p == sel) continue;
                double h = FH(p);
                xs.Add(p.FX); xs.Add(p.FX + p.FW / 2); xs.Add(p.FX + p.FW);
                ys.Add(p.FY); ys.Add(p.FY + h / 2); ys.Add(p.FY + h);
            }
        }

        double SnapThreshold { get { return D(7) / Ppm; } }

        double Best(List<double> cands, double[] edges)
        {
            double best = 0, bestD = SnapThreshold;
            foreach (var c in cands)
                foreach (var ed in edges)
                {
                    double d = c - ed;
                    if (Math.Abs(d) < bestD) { bestD = Math.Abs(d); best = d; }
                }
            return best;
        }

        void AddGuides(List<double> cands, double[] edges, bool vertical, int page)
        {
            var done = new List<double>();
            foreach (var c in cands)
            {
                if (done.Any(v => Math.Abs(v - c) < 0.01)) continue;
                foreach (var ed in edges)
                    if (Math.Abs(c - ed) < 0.05) { guides.Add(new double[] { vertical ? 1 : 0, c, page }); done.Add(c); break; }
            }
        }

        double SnapResize(Photo p, double fw, double maxW)
        {
            double ew, eh; EffDims(p, out ew, out eh);
            double r = eh / ew;
            List<double> xs, ys; SnapLines(p.FPage, out xs, out ys);
            double bx = Best(xs, new[] { p.FX + fw }), by = Best(ys, new[] { p.FY + fw * r });
            double nfw = fw;
            if (bx != 0 && (by == 0 || Math.Abs(bx) <= Math.Abs(by))) nfw = fw + bx;
            else if (by != 0) nfw = fw + by / r;
            nfw = Clamp(nfw, 3, maxW);
            AddGuides(xs, new[] { p.FX + nfw }, true, p.FPage);
            AddGuides(ys, new[] { p.FY + nfw * r }, false, p.FPage);
            return nfw;
        }

        void PreviewMouseUp(object s, MouseEventArgs e)
        {
            if (marquee)
            {
                marquee = false;
                preview.Invalidate();
                UpdateSel();
                return;
            }
            if (!free && drag == DragMode.Move && dropTarget >= 0 && dragIndex >= 0 && dragIndex < items.Count)
            {
                int from = dragIndex;
                if (dropTarget >= items.Count)
                {
                    var p = items[from];
                    items.RemoveAt(from);
                    items.Add(p);
                }
                else
                {
                    var t = items[dropTarget];
                    items[dropTarget] = items[from];
                    items[from] = t;
                }
            }
            if (pendingSingle && !dragMoved && sel != null) Select(sel);
            pendingSingle = false;
            if (free && drag != DragMode.None && dragMoved) CompactFreePages();
            drag = DragMode.None;
            dropTarget = -1;
            dragIndex = -1;
            guides.Clear();
            dragStartPos.Clear();
            AfterChange();
        }

        bool CtrlWheel(MouseEventArgs e)
        {
            int hit = PhotoAt(e.Location);
            if (hit >= 0)
            {
                if (!selList.Contains(items[hit])) Select(items[hit]);
                PushUndo("wheel");
                double f = e.Delta > 0 ? 1.05 : 1 / 1.05;
                foreach (var p in Targets())
                {
                    if (free) ScaleFree(p, f);
                    else p.Scale = Clamp(p.Scale * f, 0.05, 20);
                }
                preview.Invalidate();
                UpdateSel();
            }
            else
            {
                zoom = (float)Clamp(zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 4);
                AfterChange();
            }
            return true;
        }

        void OnPreviewKey(object s, KeyEventArgs e)
        {
            if (sel == null) return;
            var kc = e.KeyCode;
            if (free && (kc == Keys.Left || kc == Keys.Right || kc == Keys.Up || kc == Keys.Down))
            {
                PushUndo("nudge");
                double st = e.Shift ? 10 : 1;   // mm
                foreach (var p in Targets())
                {
                    if (kc == Keys.Left) p.FX -= st;
                    if (kc == Keys.Right) p.FX += st;
                    if (kc == Keys.Up) p.FY -= st;
                    if (kc == Keys.Down) p.FY += st;
                    ClampFree(p);
                }
                preview.Invalidate();
                UpdateSel();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            switch (kc)
            {
                case Keys.R: Act(e.Shift ? "rl" : "rr"); break;
                case Keys.F: Act(sel.Fill ? "fit" : "fill"); break;
                case Keys.Delete:
                case Keys.Back: Act("del"); break;
                case Keys.Left: Act("prev"); break;
                case Keys.Right: Act("next"); break;
                case Keys.Oemplus:
                case Keys.Add: ScaleAll(1.05); break;
                case Keys.OemMinus:
                case Keys.Subtract: ScaleAll(1 / 1.05); break;
                case Keys.Escape: Select(null); break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        // ------------------------------------------------------------- actions
        void SizeChanged2()
        {
            preview.Invalidate();
            UpdateSel();
        }

        void ScaleAll(double f)
        {
            PushUndo("size");
            foreach (var p in Targets())
            {
                if (free) ScaleFree(p, f);
                else p.Scale = Clamp(p.Scale * f, 0.05, 20);
            }
            SizeChanged2();
        }

        void SetScaleAll(double sc)
        {
            PushUndo("scale");
            foreach (var p in Targets()) p.Scale = Clamp(sc, 0.05, 20);
            SizeChanged2();
        }

        // W / H fields: every selected photo gets this width (or height)
        void SetSizeMm(double mm, bool width)
        {
            PushUndo("size");
            double W, H; CellSize(out W, out H);
            foreach (var p in Targets())
            {
                double ew, eh; EffDims(p, out ew, out eh);
                if (free) { p.FW = width ? mm : mm * ew / eh; ClampFree(p); }
                else
                {
                    var q = GetGeo(p, W, H);
                    double unit = (width ? ew : eh) * q.Base;
                    if (unit > 0) p.Scale = Clamp(mm / unit, 0.05, 20);
                }
            }
            SizeChanged2();
        }

        // X / Y fields: move the selection so the last-clicked photo lands there
        void MoveSelTo(double v, bool isX)
        {
            PushUndo(isX ? "posx" : "posy");
            double d = isX ? v - sel.FX : v - sel.FY;
            foreach (var p in Targets())
            {
                if (isX) p.FX += d; else p.FY += d;
                ClampFree(p);
            }
            SizeChanged2();
        }

        void Act(string a)
        {
            var ts = Targets();
            if (ts.Count == 0 || sel == null) return;
            PushUndo("");
            switch (a)
            {
                case "rl":
                case "rr":
                    foreach (var p in ts)
                    {
                        int d = a == "rl" ? 270 : 90;
                        if (free) RotateFree(p, d);
                        else { p.Rot = (p.Rot + d) % 360; p.Dx = p.Dy = 0; }
                    }
                    break;
                case "fit":
                case "fill":
                    if (!free) foreach (var p in ts) { p.Fill = a == "fill"; p.Scale = 1; p.Dx = p.Dy = 0; }
                    break;
                case "reset":
                    foreach (var p in ts)
                    {
                        if (free) { DefaultFreeSize(p); ClampFree(p); }
                        else { p.Scale = 1; p.Dx = p.Dy = 0; }
                    }
                    break;
                case "prev":
                case "next":
                    {
                        int i = items.IndexOf(sel);
                        int j = a == "prev" ? i - 1 : i + 1;
                        if (j >= 0 && j < items.Count) { items[i] = items[j]; items[j] = sel; }
                        break;
                    }
                case "front":
                    foreach (var p in ts) items.Remove(p);
                    items.AddRange(ts);
                    break;
                case "back":
                    foreach (var p in ts) items.Remove(p);
                    items.InsertRange(0, ts);
                    break;
                case "dup":
                    {
                        var copies = new List<Photo>();
                        for (int k = ts.Count - 1; k >= 0; k--)
                        {
                            var p = ts[k];
                            var c = p.Copy();
                            if (free) { c.FX += 5; c.FY += 5; ClampFree(c); }
                            else c.FreeSet = false;
                            items.Insert(items.IndexOf(p) + 1, c);
                            copies.Insert(0, c);
                        }
                        selList.Clear();
                        selList.AddRange(copies);
                        sel = copies[copies.Count - 1];
                        break;
                    }
                case "fillpage":
                    if (!free)
                    {
                        int i = items.IndexOf(sel);
                        int n = PerPage - (i % PerPage) - 1;
                        for (int k = 0; k < n; k++) { var c = sel.Copy(); c.FreeSet = false; items.Insert(i + 1, c); }
                    }
                    break;
                case "del":
                    {
                        int first = items.IndexOf(ts[0]);
                        foreach (var p in ts) items.Remove(p);
                        selList.Clear();
                        sel = items.Count == 0 ? null : items[Math.Min(first, items.Count - 1)];
                        if (sel != null) selList.Add(sel);
                        if (free) CompactFreePages();
                        break;
                    }
            }
            AfterChange();
        }

        // alignment tools (free layout). Each page's selected photos are aligned separately.
        void Align(string how)
        {
            if (!free) return;
            var ts = Targets();
            if (ts.Count == 0) return;
            PushUndo("");
            foreach (var grp in ts.GroupBy(p => p.FPage))
            {
                var list = grp.ToList();
                bool toPage = alignToPage || ts.Count == 1;
                double L = list.Min(p => p.FX), T = list.Min(p => p.FY);
                double R = list.Max(p => p.FX + p.FW), B = list.Max(p => p.FY + FH(p));
                if (toPage && how != "centerpage") { L = margin; T = margin; R = PW - margin; B = PH - margin; }
                switch (how)
                {
                    case "left": foreach (var p in list) p.FX = L; break;
                    case "right": foreach (var p in list) p.FX = R - p.FW; break;
                    case "hcenter": foreach (var p in list) p.FX = (L + R) / 2 - p.FW / 2; break;
                    case "top": foreach (var p in list) p.FY = T; break;
                    case "bottom": foreach (var p in list) p.FY = B - FH(p); break;
                    case "vcenter": foreach (var p in list) p.FY = (T + B) / 2 - FH(p) / 2; break;
                    case "centerpage":
                        {
                            double dx = PW / 2 - (L + R) / 2, dy = PH / 2 - (T + B) / 2;
                            foreach (var p in list) { p.FX += dx; p.FY += dy; }
                            break;
                        }
                    case "disth":
                        {
                            if (alignGap > 0 && list.Count >= 2)
                            {
                                var so = list.OrderBy(p => p.FX).ToList();
                                double x0 = so[0].FX;
                                foreach (var p in so) { p.FX = x0; x0 += p.FW + alignGap; }
                                break;
                            }
                            if (list.Count < 2 || (list.Count < 3 && !toPage)) break;
                            var sorted = list.OrderBy(p => p.FX).ToList();
                            double space = (R - L - sorted.Sum(p => p.FW)) / (sorted.Count - 1);
                            double x = L;
                            foreach (var p in sorted) { p.FX = x; x += p.FW + space; }
                            break;
                        }
                    case "distv":
                        {
                            if (alignGap > 0 && list.Count >= 2)
                            {
                                var so = list.OrderBy(p => p.FY).ToList();
                                double y0 = so[0].FY;
                                foreach (var p in so) { p.FY = y0; y0 += FH(p) + alignGap; }
                                break;
                            }
                            if (list.Count < 2 || (list.Count < 3 && !toPage)) break;
                            var sorted = list.OrderBy(p => p.FY).ToList();
                            double space = (B - T - sorted.Sum(p => FH(p))) / (sorted.Count - 1);
                            double y = T;
                            foreach (var p in sorted) { p.FY = y; y += FH(p) + space; }
                            break;
                        }
                    case "samew":
                        foreach (var p in list) p.FW = sel.FW;
                        break;
                    case "sameh":
                        {
                            double h = FH(sel);
                            foreach (var p in list) { double ew, eh; EffDims(p, out ew, out eh); p.FW = h * ew / eh; }
                            break;
                        }
                }
                foreach (var p in list) ClampFree(p);
            }
            AfterChange();
        }

        void AllPhotos(string a)
        {
            double W, H; CellSize(out W, out H);
            if (free) { W = PW; H = PH; }
            if (a == "clear")
            {
                if (items.Count == 0 || MessageBox.Show(this, "Remove all photos from the layout?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                PushUndo("");
                items.Clear();
                selList.Clear();
                sel = null;
            }
            else
            {
                if (items.Count == 0) return;
                PushUndo("");
            }
            foreach (var p in items)
            {
                switch (a)
                {
                    case "autorot": p.Rot = ((W >= H) != (p.W >= p.H)) ? 90 : 0; p.Dx = p.Dy = 0; break;
                    case "rot0": p.Rot = 0; p.Dx = p.Dy = 0; break;
                    case "fit": p.Fill = false; p.Scale = 1; p.Dx = p.Dy = 0; break;
                    case "fill": p.Fill = true; p.Scale = 1; p.Dx = p.Dy = 0; break;
                    case "size": p.Scale = 1; p.Dx = p.Dy = 0; break;
                }
            }
            if (free && (a == "autorot" || a == "rot0" || a == "size")) PackFree(items.ToList(), true);
            AfterChange();
        }

        // ---- undo / redo
        Snap TakeSnap()
        {
            var sn = new Snap { Items = items.Select(p => p.Copy()).ToList(), Free = free, Cols = cols, Rows = rows, BoxW = boxW, BoxH = boxH };
            sn.SelIdx = selList.Select(p => items.IndexOf(p)).Where(i => i >= 0).ToArray();
            sn.Primary = sel == null ? -1 : items.IndexOf(sel);
            return sn;
        }

        void RestoreSnap(Snap sn)
        {
            items.Clear();
            items.AddRange(sn.Items.Select(p => p.Copy()));
            free = sn.Free; cols = sn.Cols; rows = sn.Rows; boxW = sn.BoxW; boxH = sn.BoxH;
            selList.Clear();
            foreach (var i in sn.SelIdx) if (i < items.Count) selList.Add(items[i]);
            sel = sn.Primary >= 0 && sn.Primary < items.Count ? items[sn.Primary] : null;
            SyncControls();
            AfterChange();
        }

        // kind: changes of the same kind within ~1 s (e.g. spinning a number) become one undo step; "" = always a new step
        void PushUndo(string kind)
        {
            var now = DateTime.Now;
            if (kind != "" && kind == lastUndoKind && (now - lastUndoTime).TotalMilliseconds < 1200) { lastUndoTime = now; return; }
            lastUndoKind = kind;
            lastUndoTime = now;
            undo.Add(TakeSnap());
            if (undo.Count > 60) undo.RemoveAt(0);
            redo.Clear();
            btnUndo.Enabled = true;
            btnRedo.Enabled = false;
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            redo.Add(TakeSnap());
            var sn = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            lastUndoKind = "";
            RestoreSnap(sn);
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            undo.Add(TakeSnap());
            var sn = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            lastUndoKind = "";
            RestoreSnap(sn);
        }

        // ---- save / open layout files (*.photolayout, plain text)
        void SaveLayout()
        {
            if (items.Count == 0) { MessageBox.Show(this, "There is nothing to save yet.", Text); return; }
            using (var d = new SaveFileDialog { Title = "Save layout", Filter = "Photo layout (*.photolayout)|*.photolayout", DefaultExt = "photolayout", FileName = "My layout" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var ci = CultureInfo.InvariantCulture;
                var lines = new List<string>
                {
                    "PhotoPrintLayout\t1",
                    string.Join("\t", new[] { "page", paper != null ? paper.PaperName : "", landscape ? "1" : "0", cols.ToString(ci), rows.ToString(ci),
                        margin.ToString("R", ci), gap.ToString("R", ci), cut ? "1" : "0", free ? "1" : "0",
                        boxW.ToString("R", ci), boxH.ToString("R", ci), guideMode.ToString(ci), captionMode.ToString(ci), captionPt.ToString("R", ci) })
                };
                foreach (var p in items)
                    lines.Add(string.Join("\t", new[] { "photo", p.Path, p.Rot.ToString(ci), p.Fill ? "1" : "0", p.Scale.ToString("R", ci),
                        p.Dx.ToString("R", ci), p.Dy.ToString("R", ci), p.FreeSet ? "1" : "0", p.FPage.ToString(ci),
                        p.FX.ToString("R", ci), p.FY.ToString("R", ci), p.FW.ToString("R", ci), p.Caption ?? "", p.PdfPage.ToString(ci) }));
                try { File.WriteAllLines(d.FileName, lines); AddRecent(recentLayouts, d.FileName); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void OpenLayout()
        {
            using (var d = new OpenFileDialog { Title = "Open layout", Filter = "Photo layout (*.photolayout)|*.photolayout|All files|*.*" })
            {
                if (d.ShowDialog(this) == DialogResult.OK) LoadLayoutFile(d.FileName);
            }
        }

        public void LoadLayoutFile(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || !lines[0].StartsWith("PhotoPrintLayout")) { MessageBox.Show(this, "This is not a Photo Print Layout file.", Text); return; }
                var ci = CultureInfo.InvariantCulture;
                PushUndo("");
                var specs = new List<string[]>();
                foreach (var l in lines.Skip(1))
                {
                    var f = l.Split('\t');
                    if (f[0] == "page" && f.Length >= 9)
                    {
                        for (int i = 0; i < cbPaper.Items.Count; i++)
                        {
                            var pi = cbPaper.Items[i] as PaperItem;
                            if (pi != null && pi.P.PaperName == f[1]) { updating = true; cbPaper.SelectedIndex = i; paper = pi.P; updating = false; break; }
                        }
                        landscape = f[2] == "1";
                        cols = (int)Clamp(int.Parse(f[3], ci), 1, 12);
                        rows = (int)Clamp(int.Parse(f[4], ci), 1, 12);
                        margin = Clamp(double.Parse(f[5], ci), 0, 50);
                        gap = Clamp(double.Parse(f[6], ci), 0, 50);
                        cut = f[7] == "1";
                        free = f[8] == "1";
                        guideMode = cut ? 1 : 0;
                        if (f.Length >= 14)
                        {
                            boxW = Clamp(double.Parse(f[9], ci), 0, 1000); boxH = Clamp(double.Parse(f[10], ci), 0, 1000);
                            guideMode = (int)Clamp(int.Parse(f[11], ci), 0, 2); captionMode = (int)Clamp(int.Parse(f[12], ci), 0, 2);
                            captionPt = Clamp(double.Parse(f[13], ci), 5, 36);
                        }
                        else { boxW = boxH = 0; captionMode = 0; }
                        cut = guideMode == 1;
                    }
                    else if (f[0] == "photo" && f.Length >= 12) specs.Add(f);
                }
                items.Clear();
                selList.Clear();
                sel = null;
                Cursor = Cursors.WaitCursor;
                var res = new LoadResult[specs.Count];
                Parallel.For(0, specs.Count, i => { if (SpecPage(specs[i]) < 0) res[i] = Loader.LoadGdi(specs[i][1], 1600); });
                var missing = new List<string>();
                for (int i = 0; i < specs.Count; i++)
                {
                    var f = specs[i];
                    if (res[i] == null) res[i] = SpecPage(f) >= 0 ? PdfLoader.Load(f[1], SpecPage(f), 1600) : Loader.LoadWic(f[1], 1600);
                    if (res[i] == null) { missing.Add(f[1]); continue; }
                    items.Add(new Photo
                    {
                        Path = f[1], Name = Path.GetFileName(f[1]), Preview = res[i].Bmp, W = res[i].FullW, H = res[i].FullH,
                        Rot = int.Parse(f[2], ci), Fill = f[3] == "1", Scale = double.Parse(f[4], ci),
                        Dx = double.Parse(f[5], ci), Dy = double.Parse(f[6], ci), FreeSet = f[7] == "1", FPage = int.Parse(f[8], ci),
                        FX = double.Parse(f[9], ci), FY = double.Parse(f[10], ci), FW = double.Parse(f[11], ci),
                        Caption = f.Length >= 13 ? f[12] : null, PdfPage = SpecPage(f)
                    });
                }
                Cursor = Cursors.Default;
                if (free) CompactFreePages();
                SyncControls();
                FitZoom();
                AddRecent(recentLayouts, Path.GetFullPath(path));
                WriteTestReport();
                if (missing.Count > 0)
                    MessageBox.Show(this, "These photos were not found (moved, renamed or deleted):\n\n" + string.Join("\n", missing.Take(15)), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "Could not open the layout: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --------------------------------------------------------- add photos
        void BrowseAdd()
        {
            using (var d = new OpenFileDialog
            {
                Multiselect = true, Title = "Add photos",
                Filter = "Photos and PDF|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.psd;*.pdf|All files|*.*"
            })
            {
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
            }
        }

        public void AddFiles(IEnumerable<string> paths)
        {
            var all = paths.ToList();
            var lay = all.FirstOrDefault(x => x.EndsWith(".photolayout", StringComparison.OrdinalIgnoreCase));
            if (lay != null) { LoadLayoutFile(lay); return; }
            paths = all;
            var list = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        var files = Directory.GetFiles(p).Where(f => ImageExt.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                        list.AddRange(files);
                    }
                    else if (File.Exists(p)) list.Add(p);
                }
                catch { }
            }
            if (list.Count == 0) return;
            PushUndo("");
            Cursor = Cursors.WaitCursor;
            // PDF files: one picture per page (up to 50 pages)
            var srcPath = new List<string>();
            var srcPage = new List<int>();
            foreach (var fp in list)
            {
                if (fp.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    int np0 = PdfLoader.PageCount(fp);
                    if (np0 <= 0) { srcPath.Add(fp); srcPage.Add(0); continue; }   // reported below as not opened
                    for (int pg = 0; pg < Math.Min(np0, 50); pg++) { srcPath.Add(fp); srcPage.Add(pg); }
                }
                else { srcPath.Add(fp); srcPage.Add(-1); }
            }
            var res = new LoadResult[srcPath.Count];
            Parallel.For(0, srcPath.Count, i => { if (srcPage[i] < 0) res[i] = Loader.LoadGdi(srcPath[i], 1600); });
            var failed = new List<string>();
            var added = new List<Photo>();
            for (int i = 0; i < srcPath.Count; i++)
            {
                if (res[i] == null) res[i] = srcPage[i] >= 0 ? PdfLoader.Load(srcPath[i], srcPage[i], 1600) : Loader.LoadWic(srcPath[i], 1600);
                if (res[i] == null) { string nm = Path.GetFileName(srcPath[i]); if (!failed.Contains(nm)) failed.Add(nm); continue; }
                bool multi = srcPage[i] >= 0 && ((i > 0 && srcPath[i - 1] == srcPath[i]) || (i + 1 < srcPath.Count && srcPath[i + 1] == srcPath[i]));
                var np = new Photo { Path = srcPath[i], Name = Path.GetFileName(srcPath[i]) + (multi ? " (page " + (srcPage[i] + 1) + ")" : ""), Preview = res[i].Bmp, W = res[i].FullW, H = res[i].FullH, PdfPage = srcPage[i] };
                items.Add(np);
                added.Add(np);
                AddRecent(recentPhotos, np.Path);
            }
            Cursor = Cursors.Default;
            if (free) PackFree(added, false);
            AfterChange();
            WriteTestReport();
            if (failed.Count > 0)
                MessageBox.Show(this, "These files could not be opened:\n\n" + string.Join("\n", failed.Take(15)) +
                    "\n\nFor HEIC photos, install “HEIF Image Extensions” from the Microsoft Store, or convert them to JPG. Password-protected PDFs can't be opened.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void OnDragDrop(object s, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) BeginInvoke(new Action(() => AddFiles(files)));
        }

        void PasteFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    AddFiles(Clipboard.GetFileDropList().Cast<string>().ToList());
                }
                else if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage())
                    {
                        if (img == null) return;
                        string dir = Path.Combine(Path.GetTempPath(), "PhotoPrintLayout");
                        Directory.CreateDirectory(dir);
                        string file = Path.Combine(dir, "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                        img.Save(file, ImageFormat.Png);
                        AddFiles(new[] { file });
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not paste: " + ex.Message, Text); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inText = ActiveControl is NumericUpDown || ActiveControl is ComboBox || ActiveControl is TextBox;
            if (keyData == (Keys.Control | Keys.V) && !inText) { PasteFromClipboard(); return true; }
            if (!inText)
            {
                if (keyData == (Keys.Control | Keys.A)) { SelectAll(false); return true; }
                if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
                if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
                if (keyData == (Keys.Control | Keys.D)) { Act("dup"); return true; }
            }
            if (keyData == (Keys.Control | Keys.S)) { SaveLayout(); return true; }
            if (keyData == (Keys.Control | Keys.P)) { DoPrint(); return true; }
            if (keyData == (Keys.Control | Keys.O)) { BrowseAdd(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------ printing
        void ApplyPage(PrintDocument pd)
        {
            if (paper != null) pd.DefaultPageSettings.PaperSize = MatchPaper(pd.PrinterSettings, paper);
            pd.DefaultPageSettings.Landscape = landscape;
            pd.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            pd.OriginAtMargins = false;
        }

        PrintDocument MakeDoc()
        {
            var pd = new PrintDocument { DocumentName = "Photo Print Layout" };
            if (printSettings != null && printSettings.PrinterName == printerName) pd.PrinterSettings = (PrinterSettings)printSettings.Clone();
            else if (printerName != "") pd.PrinterSettings.PrinterName = printerName;
            ApplyPage(pd);
            pd.BeginPrint += (s, e) => printPage = printFrom;
            pd.PrintPage += PrintPageHandler;
            return pd;
        }

        void DoPreview() { DoPrint(); }

        void DoPrint()
        {
            if (items.Count == 0) { MessageBox.Show(this, "Add some photos first.", Text); return; }
            using (var w = new PrintWindow(this)) w.ShowDialog(this);
            printFrom = 0; printTo = int.MaxValue;
        }

        void PrintPageHandler(object sender, PrintPageEventArgs e)
        {
            var g = e.Graphics;
            var pd = (PrintDocument)sender;
            bool isPreview = pd.PrintController != null && pd.PrintController.IsPreview;
            g.PageUnit = GraphicsUnit.Millimeter;
            if (!isPreview) g.TranslateTransform(-e.PageSettings.HardMarginX * 0.254f, -e.PageSettings.HardMarginY * 0.254f);
            SetQuality(g);
            RenderPageContent(g, printPage, 0, 0, 1f, !isPreview);
            if (isPreview && previewEdge) DrawEdgeBand(g, 0, 0, 1f);
            printPage++;
            e.HasMorePages = printPage < Math.Min(PrintPageCount, printTo);
        }

        // ---- printer's own settings window (quality, paper type...)
        [DllImport("winspool.drv", CharSet = CharSet.Unicode)]
        static extern int DocumentProperties(IntPtr hwnd, IntPtr hPrinter, string pDeviceName, IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr h);

        bool ShowPrinterPreferences(IWin32Window owner)
        {
            IntPtr outBuf = IntPtr.Zero;
            try
            {
                var ps = printSettings != null && printSettings.PrinterName == printerName ? printSettings : new PrinterSettings { PrinterName = printerName };
                var pg = new PageSettings(ps) { Landscape = landscape };
                if (paper != null) pg.PaperSize = MatchPaper(ps, paper);
                IntPtr hDev = ps.GetHdevmode(pg);
                IntPtr pDev = GlobalLock(hDev);
                int size = DocumentProperties(owner.Handle, IntPtr.Zero, ps.PrinterName, IntPtr.Zero, IntPtr.Zero, 0);
                if (size <= 0) { GlobalUnlock(hDev); GlobalFree(hDev); return false; }
                outBuf = Marshal.AllocHGlobal(size);
                int r = DocumentProperties(owner.Handle, IntPtr.Zero, ps.PrinterName, outBuf, pDev, 2 | 4 | 8);   // out buffer | prompt | in buffer
                GlobalUnlock(hDev);
                GlobalFree(hDev);
                if (r != 1) return false;
                ps.SetHdevmode(outBuf);
                pg.SetHdevmode(outBuf);
                printSettings = ps;
                // the printer window may have changed paper or orientation: follow it
                var want = pg.PaperSize;
                for (int i = 0; i < cbPaper.Items.Count; i++)
                {
                    var pi = cbPaper.Items[i] as PaperItem;
                    if (pi != null && ((want.Kind != PaperKind.Custom && pi.P.Kind == want.Kind) || pi.P.PaperName == want.PaperName))
                    {
                        if (cbPaper.SelectedIndex != i) cbPaper.SelectedIndex = i;
                        break;
                    }
                }
                if (pg.Landscape != landscape) { if (pg.Landscape) rbLand.Checked = true; else rbPort.Checked = true; }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Could not open the printer settings: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf); }
        }

        // ---- the Print window: settings on the left, live preview of the real pages on the right
        class PrintWindow : Form
        {
            readonly MainForm F;
            readonly PrintPreviewControl pv;
            readonly ComboBox cbPr, cbPa;
            readonly RadioButton rbP, rbL, rbAll, rbRange;
            readonly NumericUpDown nCopies, nFrom, nTo;
            readonly CheckBox chkEdge;
            readonly Label lblInfo;
            readonly ToolStripLabel lblPage;
            PrintDocument pd;
            bool busy;

            public PrintWindow(MainForm f)
            {
                F = f;
                Text = "Print – Photo Print Layout";
                Font = f.Font;
                Icon = f.Icon;
                StartPosition = FormStartPosition.CenterParent;
                Size = new Size(f.D(1150), f.D(800));
                MinimumSize = new Size(f.D(800), f.D(560));
                ShowInTaskbar = false;
                MinimizeBox = false;
                KeyPreview = true;

                var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = f.D(310), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(f.D(16), f.D(12), f.D(8), f.D(12)), BackColor = Color.White };
                var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = f.D(56), ColumnCount = 2, Padding = new Padding(f.D(16), f.D(8), f.D(16), f.D(10)), BackColor = Color.White };
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
                var bPrint = f.PrimaryButton("Print");
                bPrint.Dock = DockStyle.Fill; bPrint.Margin = new Padding(0, 0, f.D(6), 0);
                var bCancel = f.Btn("Cancel", (s, e) => Close());
                bCancel.Dock = DockStyle.Fill;
                bottom.Controls.Add(bPrint, 0, 0);
                bottom.Controls.Add(bCancel, 1, 0);
                var leftWrap = new Panel { Dock = DockStyle.Left, Width = f.D(310) };
                leftWrap.Controls.Add(left);
                leftWrap.Controls.Add(bottom);

                f.AddHeader(left, "PRINTER");
                cbPr = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = f.D(320) };
                foreach (var it in f.cbPrinter.Items) cbPr.Items.Add(it);
                cbPr.SelectedItem = f.cbPrinter.SelectedItem;
                left.Controls.Add(f.Row(32, new float[] { 26, 74 }, f.SmallLabel("Printer"), cbPr));
                cbPa = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = f.D(320) };
                left.Controls.Add(f.Row(32, new float[] { 26, 74 }, f.SmallLabel("Paper"), cbPa));
                rbP = f.Toggle("Portrait");
                rbL = f.Toggle("Landscape");
                left.Controls.Add(f.Row(32, null, rbP, rbL));
                var bPrefs = f.Btn("Printer preferences…", (s, e) => { if (F.ShowPrinterPreferences(this)) { SyncFromMain(); Regenerate(); } });
                f.tips.SetToolTip(bPrefs, "The printer's own settings: print quality, paper type, colour…");
                left.Controls.Add(f.Row(34, null, bPrefs));

                f.AddHeader(left, "COPIES AND PAGES");
                nCopies = f.Num(1, 999, 0, 1);
                nCopies.Value = 1;
                left.Controls.Add(f.Row(32, new float[] { 60, 40 }, f.SmallLabel("Copies"), nCopies));
                rbAll = new RadioButton { Text = "All pages", Checked = true, AutoSize = false };
                rbRange = new RadioButton { Text = "Pages", AutoSize = false };
                nFrom = f.Num(1, 999, 0, 1);
                nTo = f.Num(1, 999, 0, 1);
                var pagesRow = new TableLayoutPanel { ColumnCount = 4, RowCount = 1, Width = f.SideW, Height = f.D(30), Margin = new Padding(0, f.D(2), 0, f.D(2)) };
                pagesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); pagesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
                pagesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14)); pagesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
                rbRange.Dock = DockStyle.Fill; nFrom.Anchor = AnchorStyles.Left | AnchorStyles.Right; nTo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                var lTo = f.CenterLabel("to"); lTo.Dock = DockStyle.Fill;
                pagesRow.Controls.Add(rbRange, 0, 0); pagesRow.Controls.Add(nFrom, 1, 0); pagesRow.Controls.Add(lTo, 2, 0); pagesRow.Controls.Add(nTo, 3, 0);
                rbAll.Width = f.SideW; rbAll.Height = f.D(26);
                left.Controls.Add(rbAll);
                left.Controls.Add(pagesRow);

                f.AddHeader(left, "PREVIEW");
                chkEdge = new CheckBox { Text = "Show where the printer can't print", Checked = f.previewEdge, AutoSize = false, Width = f.SideW, Height = f.D(26) };
                left.Controls.Add(chkEdge);
                lblInfo = new Label { AutoSize = false, Width = f.SideW, Height = f.D(150), ForeColor = TextCol, UseMnemonic = false };
                left.Controls.Add(lblInfo);

                // preview side
                var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(233, 237, 243) };
                var nav = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.White, RenderMode = ToolStripRenderMode.System, Padding = new Padding(f.D(8), f.D(3), f.D(8), f.D(3)) };
                var bPrevPage = new ToolStripButton("◀") { ToolTipText = "Previous page" };
                lblPage = new ToolStripLabel("Page 1 of 1");
                var bNextPage = new ToolStripButton("▶") { ToolTipText = "Next page" };
                var bOne = new ToolStripButton("1 page") { Checked = true };
                var bTwo = new ToolStripButton("2 pages");
                nav.Items.AddRange(new ToolStripItem[] { bPrevPage, lblPage, bNextPage, new ToolStripSeparator(), bOne, bTwo });
                pv = new PrintPreviewControl { Dock = DockStyle.Fill, AutoZoom = true, UseAntiAlias = true, BackColor = Color.FromArgb(233, 237, 243) };
                right.Controls.Add(pv);
                right.Controls.Add(nav);

                Controls.Add(right);
                Controls.Add(new Panel { Dock = DockStyle.Left, Width = 1, BackColor = LineCol });
                Controls.Add(leftWrap);
                AcceptButton = bPrint;
                CancelButton = bCancel;

                bPrevPage.Click += (s, e) => { pv.StartPage = Math.Max(0, pv.StartPage - pv.Columns); UpdatePageLabel(); };
                bNextPage.Click += (s, e) => { pv.StartPage = Math.Min(Math.Max(0, F.PrintPageCount - 1), pv.StartPage + pv.Columns); UpdatePageLabel(); };
                bOne.Click += (s, e) => { bOne.Checked = true; bTwo.Checked = false; pv.Columns = 1; UpdatePageLabel(); };
                bTwo.Click += (s, e) => { bOne.Checked = false; bTwo.Checked = true; pv.Columns = 2; UpdatePageLabel(); };
                cbPr.SelectedIndexChanged += (s, e) => { if (busy) return; F.cbPrinter.SelectedItem = cbPr.SelectedItem; SyncFromMain(); Regenerate(); };
                cbPa.SelectedIndexChanged += (s, e) => { if (busy) return; if (cbPa.SelectedIndex >= 0 && cbPa.SelectedIndex < F.cbPaper.Items.Count) F.cbPaper.SelectedIndex = cbPa.SelectedIndex; Regenerate(); };
                EventHandler orient = (s, e) =>
                {
                    if (busy || !((RadioButton)s).Checked) return;
                    if (rbL.Checked) F.rbLand.Checked = true; else F.rbPort.Checked = true;
                    Regenerate();
                };
                rbP.CheckedChanged += orient;
                rbL.CheckedChanged += orient;
                chkEdge.CheckedChanged += (s, e) => { F.previewEdge = chkEdge.Checked; pv.InvalidatePreview(); };
                nFrom.ValueChanged += (s, e) => { if (!busy) rbRange.Checked = true; };
                nTo.ValueChanged += (s, e) => { if (!busy) rbRange.Checked = true; };
                bPrint.Click += (s, e) => DoPrintNow();
                SyncFromMain();
                Shown += (s, e) => Regenerate();
                FormClosed += (s, e) => { if (pd != null) pd.Dispose(); };
            }

            // copy printer / paper / orientation from the main window into this window's controls
            void SyncFromMain()
            {
                busy = true;
                if (!Equals(cbPr.SelectedItem, F.cbPrinter.SelectedItem)) cbPr.SelectedItem = F.cbPrinter.SelectedItem;
                cbPa.Items.Clear();
                foreach (var it in F.cbPaper.Items) cbPa.Items.Add(it);
                cbPa.SelectedIndex = F.cbPaper.SelectedIndex;
                rbP.Checked = !F.landscape;
                rbL.Checked = F.landscape;
                busy = false;
            }

            void Regenerate()
            {
                if (pd != null) pd.Dispose();
                F.printFrom = 0; F.printTo = int.MaxValue;
                pd = F.MakeDoc();
                pv.Document = pd;
                pv.StartPage = Math.Min(pv.StartPage, Math.Max(0, F.PrintPageCount - 1));
                pv.InvalidatePreview();
                int n = F.PrintPageCount;
                busy = true;
                nFrom.Maximum = n; nTo.Maximum = n;
                if (nTo.Value > n || !rbRange.Checked) nTo.Value = n;
                if (nFrom.Value > n) nFrom.Value = 1;
                busy = false;
                UpdatePageLabel();
                // information
                var ps = pd.DefaultPageSettings;
                var sb = new StringBuilder();
                sb.AppendLine(string.Format("{0} page{1} · {2} · {3}", n, n == 1 ? "" : "s", F.paper != null ? new PaperItem(F.paper).ToString() : "?", F.landscape ? "landscape" : "portrait"));
                sb.AppendLine(string.Format("{0} photo{1}", F.items.Count, F.items.Count == 1 ? "" : "s"));
                if (F.MaxHardMargin > 0.05)
                    sb.AppendLine(string.Format("Printer can't print the outer edge: left {0:0.0}, top {1:0.0}, right {2:0.0}, bottom {3:0.0} mm.", F.hmL, F.hmT, F.hmR, F.hmB));
                int low = 0, veryLow = 0;
                for (int i = 0; i < F.items.Count; i++) { double d = F.EffDpi(i); if (d < 150) low++; if (d < 100) veryLow++; }
                if (low > 0) sb.AppendLine(string.Format("Note: {0} photo{1} will print below 150 dpi{2} and may look soft.", low, low == 1 ? "" : "s", veryLow > 0 ? " (" + veryLow + " below 100 dpi)" : ""));
                lblInfo.Text = sb.ToString();
            }

            void UpdatePageLabel()
            {
                int n = F.PrintPageCount, a = pv.StartPage + 1, b = Math.Min(n, pv.StartPage + pv.Columns);
                lblPage.Text = a == b ? string.Format("Page {0} of {1}", a, n) : string.Format("Pages {0}–{1} of {2}", a, b, n);
            }

            void DoPrintNow()
            {
                try
                {
                    F.printFrom = rbRange.Checked ? (int)nFrom.Value - 1 : 0;
                    F.printTo = rbRange.Checked ? (int)nTo.Value : int.MaxValue;
                    if (F.printTo <= F.printFrom) { MessageBox.Show(this, "Please check the page numbers.", Text); return; }
                    pd.PrinterSettings.Copies = (short)nCopies.Value;
                    pd.PrinterSettings.Collate = true;
                    pd.PrintController = new PrintControllerWithStatusDialog(new StandardPrintController(), "Printing");
                    pd.Print();
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Printing failed:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { F.printFrom = 0; F.printTo = int.MaxValue; }
            }
        }

        // ---- Save as PDF (each page rendered at 300 dpi by the same drawing code as printing)
        void SaveAsPdf()
        {
            if (items.Count == 0) { MessageBox.Show(this, "Add some photos first.", Text); return; }
            using (var d = new SaveFileDialog { Title = "Save as PDF", Filter = "PDF file (*.pdf)|*.pdf", DefaultExt = "pdf", FileName = "Photo layout" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                try { WritePdf(d.FileName, 300); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save the PDF: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { Cursor = Cursors.Default; }
            }
        }

        public void WritePdf(string path, int dpiOut)
        {
            var jpegs = new List<byte[]>();
            var px = new List<int[]>();
            double pw = PW, ph = PH;
            var jpg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                for (int page = 0; page < PrintPageCount; page++)
                {
                    int wpx = (int)Math.Round(pw / 25.4 * dpiOut), hpx = (int)Math.Round(ph / 25.4 * dpiOut);
                    using (var bmp = new Bitmap(wpx, hpx, PixelFormat.Format24bppRgb))
                    {
                        bmp.SetResolution(dpiOut, dpiOut);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.White);
                            g.PageUnit = GraphicsUnit.Millimeter;
                            SetQuality(g);
                            RenderPageContent(g, page, 0, 0, 1f, true);
                        }
                        using (var ms = new MemoryStream()) { bmp.Save(ms, jpg, ep); jpegs.Add(ms.ToArray()); }
                        px.Add(new[] { wpx, hpx });
                    }
                }
            }
            PdfWriter.Write(path, jpegs, px, pw * 72 / 25.4, ph * 72 / 25.4);
        }

        // ---- photo size presets
        void ApplySizeChoice()
        {
            int i = cbSize.SelectedIndex;
            if (i < 0) return;
            double w, h;
            if (i == SizeNames.Length - 1) { w = (double)nBoxW.Value * 10; h = (double)nBoxH.Value * 10; }
            else { w = SizeMM[i, 0]; h = SizeMM[i, 1]; }
            PushUndo("");
            if (free)
            {
                if (w > 0)
                    foreach (var p in Targets())
                    {
                        double ew, eh; EffDims(p, out ew, out eh);
                        double bw = ew >= eh ? Math.Max(w, h) : Math.Min(w, h), bh = ew >= eh ? Math.Min(w, h) : Math.Max(w, h);
                        p.FW = ew * Math.Min(bw / ew, bh / eh);
                        ClampFree(p);
                    }
            }
            else
            {
                boxW = Math.Max(0, w); boxH = Math.Max(0, h);
                if (boxW > 0) foreach (var p in items) { p.Fill = true; p.Scale = 1; p.Dx = p.Dy = 0; }
            }
            SyncControls();
            AfterChange();
        }

        // ---- recent layouts and photos
        static void AddRecent(List<string> list, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            list.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            while (list.Count > 10) list.RemoveAt(list.Count - 1);
        }

        void FillRecentMenu()
        {
            tbRecent.DropDownItems.Clear();
            var bold = new Font(Font, FontStyle.Bold);
            tbRecent.DropDownItems.Add(new ToolStripLabel("Layouts") { Font = bold, ForeColor = Muted });
            if (recentLayouts.Count == 0) tbRecent.DropDownItems.Add(new ToolStripMenuItem("(none yet)") { Enabled = false });
            foreach (var l in recentLayouts.ToList())
            {
                string path = l;
                var mi = new ToolStripMenuItem(Path.GetFileName(path)) { ToolTipText = path };
                mi.Click += (s, e) => { if (File.Exists(path)) LoadLayoutFile(path); else { MessageBox.Show(this, "File not found:\n" + path, Text); recentLayouts.Remove(path); } };
                tbRecent.DropDownItems.Add(mi);
            }
            tbRecent.DropDownItems.Add(new ToolStripSeparator());
            tbRecent.DropDownItems.Add(new ToolStripLabel("Photos (click to add)") { Font = bold, ForeColor = Muted });
            if (recentPhotos.Count == 0) tbRecent.DropDownItems.Add(new ToolStripMenuItem("(none yet)") { Enabled = false });
            foreach (var l in recentPhotos.ToList())
            {
                string path = l;
                var mi = new ToolStripMenuItem(Path.GetFileName(path)) { ToolTipText = path };
                mi.Click += (s, e) => { if (File.Exists(path)) AddFiles(new[] { path }); else { MessageBox.Show(this, "File not found:\n" + path, Text); recentPhotos.Remove(path); } };
                tbRecent.DropDownItems.Add(mi);
            }
            tbRecent.DropDownItems.Add(new ToolStripSeparator());
            var clear = new ToolStripMenuItem("Clear the list");
            clear.Click += (s, e) => { recentLayouts.Clear(); recentPhotos.Clear(); };
            tbRecent.DropDownItems.Add(clear);
        }

        // ---- files arriving from Explorer (own command line, or other copies started by the right-click menu)
        public void QueueFiles(IEnumerable<string> files) { foreach (var f in files) incoming.Enqueue(f); }

        void PumpIncoming()
        {
            string f;
            bool got = false;
            while (incoming.TryDequeue(out f))
            {
                got = true;
                if (f == "\u0001activate") { BringToFrontNow(); continue; }
                pendingFiles.Add(f);
            }
            if (got) lastIncoming = DateTime.Now;
            if (pendingFiles.Count > 0 && Visible && (DateTime.Now - lastIncoming).TotalMilliseconds > 450)
            {
                var list = pendingFiles.ToList();
                pendingFiles.Clear();
                BringToFrontNow();
                AddFiles(list);
            }
        }

        void BringToFrontNow()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        // test hook: when PPL_TEST_REPORT is set, list the loaded photos in that file
        void WriteTestReport()
        {
            string p = Environment.GetEnvironmentVariable("PPL_TEST_REPORT");
            if (string.IsNullOrEmpty(p)) return;
            try { File.WriteAllLines(p, items.Select(x => x.Path).ToArray()); } catch { }
        }

        public void StartPipeServer(string name)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        var sec = new PipeSecurity();
                        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                        using (var s = new NamedPipeServerStream(name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, sec))
                        {
                            s.WaitForConnection();
                            using (var r = new StreamReader(s, Encoding.UTF8))
                            {
                                string l;
                                while ((l = r.ReadLine()) != null) if (l.Length > 0) incoming.Enqueue(l);
                            }
                        }
                    }
                    catch { Thread.Sleep(200); }
                }
            }) { IsBackground = true, Name = "PhotoPrintLayout pipe" };
            t.Start();
        }

        // ------------------------------------------------------------ settings
        static string SettingsDir
        {
            get
            {
                string o = Environment.GetEnvironmentVariable("PPL_SETTINGS_DIR");   // tests use their own folder
                return string.IsNullOrEmpty(o) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoPrintLayout") : o;
            }
        }
        static string SettingsPath { get { return Path.Combine(SettingsDir, "settings.txt"); } }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                bool sawGuides = false;
                var ci = CultureInfo.InvariantCulture;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    int k = line.IndexOf('=');
                    if (k < 0) continue;
                    string key = line.Substring(0, k), v = line.Substring(k + 1);
                    int iv; double dv;
                    switch (key)
                    {
                        case "printer": printerName = v; break;
                        case "paper": savedPaperName = v; break;
                        case "landscape": landscape = v == "1"; break;
                        case "cut": cut = v == "1"; break;
                        case "free": free = v == "1"; break;
                        case "cols": if (int.TryParse(v, out iv)) cols = (int)Clamp(iv, 1, 12); break;
                        case "rows": if (int.TryParse(v, out iv)) rows = (int)Clamp(iv, 1, 12); break;
                        case "margin": if (double.TryParse(v, NumberStyles.Float, ci, out dv)) margin = Clamp(dv, 0, 50); break;
                        case "gap": if (double.TryParse(v, NumberStyles.Float, ci, out dv)) gap = Clamp(dv, 0, 50); break;
                        case "guides": if (int.TryParse(v, out iv)) { guideMode = (int)Clamp(iv, 0, 2); sawGuides = true; } break;
                        case "captions": if (int.TryParse(v, out iv)) captionMode = (int)Clamp(iv, 0, 2); break;
                        case "captionpt": if (double.TryParse(v, NumberStyles.Float, ci, out dv)) captionPt = Clamp(dv, 5, 36); break;
                        case "showedge": showEdge = v != "0"; break;
                        case "previewedge": previewEdge = v != "0"; break;
                        case "box":
                            {
                                var b = v.Split('x');
                                double bw, bh;
                                if (b.Length == 2 && double.TryParse(b[0], NumberStyles.Float, ci, out bw) && double.TryParse(b[1], NumberStyles.Float, ci, out bh)) { boxW = Clamp(bw, 0, 1000); boxH = Clamp(bh, 0, 1000); }
                                break;
                            }
                        case "win":
                            {
                                var b = v.Split(',');
                                int x, y, w, h;
                                if (b.Length >= 5 && int.TryParse(b[0], out x) && int.TryParse(b[1], out y) && int.TryParse(b[2], out w) && int.TryParse(b[3], out h)) { savedBounds = new Rectangle(x, y, w, h); savedMax = b[4] == "1"; }
                                break;
                            }
                        case "showgaps": showGaps = v != "0"; break;
                        case "aligngap": if (double.TryParse(v, NumberStyles.Float, ci, out dv)) alignGap = Clamp(dv, 0, 200); break;
                        case "recentlayout": if (recentLayouts.Count < 10) recentLayouts.Add(v); break;
                        case "recentphoto": if (recentPhotos.Count < 10) recentPhotos.Add(v); break;
                    }
                }
                if (!sawGuides) guideMode = cut ? 1 : 0;
                cut = guideMode == 1;
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                var ci = CultureInfo.InvariantCulture;
                var lines = new List<string>
                {
                    "printer=" + printerName,
                    "paper=" + (paper != null ? paper.PaperName : ""),
                    "landscape=" + (landscape ? "1" : "0"),
                    "cut=" + (cut ? "1" : "0"),
                    "free=" + (free ? "1" : "0"),
                    "cols=" + cols,
                    "rows=" + rows,
                    "margin=" + margin.ToString(ci),
                    "gap=" + gap.ToString(ci),
                    "guides=" + guideMode,
                    "captions=" + captionMode,
                    "captionpt=" + captionPt.ToString(ci),
                    "showedge=" + (showEdge ? "1" : "0"),
                    "previewedge=" + (previewEdge ? "1" : "0"),
                    "showgaps=" + (showGaps ? "1" : "0"),
                    "aligngap=" + alignGap.ToString(ci),
                    "box=" + boxW.ToString(ci) + "x" + boxH.ToString(ci),
                    "win=" + savedBounds.X + "," + savedBounds.Y + "," + savedBounds.Width + "," + savedBounds.Height + "," + (savedMax ? "1" : "0")
                };
                foreach (var l in recentLayouts) lines.Add("recentlayout=" + l);
                foreach (var l in recentPhotos) lines.Add("recentphoto=" + l);
                Directory.CreateDirectory(SettingsDir);
                string tmp = SettingsPath + ".tmp";
                File.WriteAllLines(tmp, lines);
                if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, null); else File.Move(tmp, SettingsPath);
            }
            catch { }
        }
    }

    // Minimal PDF writer: one full-page JPEG image per page, page size in points (1/72 inch).
    static class PdfWriter
    {
        public static void Write(string path, List<byte[]> jpegs, List<int[]> px, double wPt, double hPt)
        {
            var ci = CultureInfo.InvariantCulture;
            int n = jpegs.Count, size = 3 + 3 * n;
            var off = new long[size];
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                Action<string> w = t => { var b = Encoding.ASCII.GetBytes(t); fs.Write(b, 0, b.Length); };
                w("%PDF-1.4\n");
                fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);
                off[1] = fs.Position; w("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                off[2] = fs.Position;
                w("2 0 obj\n<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, n).Select(i => (3 + 3 * i) + " 0 R")) + "] /Count " + n + " >>\nendobj\n");
                string W = wPt.ToString("0.###", ci), H = hPt.ToString("0.###", ci);
                for (int i = 0; i < n; i++)
                {
                    int po = 3 + 3 * i, co = po + 1, io = po + 2;
                    off[po] = fs.Position;
                    w(po + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + W + " " + H + "] /Resources << /XObject << /Im0 " + io + " 0 R >> /ProcSet [/PDF /ImageC] >> /Contents " + co + " 0 R >>\nendobj\n");
                    string content = "q " + W + " 0 0 " + H + " 0 0 cm /Im0 Do Q\n";
                    off[co] = fs.Position;
                    w(co + " 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "endstream\nendobj\n");
                    off[io] = fs.Position;
                    w(io + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + px[i][0] + " /Height " + px[i][1] + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpegs[i].Length + " >>\nstream\n");
                    fs.Write(jpegs[i], 0, jpegs[i].Length);
                    w("\nendstream\nendobj\n");
                }
                long xref = fs.Position;
                w("xref\n0 " + size + "\n0000000000 65535 f \n");
                for (int i = 1; i < size; i++) w(off[i].ToString("0000000000", ci) + " 00000 n \n");
                w("trailer\n<< /Size " + size + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            // One window per user: copies started by Explorer (one per selected file) hand their files to the first copy.
            string name = "PhotoPrintLayout-" + Environment.UserName + "-" + (Environment.GetEnvironmentVariable("PPL_INSTANCE") ?? "main");
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            bool first;
            var mutex = new Mutex(true, "Local\\" + name, out first);
            if (!first && SendToRunning(name, args)) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var f = new MainForm();
            f.StartPipeServer(name);
            if (args.Length > 0) f.QueueFiles(args.Select(a => FullPath(a)));
            Application.Run(f);
            GC.KeepAlive(mutex);
        }

        static string FullPath(string a) { try { return Path.GetFullPath(a); } catch { return a; } }

        static bool SendToRunning(string name, string[] args)
        {
            var lines = args.Length > 0 ? args.Select(a => FullPath(a)).ToList() : new List<string> { "\u0001activate" };
            for (int i = 0; i < 25; i++)
            {
                try
                {
                    using (var c = new NamedPipeClientStream(".", name, PipeDirection.Out))
                    {
                        c.Connect(400);
                        using (var w = new StreamWriter(c, new UTF8Encoding(false)))
                        {
                            foreach (var l in lines) w.WriteLine(l);
                            w.Flush();
                        }
                        return true;
                    }
                }
                catch { Thread.Sleep(120); }
            }
            return false;
        }
    }
}
