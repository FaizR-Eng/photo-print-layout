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
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Photo Print Layout")]
[assembly: System.Reflection.AssemblyProduct("Photo Print Layout")]
[assembly: System.Reflection.AssemblyVersion("1.3.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.1.0")]

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

        PreviewPanel preview;
        ComboBox cbPrinter, cbPaper;
        RadioButton rbPort, rbLand, rbFit, rbFill;
        NumericUpDown nCols, nRows, nMargin, nGap, nSize, nWmm, nHmm;
        CheckBox chkCut;
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
        Control arrangeBox;
        Button btnUndo, btnRedo;
        CheckBox chkAlignPage, chkSnap, chkGrid;
        readonly Dictionary<Photo, double[]> dragStartPos = new Dictionary<Photo, double[]>();
        bool pendingSingle;
        bool marquee;
        PointF marqA, marqB;
        List<Photo> marqBase = new List<Photo>();
        readonly List<double[]> guides = new List<double[]>();   // { 1 = vertical / 0 = horizontal, position mm, page }
        class Snap { public List<Photo> Items; public int[] SelIdx; public int Primary; public bool Free; public int Cols, Rows; }
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
            FillPrinters();
            SyncControls();
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            Shown += (s, e) => { FitZoom(); preview.Focus(); };
            FormClosing += (s, e) => SaveSettings();
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

            Controls.Add(preview);
            Controls.Add(side);
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
            btnUndo = Btn("↶  Undo", (s, e) => Undo());
            btnRedo = Btn("↷  Redo", (s, e) => Redo());
            tips.SetToolTip(btnUndo, "Undo the last change (Ctrl+Z)");
            tips.SetToolTip(btnRedo, "Redo (Ctrl+Y)");
            side.Controls.Add(Row(32, null, btnUndo, btnRedo));
            var bOpen = Btn("Open layout…", (s, e) => OpenLayout());
            var bSave = Btn("Save layout…", (s, e) => SaveLayout());
            tips.SetToolTip(bOpen, "Open a layout you saved before");
            tips.SetToolTip(bSave, "Save this arrangement to reprint or edit later (Ctrl+S)");
            side.Controls.Add(Row(32, null, bOpen, bSave));

            // Printer & paper
            AddHeader(side, "PRINTER & PAPER");
            cbPrinter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cbPaper = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cbPrinter.SelectedIndexChanged += (s, e) =>
            {
                if (updating) return;
                printerName = cbPrinter.SelectedItem as string ?? "";
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
            nCols = Num(1, 12, 0, 1);
            nRows = Num(1, 12, 0, 1);
            side.Controls.Add(Row(32, new float[] { 46, 24, 6, 24 }, SmallLabel("Columns × Rows"), nCols, CenterLabel("×"), nRows));
            nMargin = Num(0, 50, 1, 0.5m);
            nGap = Num(0, 50, 1, 0.5m);
            side.Controls.Add(Row(32, new float[] { 70, 30 }, SmallLabel("Page margin (mm)"), nMargin));
            side.Controls.Add(Row(32, new float[] { 70, 30 }, SmallLabel("Gap between photos (mm)"), nGap));
            chkCut = new CheckBox { Text = "Print thin cutting lines", AutoSize = false, Width = W, Height = D(26) };
            side.Controls.Add(chkCut);
            nCols.ValueChanged += (s, e) => { if (updating) return; PushUndo("cols"); cols = (int)nCols.Value; SyncControls(); AfterChange(); };
            nRows.ValueChanged += (s, e) => { if (updating) return; PushUndo("rows"); rows = (int)nRows.Value; SyncControls(); AfterChange(); };
            nMargin.ValueChanged += (s, e) => { if (updating) return; margin = (double)nMargin.Value; AfterChange(); };
            nGap.ValueChanged += (s, e) => { if (updating) return; gap = (double)nGap.Value; AfterChange(); };
            chkCut.CheckedChanged += (s, e) => { if (updating) return; cut = chkCut.Checked; preview.Invalidate(); };

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
            ab.Controls.Add(Hint("Select several photos with Ctrl+click or Shift+click, or drag a box around them from an empty spot. Ctrl+A selects all. " +
                                 "With one photo selected, Align uses the page margins. Hold Alt while dragging to turn snapping off."));
            side.Controls.Add(ab);

            // All photos
            AddHeader(side, "ALL PHOTOS");
            side.Controls.Add(Row(34, null, Btn("Auto-rotate all", (s, e) => AllPhotos("autorot")), Btn("Reset rotation", (s, e) => AllPhotos("rot0"))));
            side.Controls.Add(Row(34, null, Btn("All: Fit", (s, e) => AllPhotos("fit")), Btn("All: Fill", (s, e) => AllPhotos("fill"))));
            side.Controls.Add(Row(34, null, btnResetAll = Btn("Reset all sizes", (s, e) => AllPhotos("size")), Btn("Clear all", (s, e) => AllPhotos("clear"))));

            // Print
            AddHeader(side, "PRINT");
            var pv = Btn("Print preview…", (s, e) => DoPreview());
            var pr = PrimaryButton("Print…");
            pr.Click += (s, e) => DoPrint();
            pr.Dock = DockStyle.Fill;
            side.Controls.Add(Row(40, new float[] { 45, 55 }, pv, pr));
            side.Controls.Add(Hint("In the Print window you can pick copies and use Preferences for quality and paper type. " +
                                   "The paper size is taken from this app."));

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
            chkCut.Checked = cut;
            nCols.Enabled = nRows.Enabled = !free;
            for (int i = 0; i < presetButtons.Count; i++)
            {
                int c = PresetCR[i, 0], r = PresetCR[i, 1];
                if (landscape) { int t = c; c = r; r = t; }
                bool on = PresetN[i] == 0 ? free : (!free && c == cols && r == rows);
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
            btnUndo.Enabled = undo.Count > 0;
            btnRedo.Enabled = redo.Count > 0;
            updating = true;
            if (has)
            {
                if (n > 1)
                    lblSel.Text = n + " photos selected — changes apply to all\nNumbers show the last one clicked: " + sel.Name;
                else
                    lblSel.Text = sel.Name + "\n" + sel.W + " × " + sel.H + " px  ·  rotated " + sel.Rot + "°" + (free ? "  ·  page " + (sel.FPage + 1) : "");
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
        int PerPage { get { return cols * rows; } }
        int FreeLastPage { get { int m = 0; foreach (var p in items) if (p.FPage > m) m = p.FPage; return m; } }
        // pages shown on screen (free layout shows one extra empty page to drag photos onto)
        int PageCount { get { if (free) return items.Count == 0 ? 1 : FreeLastPage + 2; return Math.Max(1, (items.Count + PerPage - 1) / PerPage); } }
        // pages that get printed
        int PrintPageCount { get { if (free) return items.Count == 0 ? 1 : FreeLastPage + 1; return PageCount; } }
        float Ppm { get { return zoom * 96f * dpi / 25.4f; } }
        int LabelH { get { return D(26); } }
        int GapPx { get { return D(22); } }
        int Pad { get { return D(20); } }

        void CellSize(out double w, out double h)
        {
            w = (PW - 2 * margin - (cols - 1) * gap) / cols;
            h = (PH - 2 * margin - (rows - 1) * gap) / rows;
            if (w < 1) w = 1;
            if (h < 1) h = 1;
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

        RectangleF CellRectMm(int slot)
        {
            double w, h; CellSize(out w, out h);
            int c = slot % cols, r = slot / cols;
            return new RectangleF((float)(margin + c * (w + gap)), (float)(margin + r * (h + gap)), (float)w, (float)h);
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

        void DrawSizeLabel(Graphics g, int index, bool showPos)
        {
            var p = items[index];
            var vr = VisibleImageRect(index);
            double wmm, hmm;
            if (free) { wmm = p.FW; hmm = FH(p); }
            else { wmm = vr.Width / Ppm; hmm = vr.Height / Ppm; }   // printed (visible) part
            string t = string.Format("{0:0.0} × {1:0.0} cm", wmm / 10, hmm / 10);
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
            double W, H; CellSize(out W, out H);
            int n = PageCount, per = PerPage, nPrint = PrintPageCount;
            float k = Ppm;
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var mutedBrush = new SolidBrush(Muted))
            using (var emptyBrush = new SolidBrush(Color.FromArgb(154, 163, 178)))
            using (var shadow = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
            using (var dash = new Pen(Color.FromArgb(197, 203, 214), 1) { DashStyle = DashStyle.Dash })
            using (var guide = new Pen(Color.FromArgb(147, 197, 253), 1) { DashStyle = DashStyle.Dot })
            using (var gridPen = new Pen(Color.FromArgb(228, 233, 242), 1))
            using (var cutPen = new Pen(Color.FromArgb(156, 163, 175), Math.Max(1f, 0.25f * k)))
            {
                for (int p = 0; p < n; p++)
                {
                    var pr = PageRectPx(p);
                    if (pr.Bottom < clip.Top - LabelH || pr.Top - LabelH > clip.Bottom) continue;
                    g.FillRectangle(shadow, pr.X + D(2), pr.Y + D(3), pr.Width, pr.Height);
                    g.FillRectangle(Brushes.White, pr);
                    string label = (free && p >= nPrint) ? "Extra page (not printed unless you put a photo on it)" : "Page " + (p + 1) + " of " + nPrint;
                    g.DrawString(label, Font, mutedBrush, new RectangleF(pr.X - D(150), pr.Y - LabelH, pr.Width + D(300), LabelH), center);
                    if (free)
                    {
                        if (showGrid)
                        {
                            for (double x = 10; x < PW; x += 10) g.DrawLine(gridPen, pr.X + (float)x * k, pr.Y, pr.X + (float)x * k, pr.Bottom);
                            for (double y = 10; y < PH; y += 10) g.DrawLine(gridPen, pr.X, pr.Y + (float)y * k, pr.Right, pr.Y + (float)y * k);
                        }
                        if (margin > 0)
                            g.DrawRectangle(guide, pr.X + (float)margin * k, pr.Y + (float)margin * k, pr.Width - 2 * (float)margin * k, pr.Height - 2 * (float)margin * k);
                        var st = g.Save();
                        g.SetClip(pr, CombineMode.Intersect);
                        bool any = false;
                        foreach (var ph in items)
                        {
                            if (ph.FPage != p) continue;
                            any = true;
                            if (ph.Preview != null) DrawFree(g, ph, ph.Preview, pr.X, pr.Y, k);
                            if (cut) { var r = FreeRectPx(ph); g.DrawRectangle(cutPen, r.X, r.Y, r.Width, r.Height); }
                        }
                        g.Restore(st);
                        if (!any) g.DrawString(items.Count == 0 ? "Double-click or drop photos here" : "Drag a photo here, or double-click to add photos", Font, emptyBrush, pr, center);
                        continue;
                    }
                    for (int sl = 0; sl < per; sl++)
                    {
                        int idx = p * per + sl;
                        var cr = CellRectPx(idx);
                        if (idx < items.Count)
                        {
                            var ph = items[idx];
                            if (ph.Preview != null) DrawPhoto(g, ph, ph.Preview, cr, W, H, k);
                            if (cut) g.DrawRectangle(cutPen, cr.X, cr.Y, cr.Width, cr.Height);
                        }
                        else
                        {
                            g.DrawRectangle(dash, cr.X + 1, cr.Y + 1, cr.Width - 2, cr.Height - 2);
                            g.DrawString("+ Add photo", Font, emptyBrush, cr, center);
                        }
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
                            if (list.Count < 2 || (list.Count < 3 && !toPage)) break;
                            var sorted = list.OrderBy(p => p.FX).ToList();
                            double space = (R - L - sorted.Sum(p => p.FW)) / (sorted.Count - 1);
                            double x = L;
                            foreach (var p in sorted) { p.FX = x; x += p.FW + space; }
                            break;
                        }
                    case "distv":
                        {
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
            var sn = new Snap { Items = items.Select(p => p.Copy()).ToList(), Free = free, Cols = cols, Rows = rows };
            sn.SelIdx = selList.Select(p => items.IndexOf(p)).Where(i => i >= 0).ToArray();
            sn.Primary = sel == null ? -1 : items.IndexOf(sel);
            return sn;
        }

        void RestoreSnap(Snap sn)
        {
            items.Clear();
            items.AddRange(sn.Items.Select(p => p.Copy()));
            free = sn.Free; cols = sn.Cols; rows = sn.Rows;
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
                        margin.ToString("R", ci), gap.ToString("R", ci), cut ? "1" : "0", free ? "1" : "0" })
                };
                foreach (var p in items)
                    lines.Add(string.Join("\t", new[] { "photo", p.Path, p.Rot.ToString(ci), p.Fill ? "1" : "0", p.Scale.ToString("R", ci),
                        p.Dx.ToString("R", ci), p.Dy.ToString("R", ci), p.FreeSet ? "1" : "0", p.FPage.ToString(ci),
                        p.FX.ToString("R", ci), p.FY.ToString("R", ci), p.FW.ToString("R", ci) }));
                try { File.WriteAllLines(d.FileName, lines); }
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
                    }
                    else if (f[0] == "photo" && f.Length >= 12) specs.Add(f);
                }
                items.Clear();
                selList.Clear();
                sel = null;
                Cursor = Cursors.WaitCursor;
                var res = new LoadResult[specs.Count];
                Parallel.For(0, specs.Count, i => { res[i] = Loader.LoadGdi(specs[i][1], 1600); });
                var missing = new List<string>();
                for (int i = 0; i < specs.Count; i++)
                {
                    var f = specs[i];
                    if (res[i] == null) res[i] = Loader.LoadWic(f[1], 1600);
                    if (res[i] == null) { missing.Add(f[1]); continue; }
                    items.Add(new Photo
                    {
                        Path = f[1], Name = Path.GetFileName(f[1]), Preview = res[i].Bmp, W = res[i].FullW, H = res[i].FullH,
                        Rot = int.Parse(f[2], ci), Fill = f[3] == "1", Scale = double.Parse(f[4], ci),
                        Dx = double.Parse(f[5], ci), Dy = double.Parse(f[6], ci), FreeSet = f[7] == "1", FPage = int.Parse(f[8], ci),
                        FX = double.Parse(f[9], ci), FY = double.Parse(f[10], ci), FW = double.Parse(f[11], ci)
                    });
                }
                Cursor = Cursors.Default;
                if (free) CompactFreePages();
                SyncControls();
                FitZoom();
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
                Filter = "Images|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif|All files|*.*"
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
            var res = new LoadResult[list.Count];
            Parallel.For(0, list.Count, i => { res[i] = Loader.LoadGdi(list[i], 1600); });
            var failed = new List<string>();
            var added = new List<Photo>();
            for (int i = 0; i < list.Count; i++)
            {
                if (res[i] == null) res[i] = Loader.LoadWic(list[i], 1600);
                if (res[i] == null) { failed.Add(Path.GetFileName(list[i])); continue; }
                var np = new Photo { Path = list[i], Name = Path.GetFileName(list[i]), Preview = res[i].Bmp, W = res[i].FullW, H = res[i].FullH };
                items.Add(np);
                added.Add(np);
            }
            Cursor = Cursors.Default;
            if (free) PackFree(added, false);
            AfterChange();
            if (failed.Count > 0)
                MessageBox.Show(this, "These files could not be opened:\n\n" + string.Join("\n", failed.Take(15)) +
                    "\n\nFor HEIC photos, install “HEIF Image Extensions” from the Microsoft Store, or convert them to JPG.",
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
            if (printerName != "") pd.PrinterSettings.PrinterName = printerName;
            ApplyPage(pd);
            pd.BeginPrint += (s, e) => printPage = 0;
            pd.PrintPage += PrintPageHandler;
            return pd;
        }

        void DoPreview()
        {
            if (items.Count == 0) { MessageBox.Show(this, "Add some photos first.", Text); return; }
            using (var pd = MakeDoc())
            using (var d = new PrintPreviewDialog { Document = pd, Width = D(900), Height = D(820), StartPosition = FormStartPosition.CenterParent, UseAntiAlias = true })
            {
                d.ShowDialog(this);
            }
        }

        void DoPrint()
        {
            if (items.Count == 0) { MessageBox.Show(this, "Add some photos first.", Text); return; }
            using (var pd = MakeDoc())
            using (var dlg = new PrintDialog { Document = pd, UseEXDialog = true, AllowSomePages = false, AllowSelection = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string chosen = pd.PrinterSettings.PrinterName;
                if (chosen != printerName && cbPrinter.Items.Contains(chosen)) cbPrinter.SelectedItem = chosen;
                ApplyPage(pd);
                try { pd.Print(); }
                catch (Exception ex) { MessageBox.Show(this, "Printing failed:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void PrintPageHandler(object sender, PrintPageEventArgs e)
        {
            var g = e.Graphics;
            var pd = (PrintDocument)sender;
            bool isPreview = pd.PrintController != null && pd.PrintController.IsPreview;
            g.PageUnit = GraphicsUnit.Millimeter;
            if (!isPreview) g.TranslateTransform(-e.PageSettings.HardMarginX * 0.254f, -e.PageSettings.HardMarginY * 0.254f);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            double W, H; CellSize(out W, out H);
            int per = PerPage;
            if (free) PrintFreePage(g);
            else for (int sl = 0; sl < per; sl++)
            {
                int idx = printPage * per + sl;
                if (idx >= items.Count) break;
                var ph = items[idx];
                var cell = CellRectMm(sl);
                var q = GetGeo(ph, W, H);
                int need = (int)Math.Ceiling(Math.Max(q.IW, q.IH) / 25.4 * 360);   // ~360 dpi is plenty for inkjet photos
                var r = Loader.LoadAny(ph.Path, Math.Max(need, 64));
                Image img = r != null ? (Image)r.Bmp : ph.Preview;
                if (img != null) DrawPhoto(g, ph, img, cell, W, H, 1f);
                if (r != null) r.Bmp.Dispose();
                if (cut) using (var pen = new Pen(Color.FromArgb(150, 150, 150), 0.2f)) g.DrawRectangle(pen, cell.X, cell.Y, cell.Width, cell.Height);
            }
            printPage++;
            e.HasMorePages = printPage < PrintPageCount;
        }

        void PrintFreePage(Graphics g)
        {
            foreach (var ph in items)
            {
                if (ph.FPage != printPage) continue;
                double ew, eh; EffDims(ph, out ew, out eh);
                double s = ph.FW / ew;
                int need = (int)Math.Ceiling(Math.Max(ph.W, ph.H) * s / 25.4 * 360);
                var r = Loader.LoadAny(ph.Path, Math.Max(need, 64));
                Image img = r != null ? (Image)r.Bmp : ph.Preview;
                if (img != null) DrawFree(g, ph, img, 0, 0, 1f);
                if (r != null) r.Bmp.Dispose();
                if (cut) using (var pen = new Pen(Color.FromArgb(150, 150, 150), 0.2f)) g.DrawRectangle(pen, (float)ph.FX, (float)ph.FY, (float)ph.FW, (float)FH(ph));
            }
        }

        // ------------------------------------------------------------ settings
        static string SettingsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoPrintLayout", "settings.txt"); }
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
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
                        case "margin": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) margin = Clamp(dv, 0, 50); break;
                        case "gap": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) gap = Clamp(dv, 0, 50); break;
                    }
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[]
                {
                    "printer=" + printerName,
                    "paper=" + (paper != null ? paper.PaperName : ""),
                    "landscape=" + (landscape ? "1" : "0"),
                    "cut=" + (cut ? "1" : "0"),
                    "free=" + (free ? "1" : "0"),
                    "cols=" + cols,
                    "rows=" + rows,
                    "margin=" + margin.ToString(CultureInfo.InvariantCulture),
                    "gap=" + gap.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch { }
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var f = new MainForm();
            if (args.Length > 0) f.Shown += (s, e) => f.AddFiles(args);   // photos or a .photolayout file
            Application.Run(f);
        }
    }
}
