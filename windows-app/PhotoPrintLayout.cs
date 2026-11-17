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
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

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
        static readonly int[] PresetN = { 1, 2, 3, 4, 6, 8, 9, 12, 16, 20 };
        static readonly int[,] PresetCR = { { 1, 1 }, { 1, 2 }, { 1, 3 }, { 2, 2 }, { 2, 3 }, { 2, 4 }, { 3, 3 }, { 3, 4 }, { 4, 4 }, { 4, 5 } };
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
            Text = "Photo Print Layout";
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
        static double Clamp(double v, double a, double b) { return v < a ? a : (v > b ? b : v); }

        // ------------------------------------------------------------------ UI
        void BuildUi()
        {
            preview = new PreviewPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(233, 237, 243), AllowDrop = true };
            preview.Paint += PreviewPaint;
            preview.MouseDown += PreviewMouseDown;
            preview.MouseMove += PreviewMouseMove;
            preview.MouseUp += PreviewMouseUp;
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
            var grid = new TableLayoutPanel { ColumnCount = 5, RowCount = 2, Width = W, Height = D(68), Margin = new Padding(0, D(2), 0, D(4)) };
            for (int i = 0; i < 5; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            for (int i = 0; i < PresetN.Length; i++)
            {
                int idx = i;
                var b = Btn(PresetN[i].ToString(), (s, e) =>
                {
                    int c = PresetCR[idx, 0], r = PresetCR[idx, 1];
                    if (landscape) { cols = r; rows = c; } else { cols = c; rows = r; }
                    SyncControls();
                    AfterChange();
                });
                b.Dock = DockStyle.Fill;
                b.Margin = new Padding(D(2));
                presetButtons.Add(b);
                grid.Controls.Add(b, i % 5, i / 5);
            }
            side.Controls.Add(grid);
            nCols = Num(1, 12, 0, 1);
            nRows = Num(1, 12, 0, 1);
            side.Controls.Add(Row(32, new float[] { 46, 24, 6, 24 }, SmallLabel("Columns × Rows"), nCols, CenterLabel("×"), nRows));
            nMargin = Num(0, 50, 1, 0.5m);
            nGap = Num(0, 50, 1, 0.5m);
            side.Controls.Add(Row(32, new float[] { 70, 30 }, SmallLabel("Page margin (mm)"), nMargin));
            side.Controls.Add(Row(32, new float[] { 70, 30 }, SmallLabel("Gap between photos (mm)"), nGap));
            chkCut = new CheckBox { Text = "Print thin cutting lines", AutoSize = false, Width = W, Height = D(26) };
            side.Controls.Add(chkCut);
            nCols.ValueChanged += (s, e) => { if (updating) return; cols = (int)nCols.Value; SyncControls(); AfterChange(); };
            nRows.ValueChanged += (s, e) => { if (updating) return; rows = (int)nRows.Value; SyncControls(); AfterChange(); };
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
            sb.Controls.Add(Row(32, null, rbFit, rbFill));

            sb.Controls.Add(SubHeader("Size"));
            tbSize = new TrackBar { Minimum = 10, Maximum = 300, TickStyle = TickStyle.None, AutoSize = false, SmallChange = 5, LargeChange = 10, Value = 100 };
            nSize = Num(5, 2000, 0, 5);
            sb.Controls.Add(Row(32, new float[] { 66, 34 }, tbSize, WithSuffix(nSize, "%")));
            nWmm = Num(1, 2000, 1, 1);
            nHmm = Num(1, 2000, 1, 1);
            sb.Controls.Add(Row(32, new float[] { 13, 37, 13, 37 }, SmallLabel("W"), WithSuffix(nWmm, "mm"), SmallLabel("H"), WithSuffix(nHmm, "mm")));
            sb.Controls.Add(Row(34, null, Btn("Reset size & position", (s, e) => Act("reset"))));
            tbSize.ValueChanged += (s, e) => { if (updating || sel == null) return; sel.Scale = tbSize.Value / 100.0; SizeChanged2(); };
            nSize.ValueChanged += (s, e) => { if (updating || sel == null) return; sel.Scale = (double)nSize.Value / 100.0; SizeChanged2(); };
            nWmm.ValueChanged += (s, e) => { if (updating || sel == null) return; SetSizeMm((double)nWmm.Value, true); };
            nHmm.ValueChanged += (s, e) => { if (updating || sel == null) return; SetSizeMm((double)nHmm.Value, false); };

            sb.Controls.Add(SubHeader("Order & copies"));
            sb.Controls.Add(Row(34, null, Btn("◀  Move earlier", (s, e) => Act("prev")), Btn("Move later  ▶", (s, e) => Act("next"))));
            sb.Controls.Add(Row(34, null, Btn("Duplicate", (s, e) => Act("dup")), Btn("Repeat to fill page", (s, e) => Act("fillpage"))));
            sb.Controls.Add(Row(34, null, Btn("✕  Remove from layout", (s, e) => Act("del"))));
            side.Controls.Add(sb);
            side.Controls.Add(Hint("On the page: drag a photo to move it inside its box, drag the blue corner square to resize it, " +
                                   "or drag it onto another box to swap places. Ctrl + mouse wheel also resizes. Right-click for more.\n" +
                                   "Keys: R rotate · F fit/fill · + / − size · Del remove · ← → order"));

            // All photos
            AddHeader(side, "ALL PHOTOS");
            side.Controls.Add(Row(34, null, Btn("Auto-rotate all", (s, e) => AllPhotos("autorot")), Btn("Reset rotation", (s, e) => AllPhotos("rot0"))));
            side.Controls.Add(Row(34, null, Btn("All: Fit", (s, e) => AllPhotos("fit")), Btn("All: Fill", (s, e) => AllPhotos("fill"))));
            side.Controls.Add(Row(34, null, Btn("Reset all sizes", (s, e) => AllPhotos("size")), Btn("Clear all", (s, e) => AllPhotos("clear"))));

            // Print
            AddHeader(side, "PRINT");
            var pv = Btn("Print preview…", (s, e) => DoPreview());
            var pr = PrimaryButton("🖨  Print…");
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
            AddCtx("Remove", "del");
        }

        void AddCtx(string text, string action)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Click += (s, e) => Act(action);
            ctx.Items.Add(mi);
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
            l.Height = TextRenderer.MeasureText(text, l.Font, new Size(SideW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + D(4);
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
            for (int i = 0; i < presetButtons.Count; i++)
            {
                int c = PresetCR[i, 0], r = PresetCR[i, 1];
                if (landscape) { int t = c; c = r; r = t; }
                bool on = c == cols && r == rows;
                presetButtons[i].BackColor = on ? Accent : BtnBack;
                presetButtons[i].ForeColor = on ? Color.White : TextCol;
            }
            updating = false;
            UpdateSel();
            UpdateInfo();
        }

        void UpdateInfo()
        {
            double w, h; CellSize(out w, out h);
            int n = items.Count, p = PageCount;
            if (n == 0) lblInfo.Text = string.Format("No photos yet — click “Add photos”.   Each box {0:0} × {1:0} mm", w, h);
            else lblInfo.Text = string.Format("{0} photo{1}  ·  {2} page{3}  ·  each box {4:0} × {5:0} mm", n, n == 1 ? "" : "s", p, p == 1 ? "" : "s", w, h);
            lblZoom.Text = Math.Round(zoom * 100) + "%";
        }

        void UpdateSel()
        {
            bool has = sel != null && items.Contains(sel);
            if (!has) sel = null;
            selBox.Enabled = has;
            updating = true;
            if (has)
            {
                lblSel.Text = sel.Name + "\n" + sel.W + " × " + sel.H + " px  ·  rotated " + sel.Rot + "°";
                rbFit.Checked = !sel.Fill;
                rbFill.Checked = sel.Fill;
                double W, H; CellSize(out W, out H);
                var q = GetGeo(sel, W, H);
                SetNum(nSize, sel.Scale * 100);
                tbSize.Value = (int)Clamp(Math.Round(sel.Scale * 100), tbSize.Minimum, tbSize.Maximum);
                SetNum(nWmm, q.EW);
                SetNum(nHmm, q.EH);
            }
            else
            {
                lblSel.Text = "Click a photo on the page to select it.";
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
        int PageCount { get { return Math.Max(1, (items.Count + PerPage - 1) / PerPage); } }
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
            if (si < 0) return false;
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

        void PreviewPaint(object s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TranslateTransform(preview.AutoScrollPosition.X, preview.AutoScrollPosition.Y);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var clip = e.ClipRectangle;
            clip.Offset(-preview.AutoScrollPosition.X, -preview.AutoScrollPosition.Y);
            double W, H; CellSize(out W, out H);
            int n = PageCount, per = PerPage;
            float k = Ppm;
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var mutedBrush = new SolidBrush(Muted))
            using (var emptyBrush = new SolidBrush(Color.FromArgb(154, 163, 178)))
            using (var shadow = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
            using (var dash = new Pen(Color.FromArgb(197, 203, 214), 1) { DashStyle = DashStyle.Dash })
            using (var cutPen = new Pen(Color.FromArgb(156, 163, 175), Math.Max(1f, 0.25f * k)))
            {
                for (int p = 0; p < n; p++)
                {
                    var pr = PageRectPx(p);
                    if (pr.Bottom < clip.Top - LabelH || pr.Top - LabelH > clip.Bottom) continue;
                    g.FillRectangle(shadow, pr.X + D(2), pr.Y + D(3), pr.Width, pr.Height);
                    g.FillRectangle(Brushes.White, pr);
                    g.DrawString("Page " + (p + 1) + " of " + n, Font, mutedBrush, new RectangleF(pr.X, pr.Y - LabelH, pr.Width, LabelH), center);
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
            int si = sel == null ? -1 : items.IndexOf(sel);
            if (si >= 0)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var cr = CellRectPx(si);
                using (var pen = new Pen(Accent, D(2))) g.DrawRectangle(pen, cr.X, cr.Y, cr.Width, cr.Height);
                var vr = VisibleImageRect(si);
                using (var pen = new Pen(Accent, 1) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, vr.X, vr.Y, vr.Width, vr.Height);
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
        }

        // --------------------------------------------------------------- mouse
        void Select(Photo p)
        {
            sel = p;
            preview.Invalidate();
            UpdateSel();
        }

        void PreviewMouseDown(object s, MouseEventArgs e)
        {
            var wp = World(e.Location);
            if (e.Button == MouseButtons.Left && OnHandle(wp))
            {
                int si = items.IndexOf(sel);
                drag = DragMode.Resize;
                dragIndex = si;
                dragStart = e.Location;
                dragScale = sel.Scale;
                var cr = CellRectPx(si);
                double W, H; CellSize(out W, out H);
                var q = GetGeo(sel, W, H);
                dragCenter = new PointF((float)(cr.X + cr.Width / 2 + q.Dx * Ppm), (float)(cr.Y + cr.Height / 2 + q.Dy * Ppm));
                return;
            }
            int hit = HitTest(e.Location);
            if (hit < 0) { Select(null); return; }
            if (hit >= items.Count)
            {
                Select(null);
                if (e.Button == MouseButtons.Left) BrowseAdd();
                return;
            }
            Select(items[hit]);
            if (e.Button == MouseButtons.Right) { ctx.Show(preview, e.Location); return; }
            if (e.Button == MouseButtons.Left)
            {
                drag = DragMode.Move;
                dragMoved = false;
                dragIndex = hit;
                dragStart = e.Location;
                double W, H; CellSize(out W, out H);
                var q = GetGeo(sel, W, H);
                dragDx = q.Dx; dragDy = q.Dy;
            }
        }

        void PreviewMouseMove(object s, MouseEventArgs e)
        {
            var wp = World(e.Location);
            if (drag == DragMode.Resize && sel != null)
            {
                var p0 = World(dragStart);
                double d0 = Math.Sqrt(Math.Pow(p0.X - dragCenter.X, 2) + Math.Pow(p0.Y - dragCenter.Y, 2));
                double d1 = Math.Sqrt(Math.Pow(wp.X - dragCenter.X, 2) + Math.Pow(wp.Y - dragCenter.Y, 2));
                if (d0 < 1) d0 = 1;
                sel.Scale = Clamp(dragScale * d1 / d0, 0.05, 20);
                preview.Invalidate();
                UpdateSel();
                return;
            }
            if (drag == DragMode.Move && sel != null)
            {
                if (!dragMoved && Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) < D(4)) return;
                dragMoved = true;
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
            if (OnHandle(wp)) preview.Cursor = Cursors.SizeNWSE;
            else
            {
                int h = HitTest(e.Location);
                preview.Cursor = h < 0 ? Cursors.Default : (h >= items.Count ? Cursors.Hand : Cursors.SizeAll);
            }
        }

        void PreviewMouseUp(object s, MouseEventArgs e)
        {
            if (drag == DragMode.Move && dropTarget >= 0 && dragIndex >= 0 && dragIndex < items.Count)
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
            drag = DragMode.None;
            dropTarget = -1;
            dragIndex = -1;
            AfterChange();
        }

        bool CtrlWheel(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit >= 0 && hit < items.Count)
            {
                if (sel != items[hit]) Select(items[hit]);
                sel.Scale = Clamp(sel.Scale * (e.Delta > 0 ? 1.05 : 1 / 1.05), 0.05, 20);
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
            switch (e.KeyCode)
            {
                case Keys.R: Act(e.Shift ? "rl" : "rr"); break;
                case Keys.F: Act(sel.Fill ? "fit" : "fill"); break;
                case Keys.Delete:
                case Keys.Back: Act("del"); break;
                case Keys.Left: Act("prev"); break;
                case Keys.Right: Act("next"); break;
                case Keys.Oemplus:
                case Keys.Add: sel.Scale = Clamp(sel.Scale * 1.05, 0.05, 20); SizeChanged2(); break;
                case Keys.OemMinus:
                case Keys.Subtract: sel.Scale = Clamp(sel.Scale / 1.05, 0.05, 20); SizeChanged2(); break;
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

        void SetSizeMm(double mm, bool width)
        {
            double W, H; CellSize(out W, out H);
            var q = GetGeo(sel, W, H);
            double ew, eh; EffDims(sel, out ew, out eh);
            double unit = (width ? ew : eh) * q.Base;
            if (unit <= 0) return;
            sel.Scale = Clamp(mm / unit, 0.05, 20);
            SizeChanged2();
        }

        void Act(string a)
        {
            if (sel == null) return;
            int i = items.IndexOf(sel);
            if (i < 0) return;
            var p = sel;
            switch (a)
            {
                case "rl": p.Rot = (p.Rot + 270) % 360; p.Dx = p.Dy = 0; break;
                case "rr": p.Rot = (p.Rot + 90) % 360; p.Dx = p.Dy = 0; break;
                case "fit": p.Fill = false; p.Scale = 1; p.Dx = p.Dy = 0; break;
                case "fill": p.Fill = true; p.Scale = 1; p.Dx = p.Dy = 0; break;
                case "reset": p.Scale = 1; p.Dx = p.Dy = 0; break;
                case "prev": if (i > 0) { items[i] = items[i - 1]; items[i - 1] = p; } break;
                case "next": if (i < items.Count - 1) { items[i] = items[i + 1]; items[i + 1] = p; } break;
                case "dup": items.Insert(i + 1, p.Copy()); break;
                case "fillpage":
                    {
                        int n = PerPage - (i % PerPage) - 1;
                        for (int k = 0; k < n; k++) items.Insert(i + 1, p.Copy());
                        break;
                    }
                case "del":
                    items.RemoveAt(i);
                    sel = items.Count == 0 ? null : items[Math.Min(i, items.Count - 1)];
                    break;
            }
            AfterChange();
        }

        void AllPhotos(string a)
        {
            double W, H; CellSize(out W, out H);
            if (a == "clear")
            {
                if (items.Count == 0 || MessageBox.Show(this, "Remove all photos from the layout?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                items.Clear();
                sel = null;
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
            AfterChange();
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
            Cursor = Cursors.WaitCursor;
            var res = new LoadResult[list.Count];
            Parallel.For(0, list.Count, i => { res[i] = Loader.LoadGdi(list[i], 1600); });
            var failed = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                if (res[i] == null) res[i] = Loader.LoadWic(list[i], 1600);
                if (res[i] == null) { failed.Add(Path.GetFileName(list[i])); continue; }
                items.Add(new Photo { Path = list[i], Name = Path.GetFileName(list[i]), Preview = res[i].Bmp, W = res[i].FullW, H = res[i].FullH });
            }
            Cursor = Cursors.Default;
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
            for (int sl = 0; sl < per; sl++)
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
            e.HasMorePages = printPage < PageCount;
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
            if (args.Length > 0) f.Shown += (s, e) => f.AddFiles(args);
            Application.Run(f);
        }
    }
}
