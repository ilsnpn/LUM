// LUM - bulle caméra ronde, toujours au premier plan + enregistrement de l'écran où elle se trouve.
// Fenêtre "layered" (transparence par pixel) : les coins transparents laissent passer les clics.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Lum
{
    public sealed class BubbleForm : Form
    {
        // ------------------------------------------------------------ réglages visuels (px logiques @96 dpi)
        private static readonly int[] SizesDip = { 140, 220, 320 };   // Petit / Moyen / Grand
        private const int PadDip = 14;          // marge pour l'ombre
        private const int PillHDip = 44;        // hauteur de la barre de menu
        private const int PillOverlapDip = 16;  // la barre chevauche le bas du cercle
        private const int BtnDip = 30;          // taille d'un bouton
        private const int ScreenMarginDip = 8;  // distance mini au bord de l'écran
        private const int MagnetDip = 28;       // distance d'aimantation

        private const int StopWDip = 84;        // bouton "■ 0:00" (plus large)
        private const int CountdownSeconds = 3;

        private enum Btn { None, Record, SizeS, SizeM, SizeL, Mirror, Close, Stop, Pause, Discard }
        private enum RecState { Idle, Countdown, Recording, Finishing }

        private readonly Settings _settings;
        private readonly float _k;               // facteur DPI
        private int _sizeIndex;
        private bool _mirror;
        private int _cameraIndex;

        private CameraCapture _capture;
        private readonly object _frameLock = new object();
        private Bitmap _pendingFrame;            // dernière image reçue (thread capture)
        private Bitmap _frame;                   // image affichée (thread UI)
        private int _renderQueued;
        private string _status = "Démarrage de la caméra…";

        private bool _hover;
        private float _menuAlpha;                // 0..1 (animation d'apparition)
        private Btn _hotBtn = Btn.None;
        private Btn _pressedBtn = Btn.None;
        private bool _pillBelow = true;
        private int _pillOffsetX;                // position X de la barre dans la fenêtre

        private readonly System.Windows.Forms.Timer _uiTimer;
        private int _topmostTick;
        private ContextMenuStrip _menu;

        // --- enregistrement (V2)
        private RecState _rec = RecState.Idle;
        private ScreenRecorder _recorder;
        private DateTime _countdownStart;
        private int _shownSecond = -1;
        private bool _excluded;                  // bulle exclue de la capture => on la redessine nous-mêmes
        private Screen _lockedScreen;            // écran enregistré : la bulle ne peut pas le quitter
        private bool _closeAfterSave;
        private readonly object _overlayLock = new object();
        private Bitmap _overlay;                 // copie de la bulle (sans la barre) pour la vidéo
        private int _mic;

        public BubbleForm(Settings settings)
        {
            _settings = settings;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) _k = g.DpiX / 96f;

            _sizeIndex = Math.Max(0, Math.Min(2, settings.SizeIndex));
            _mirror = settings.Mirror;
            _cameraIndex = settings.CameraIndex;
            _mic = settings.MicDevice;

            Text = "LUM";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            Size sz = WindowSizeFor(CircleSize);
            Point loc;
            if (settings.HasPosition) loc = new Point(settings.X, settings.Y);
            else
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int m = D(ScreenMarginDip) + D(24);
                loc = new Point(wa.Right - m - CircleSize - CircleOffset(CircleSize).X,
                                wa.Bottom - m - CircleSize - CircleOffset(CircleSize).Y);
            }
            Bounds = new Rectangle(ClampWindowLocation(loc, sz, false), sz);

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 16;
            _uiTimer.Tick += OnUiTick;

            BuildContextMenu();
        }

        // ============================================================ géométrie
        private int D(int dip) { return (int)Math.Round(dip * _k); }
        private int CircleSize { get { return D(SizesDip[_sizeIndex]); } }
        private int PillW { get { return D(12 + BtnDip + 21 + BtnDip * 3 + 4 * 2 + 21 + BtnDip + 4 + BtnDip + 12); } }
        private bool RecordingUi { get { return _rec != RecState.Idle; } }

        // Espace réservé autour du cercle : l'ombre + la barre (dessous OU dessus, alignée à gauche/droite/centre).
        private Point CircleOffset(int s)
        {
            int ex = Math.Max(0, PillW - s);
            int ey = D(PillHDip - PillOverlapDip);
            return new Point(D(PadDip) + ex, D(PadDip) + ey);
        }

        private Size WindowSizeFor(int s)
        {
            Point o = CircleOffset(s);
            return new Size(s + 2 * o.X, s + 2 * o.Y);
        }

        private Rectangle CircleRectLocal { get { Point o = CircleOffset(CircleSize); return new Rectangle(o.X, o.Y, CircleSize, CircleSize); } }

        private Rectangle PillRectLocal
        {
            get
            {
                Rectangle c = CircleRectLocal;
                int y = _pillBelow ? c.Bottom - D(PillOverlapDip) : c.Top - D(PillHDip) + D(PillOverlapDip);
                return new Rectangle(_pillOffsetX, y, PillW, D(PillHDip));
            }
        }

        // Place la barre pour qu'elle reste visible à l'écran (dessous si possible, sinon dessus).
        private void UpdatePillPlacement()
        {
            Rectangle c = CircleRectLocal;
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            int m = D(ScreenMarginDip);
            int circleBottomScreen = Top + c.Bottom;
            _pillBelow = circleBottomScreen - D(PillOverlapDip) + D(PillHDip) + m <= wa.Bottom;

            int centered = c.Left + (c.Width - PillW) / 2;
            int minX = Math.Min(c.Left, c.Right - PillW);
            int maxX = Math.Max(c.Left, c.Right - PillW);
            int x = centered;
            int screenLeft = Left + x;
            if (screenLeft < wa.Left + m) x += (wa.Left + m) - screenLeft;
            if (Left + x + PillW > wa.Right - m) x -= (Left + x + PillW) - (wa.Right - m);
            _pillOffsetX = Math.Max(minX, Math.Min(maxX, x));
        }

        // Disposition des boutons selon le mode (repos / enregistrement) + positions des séparateurs.
        private Dictionary<Btn, Rectangle> ButtonRects() { List<int> seps; return PillLayout(out seps); }

        private Dictionary<Btn, Rectangle> PillLayout(out List<int> separators)
        {
            var r = new Dictionary<Btn, Rectangle>();
            separators = new List<int>();
            Rectangle p = PillRectLocal;
            int b = D(BtnDip), gap = D(4), sep = D(21);
            int y = p.Top + (p.Height - b) / 2;
            if (!RecordingUi)
            {
                int x = p.Left + D(12);
                r[Btn.Record] = new Rectangle(x, y, b, b); x += b; separators.Add(x + sep / 2); x += sep;
                r[Btn.SizeS] = new Rectangle(x, y, b, b); x += b + gap;
                r[Btn.SizeM] = new Rectangle(x, y, b, b); x += b + gap;
                r[Btn.SizeL] = new Rectangle(x, y, b, b); x += b; separators.Add(x + sep / 2); x += sep;
                r[Btn.Mirror] = new Rectangle(x, y, b, b); x += b + gap;
                r[Btn.Close] = new Rectangle(x, y, b, b);
            }
            else
            {
                int sw = D(StopWDip);
                int content = sw + sep + b + gap + b;
                int x = p.Left + (p.Width - content) / 2;
                r[Btn.Stop] = new Rectangle(x, y, sw, b); x += sw; separators.Add(x + sep / 2); x += sep;
                r[Btn.Pause] = new Rectangle(x, y, b, b); x += b + gap;
                r[Btn.Discard] = new Rectangle(x, y, b, b);
            }
            return r;
        }

        // ============================================================ fenêtre
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* tout est dessiné par UpdateLayeredWindow */ }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UpdatePillPlacement();
            Render();
            _uiTimer.Start();
            StartCamera();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_rec == RecState.Recording || _rec == RecState.Finishing)
            {
                // On termine proprement le MP4 avant de quitter
                e.Cancel = true;
                _closeAfterSave = true;
                if (_rec == RecState.Recording) StopRecording(false);
                return;
            }
            _uiTimer.Stop();
            SaveSettings();
            StopCamera();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOVING)
            {
                // Contraint la bulle à l'écran + effet aimant sur les bords
                var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Point p = ClampWindowLocation(new Point(r.Left, r.Top), Size, true);
                r.Right = p.X + (r.Right - r.Left); r.Bottom = p.Y + (r.Bottom - r.Top);
                r.Left = p.X; r.Top = p.Y;
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = new IntPtr(1);
                return;
            }
            if (m.Msg == Native.WM_EXITSIZEMOVE)
            {
                UpdatePillPlacement();
                Render();
                SaveSettings();
            }
            base.WndProc(ref m);
        }

        // Retourne la position de fenêtre corrigée pour que le cercle reste dans la zone de travail
        // de l'écran concerné (plusieurs écrans gérés), avec aimantation sur les bords.
        private Point ClampWindowLocation(Point loc, Size windowSize, bool useCursorScreen)
        {
            int s = windowSize.Width - 2 * CircleOffsetFromWindow(windowSize).X;
            Point o = CircleOffsetFromWindow(windowSize);
            Rectangle circle = new Rectangle(loc.X + o.X, loc.Y + o.Y, s, s);
            Screen scr = _lockedScreen ?? (useCursorScreen ? Screen.FromPoint(Cursor.Position) : Screen.FromRectangle(circle));
            Rectangle wa = scr.WorkingArea;
            int m = D(ScreenMarginDip), mag = D(MagnetDip);

            int minX = wa.Left + m, maxX = wa.Right - m - s;
            int minY = wa.Top + m, maxY = wa.Bottom - m - s;
            int x = circle.X, y = circle.Y;
            if (x - minX < mag) x = minX;
            if (maxX - x < mag) x = maxX;
            if (y - minY < mag) y = minY;
            if (maxY - y < mag) y = maxY;
            x = Math.Max(minX, Math.Min(maxX, x));
            y = Math.Max(minY, Math.Min(maxY, y));
            return new Point(x - o.X, y - o.Y);
        }

        private Point CircleOffsetFromWindow(Size windowSize)
        {
            // le cercle est centré dans la fenêtre
            for (int i = 0; i < SizesDip.Length; i++)
            {
                int s = D(SizesDip[i]);
                if (WindowSizeFor(s) == windowSize) return CircleOffset(s);
            }
            return CircleOffset(CircleSize);
        }

        private void SetSize(int index)
        {
            if (index == _sizeIndex) return;
            Rectangle oldCircle = new Rectangle(Left + CircleRectLocal.X, Top + CircleRectLocal.Y, CircleSize, CircleSize);
            Point center = new Point(oldCircle.X + oldCircle.Width / 2, oldCircle.Y + oldCircle.Height / 2);
            _sizeIndex = index;
            int s = CircleSize;
            Size ws = WindowSizeFor(s);
            Point o = CircleOffset(s);
            Point loc = new Point(center.X - s / 2 - o.X, center.Y - s / 2 - o.Y);
            loc = ClampWindowLocation(loc, ws, false);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, loc.X, loc.Y, ws.Width, ws.Height, Native.SWP_NOACTIVATE);
            UpdatePillPlacement();
            Render();
            SaveSettings();
        }

        // ============================================================ souris
        private bool IsInCircle(Point p)
        {
            Rectangle c = CircleRectLocal;
            double dx = p.X - (c.X + c.Width / 2.0), dy = p.Y - (c.Y + c.Height / 2.0);
            return dx * dx + dy * dy <= (c.Width / 2.0) * (c.Width / 2.0);
        }

        private bool IsInPill(Point p) { return _menuAlpha > 0.05f && PillRectLocal.Contains(p); }

        private Btn HitButton(Point p)
        {
            if (!IsInPill(p)) return Btn.None;
            foreach (var kv in ButtonRects()) if (kv.Value.Contains(p)) return kv.Key;
            return Btn.None;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Btn hot = HitButton(e.Location);
            if (hot != _hotBtn) { _hotBtn = hot; Render(); }
            Cursor = hot != Btn.None ? Cursors.Hand : Cursors.SizeAll;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Btn b = HitButton(e.Location);
            if (b != Btn.None) { _pressedBtn = b; Render(); return; }
            if (IsInCircle(e.Location) || IsInPill(e.Location))
            {
                // Déplacement natif de la fenêtre (fluide, gère le multi-écran)
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { Native.SetForegroundWindow(Handle); _menu.Show(this, e.Location); return; }
            Btn b = HitButton(e.Location);
            Btn pressed = _pressedBtn;
            _pressedBtn = Btn.None;
            if (b != Btn.None && b == pressed) Execute(b);
            else Render();
        }

        private void Execute(Btn b)
        {
            switch (b)
            {
                case Btn.SizeS: SetSize(0); break;
                case Btn.SizeM: SetSize(1); break;
                case Btn.SizeL: SetSize(2); break;
                case Btn.Mirror: _mirror = !_mirror; SaveSettings(); Render(); break;
                case Btn.Close: Close(); break;
                case Btn.Record: StartCountdown(); break;
                case Btn.Stop:
                    if (_rec == RecState.Countdown) CancelCountdown();
                    else if (_rec == RecState.Recording) StopRecording(false);
                    break;
                case Btn.Pause:
                    if (_rec != RecState.Recording) break;
                    if (_recorder.Paused) _recorder.Resume(); else _recorder.Pause();
                    Render();
                    break;
                case Btn.Discard: DiscardRecording(); break;
            }
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            // Survol : on vérifie la position réelle du curseur (plus fiable que MouseLeave sur une fenêtre layered)
            Point p = PointToClient(Cursor.Position);
            bool inside = IsInCircle(p) || (_hover && IsInPill(p)) || (_menu != null && _menu.Visible);
            if (!inside && _hotBtn != Btn.None) { _hotBtn = Btn.None; Render(); }
            _hover = inside;

            // Pendant l'enregistrement, la barre reste affichée si elle n'apparaît pas dans la vidéo.
            float target = (_hover || _rec == RecState.Countdown || (RecordingUi && _excluded)) ? 1f : 0f;

            if (_rec == RecState.Countdown)
            {
                int left = CountdownSeconds - (int)(DateTime.Now - _countdownStart).TotalSeconds;
                if (left <= 0) BeginRecording();
                else if (left != _shownSecond) { _shownSecond = left; Render(); }
            }
            else if (_rec == RecState.Recording && _recorder != null)
            {
                int sec = (int)(_recorder.Position / 10000000L);
                int blink = _recorder.Paused ? (int)(DateTime.Now.Ticks / 5000000L % 2) : 0;
                if (sec * 2 + blink != _shownSecond) { _shownSecond = sec * 2 + blink; if (_frame == null) Render(); }
            }
            if (Math.Abs(_menuAlpha - target) > 0.001f)
            {
                if (_menuAlpha == 0f && target > 0f) UpdatePillPlacement();
                _menuAlpha += (target - _menuAlpha) * 0.35f;
                if (Math.Abs(_menuAlpha - target) < 0.02f) _menuAlpha = target;
                Render();
            }

            // Réaffirme le "toujours au premier plan" (ex : diaporama PowerPoint lancé après LUM)
            if (++_topmostTick >= 60)
            {
                _topmostTick = 0;
                if (Control.MouseButtons == MouseButtons.None && !(_menu != null && _menu.Visible))
                    Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        // ============================================================ menu clic droit
        private void BuildContextMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Opening += delegate { FillContextMenu(); };
            FillContextMenu();
        }

        private void FillContextMenu()
        {
            _menu.Items.Clear();
            string[] labels = { "Petit", "Moyen", "Grand" };
            var size = new ToolStripMenuItem("Taille");
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(labels[i]);
                it.Checked = i == _sizeIndex;
                it.Click += delegate { SetSize(idx); };
                size.DropDownItems.Add(it);
            }
            _menu.Items.Add(size);

            var cams = new ToolStripMenuItem("Caméra");
            List<string> names;
            try { names = CameraCapture.ListCameras(); } catch { names = new List<string>(); }
            if (names.Count == 0) cams.DropDownItems.Add(new ToolStripMenuItem("(aucune caméra)") { Enabled = false });
            for (int i = 0; i < names.Count; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(names[i]);
                it.Checked = i == _cameraIndex || (names.Count > 0 && _cameraIndex >= names.Count && i == 0);
                it.Click += delegate { _cameraIndex = idx; SaveSettings(); RestartCamera(); };
                cams.DropDownItems.Add(it);
            }
            _menu.Items.Add(cams);

            var mirror = new ToolStripMenuItem("Effet miroir");
            mirror.Checked = _mirror;
            mirror.Click += delegate { _mirror = !_mirror; SaveSettings(); Render(); };
            _menu.Items.Add(mirror);

            _menu.Items.Add(new ToolStripSeparator());
            var rec = new ToolStripMenuItem(RecordingUi ? "Arrêter l'enregistrement" : "Enregistrer l'écran");
            rec.Enabled = _rec != RecState.Finishing;
            rec.Click += delegate
            {
                if (_rec == RecState.Idle) StartCountdown();
                else if (_rec == RecState.Countdown) CancelCountdown();
                else if (_rec == RecState.Recording) StopRecording(false);
            };
            _menu.Items.Add(rec);

            var mic = new ToolStripMenuItem("Micro");
            mic.Enabled = _rec == RecState.Idle;
            var micNone = new ToolStripMenuItem("Pas de son");
            micNone.Checked = _mic == ScreenRecorder.MicNone;
            micNone.Click += delegate { _mic = ScreenRecorder.MicNone; SaveSettings(); };
            mic.DropDownItems.Add(micNone);
            var micDef = new ToolStripMenuItem("Micro par défaut de Windows");
            micDef.Checked = _mic == ScreenRecorder.MicDefault;
            micDef.Click += delegate { _mic = ScreenRecorder.MicDefault; SaveSettings(); };
            mic.DropDownItems.Add(micDef);
            string[] mics;
            try { mics = WaveIn.ListDevices(); } catch { mics = new string[0]; }
            if (mics.Length > 0) mic.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < mics.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(mics[i]);
                it.Checked = _mic == i;
                it.Click += delegate { _mic = idx; SaveSettings(); };
                mic.DropDownItems.Add(it);
            }
            _menu.Items.Add(mic);

            var folder = new ToolStripMenuItem("Ouvrir le dossier des vidéos");
            folder.Click += delegate
            {
                try { Directory.CreateDirectory(VideoFolder); System.Diagnostics.Process.Start("explorer.exe", "\"" + VideoFolder + "\""); } catch { }
            };
            _menu.Items.Add(folder);

            _menu.Items.Add(new ToolStripSeparator());
            var quit = new ToolStripMenuItem("Fermer LUM");
            quit.Click += delegate { Close(); };
            _menu.Items.Add(quit);
        }

        // ============================================================ caméra
        private void StartCamera()
        {
            _status = "Démarrage de la caméra…";
            _capture = new CameraCapture(_cameraIndex);
            _capture.FrameReady += OnFrame;
            _capture.Error += OnCameraError;
            _capture.Start();
        }

        private void StopCamera()
        {
            if (_capture == null) return;
            _capture.FrameReady -= OnFrame;
            _capture.Error -= OnCameraError;
            _capture.Dispose();
            _capture = null;
        }

        private void RestartCamera()
        {
            StopCamera();
            lock (_frameLock) { if (_pendingFrame != null) { _pendingFrame.Dispose(); _pendingFrame = null; } }
            if (_frame != null) { _frame.Dispose(); _frame = null; }
            StartCamera();
            Render();
        }

        private void OnFrame(Bitmap bmp)
        {
            lock (_frameLock)
            {
                if (_pendingFrame != null) _pendingFrame.Dispose();
                _pendingFrame = bmp;
            }
            if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            {
                try { BeginInvoke(new Action(OnFrameOnUi)); }
                catch { Interlocked.Exchange(ref _renderQueued, 0); }
            }
        }

        private void OnFrameOnUi()
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            Bitmap next;
            lock (_frameLock) { next = _pendingFrame; _pendingFrame = null; }
            if (next == null) return;
            if (_frame != null) _frame.Dispose();
            _frame = next;
            _status = null;
            Render();
        }

        private void OnCameraError(string msg)
        {
            try { BeginInvoke(new Action(delegate { _status = msg; Render(); })); } catch { }
        }

        // ============================================================ rendu
        private void Render()
        {
            if (!IsHandleCreated) return;
            Size ws = Size;
            using (var bmp = new Bitmap(ws.Width, ws.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    DrawCircle(g);
                    if (_rec == RecState.Recording && _excluded) { g.Flush(); StoreOverlay(bmp); }
                    if (_menuAlpha > 0.01f) DrawPill(g);
                }
                Present(bmp);
            }
        }

        private void DrawCircle(Graphics g)
        {
            Rectangle c = CircleRectLocal;

            // Ombre douce
            for (int i = 6; i >= 1; i--)
            {
                int grow = D(i * 2) / 2;
                using (var b = new SolidBrush(Color.FromArgb(7, 0, 0, 0)))
                    g.FillEllipse(b, c.X - grow, c.Y - grow + D(2), c.Width + 2 * grow, c.Height + 2 * grow);
            }

            if (_frame != null)
            {
                int s = c.Width;
                using (var square = new Bitmap(s, s, PixelFormat.Format32bppPArgb))
                {
                    using (var sg = Graphics.FromImage(square))
                    {
                        sg.InterpolationMode = InterpolationMode.Bilinear;
                        sg.PixelOffsetMode = PixelOffsetMode.Half;
                        int side = Math.Min(_frame.Width, _frame.Height);
                        var src = new Rectangle((_frame.Width - side) / 2, (_frame.Height - side) / 2, side, side);
                        if (_mirror) { sg.TranslateTransform(s, 0); sg.ScaleTransform(-1, 1); }
                        sg.DrawImage(_frame, new Rectangle(0, 0, s, s), src, GraphicsUnit.Pixel);
                    }
                    using (var tb = new TextureBrush(square, WrapMode.Clamp))
                    {
                        tb.TranslateTransform(c.X, c.Y);
                        g.FillEllipse(tb, c);
                    }
                }
            }
            else
            {
                using (var b = new LinearGradientBrush(c, Color.FromArgb(255, 58, 60, 68), Color.FromArgb(255, 30, 31, 36), 90f))
                    g.FillEllipse(b, c);
                DrawCameraIcon(g, c);
                if (!string.IsNullOrEmpty(_status))
                {
                    using (var f = new Font("Segoe UI", Math.Max(7f, c.Width / 22f), FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var br = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
                    {
                        var tr = new RectangleF(c.X + c.Width * 0.15f, c.Y + c.Height * 0.62f, c.Width * 0.7f, c.Height * 0.3f);
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g.DrawString(_status, f, br, tr, sf);
                    }
                }
            }

            if (_rec == RecState.Countdown || _rec == RecState.Finishing)
            {
                using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillEllipse(b, c);
                string txt = _rec == RecState.Countdown ? Math.Max(1, _shownSecond).ToString() : "Finalisation…";
                float fs = _rec == RecState.Countdown ? c.Width * 0.42f : Math.Max(8f, c.Width / 14f);
                using (var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(Color.White))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.DrawString(txt, f, br, new RectangleF(c.X, c.Y, c.Width, c.Height), sf);
                }
            }

            // Liseré blanc
            float w = Math.Max(2f, D(3));
            using (var pen = new Pen(Color.FromArgb(235, 255, 255, 255), w))
                g.DrawEllipse(pen, c.X + w / 2, c.Y + w / 2, c.Width - w, c.Height - w);
        }

        private void DrawCameraIcon(Graphics g, Rectangle c)
        {
            float u = c.Width / 10f;
            float cx = c.X + c.Width / 2f, cy = c.Y + c.Height * 0.45f;
            using (var b = new SolidBrush(Color.FromArgb(160, 255, 255, 255)))
            using (var path = RoundRect(new RectangleF(cx - 1.6f * u, cy - 0.9f * u, 2.3f * u, 1.8f * u), 0.35f * u))
            {
                g.FillPath(b, path);
                g.FillPolygon(b, new[] { new PointF(cx + 0.9f * u, cy - 0.2f * u), new PointF(cx + 1.6f * u, cy - 0.7f * u),
                                         new PointF(cx + 1.6f * u, cy + 0.7f * u), new PointF(cx + 0.9f * u, cy + 0.2f * u) });
            }
        }

        private void DrawPill(Graphics g)
        {
            int a = (int)(255 * _menuAlpha);
            Rectangle p = PillRectLocal;
            float radius = p.Height / 2f;

            for (int i = 5; i >= 1; i--)
            {
                int grow = D(i * 2) / 2;
                using (var path = RoundRect(new RectangleF(p.X - grow, p.Y - grow + D(2), p.Width + 2 * grow, p.Height + 2 * grow), radius + grow))
                using (var b = new SolidBrush(Color.FromArgb((int)(9 * _menuAlpha), 0, 0, 0)))
                    g.FillPath(b, path);
            }
            using (var path = RoundRect(p, radius))
            using (var b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                g.FillPath(b, path);

            List<int> seps;
            var rects = PillLayout(out seps);
            foreach (var kv in rects)
            {
                Rectangle r = kv.Value;
                bool hot = kv.Key == _hotBtn;
                bool active = (kv.Key == Btn.SizeS && _sizeIndex == 0) || (kv.Key == Btn.SizeM && _sizeIndex == 1)
                           || (kv.Key == Btn.SizeL && _sizeIndex == 2) || (kv.Key == Btn.Mirror && _mirror);
                if (hot || active)
                {
                    bool danger = kv.Key == Btn.Close || kv.Key == Btn.Record || kv.Key == Btn.Discard || kv.Key == Btn.Stop;
                    Color bg = danger && hot ? Color.FromArgb(a, 253, 228, 228)
                             : active ? Color.FromArgb(a, 232, 234, 240) : Color.FromArgb(a, 242, 243, 246);
                    using (var path = RoundRect(r, D(8)))
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                }
                DrawIcon(g, kv.Key, r, a, hot, active);
            }

            using (var pen = new Pen(Color.FromArgb((int)(a * 0.6f), 210, 212, 218), Math.Max(1f, _k)))
                foreach (int sx in seps) g.DrawLine(pen, sx, p.Top + D(11), sx, p.Bottom - D(11));
        }

        private void DrawIcon(Graphics g, Btn b, Rectangle r, int a, bool hot, bool active)
        {
            Color ink = Color.FromArgb(a, 38, 40, 48);
            Color dim = Color.FromArgb(a, 120, 124, 135);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            float stroke = Math.Max(1.5f, 1.8f * _k);

            if (b == Btn.SizeS || b == Btn.SizeM || b == Btn.SizeL)
            {
                float rad = (b == Btn.SizeS ? 3.5f : b == Btn.SizeM ? 5.5f : 7.5f) * _k;
                using (var br = new SolidBrush(active || hot ? ink : dim))
                    g.FillEllipse(br, cx - rad, cy - rad, 2 * rad, 2 * rad);
            }
            else if (b == Btn.Mirror)
            {
                float u = 3.2f * _k;
                using (var br = new SolidBrush(active || hot ? ink : dim))
                using (var pen = new Pen(active || hot ? ink : dim, Math.Max(1f, _k)))
                {
                    g.FillPolygon(br, new[] { new PointF(cx - 1.2f * u, cy - 1.5f * u), new PointF(cx - 1.2f * u, cy + 1.5f * u), new PointF(cx - 2.6f * u, cy + 1.5f * u) });
                    g.DrawPolygon(pen, new[] { new PointF(cx + 1.2f * u, cy - 1.5f * u), new PointF(cx + 1.2f * u, cy + 1.5f * u), new PointF(cx + 2.6f * u, cy + 1.5f * u) });
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawLine(pen, cx, cy - 2f * u, cx, cy + 2f * u);
                }
            }
            else if (b == Btn.Record)
            {
                float rad = 7f * _k;
                using (var br = new SolidBrush(Color.FromArgb(a, 229, 57, 53)))
                    g.FillEllipse(br, cx - rad, cy - rad, 2 * rad, 2 * rad);
            }
            else if (b == Btn.Stop)
            {
                bool paused = _recorder != null && _recorder.Paused;
                float sq = 11f * _k;
                float sx0 = r.X + D(12);
                Color red = Color.FromArgb(a, 229, 57, 53);
                using (var path = RoundRect(new RectangleF(sx0, cy - sq / 2, sq, sq), 2.5f * _k))
                using (var br = new SolidBrush(_rec == RecState.Recording ? red : ink)) g.FillPath(br, path);
                long pos = _recorder != null ? _recorder.Position : 0;
                int total = (int)(pos / 10000000L);
                string t = (total / 60) + ":" + (total % 60).ToString("00");
                bool blinkOff = paused && (DateTime.Now.Ticks / 5000000L % 2) == 1;
                using (var f = new Font("Segoe UI", 14f * _k, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(blinkOff ? Color.FromArgb(a / 3, 38, 40, 48) : ink))
                using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(t, f, br, new RectangleF(sx0 + sq + D(8), r.Y, r.Right - sx0 - sq - D(8), r.Height), sf);
                }
            }
            else if (b == Btn.Pause)
            {
                bool paused = _recorder != null && _recorder.Paused;
                using (var br = new SolidBrush(ink))
                {
                    if (paused) // bouton "reprendre" : pastille rouge
                    {
                        float rad = 6.5f * _k;
                        using (var red = new SolidBrush(Color.FromArgb(a, 229, 57, 53))) g.FillEllipse(red, cx - rad, cy - rad, 2 * rad, 2 * rad);
                    }
                    else
                    {
                        float bw = 3.2f * _k, bh = 12f * _k, gap = 2.6f * _k;
                        using (var p1 = RoundRect(new RectangleF(cx - gap / 2 - bw, cy - bh / 2, bw, bh), 1f * _k)) g.FillPath(br, p1);
                        using (var p2 = RoundRect(new RectangleF(cx + gap / 2, cy - bh / 2, bw, bh), 1f * _k)) g.FillPath(br, p2);
                    }
                }
            }
            else if (b == Btn.Discard)
            {
                Color c = hot ? Color.FromArgb(a, 210, 45, 45) : ink;
                float u = _k, lw = Math.Max(1.2f, 1.5f * _k);
                using (var pen = new Pen(c, lw))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLine(pen, cx - 6 * u, cy - 4.5f * u, cx + 6 * u, cy - 4.5f * u);             // couvercle
                    g.DrawLine(pen, cx - 2 * u, cy - 6.5f * u, cx + 2 * u, cy - 6.5f * u);             // poignée
                    g.DrawPolygon(pen, new[] { new PointF(cx - 4.5f * u, cy - 4.5f * u), new PointF(cx - 3.8f * u, cy + 6.5f * u),
                                               new PointF(cx + 3.8f * u, cy + 6.5f * u), new PointF(cx + 4.5f * u, cy - 4.5f * u) });
                    g.DrawLine(pen, cx - 1.5f * u, cy - 1.5f * u, cx - 1.5f * u, cy + 3.8f * u);
                    g.DrawLine(pen, cx + 1.5f * u, cy - 1.5f * u, cx + 1.5f * u, cy + 3.8f * u);
                }
            }
            else if (b == Btn.Close)
            {
                float u = 5f * _k;
                using (var pen = new Pen(hot ? Color.FromArgb(a, 210, 45, 45) : ink, stroke))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, cx - u, cy - u, cx + u, cy + u);
                    g.DrawLine(pen, cx + u, cy - u, cx - u, cy + u);
                }
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void Present(Bitmap bmp)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
                var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new Native.POINT { x = 0, y = 0 };
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ============================================================ enregistrement (V2)
        private static string VideoFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LUM"); }
        }

        private void StartCountdown()
        {
            if (_rec != RecState.Idle) return;
            _rec = RecState.Countdown;
            _countdownStart = DateTime.Now;
            _shownSecond = CountdownSeconds;
            _lockedScreen = Screen.FromHandle(Handle);  // l'écran de la bulle = l'écran enregistré
            Render();
        }

        private void CancelCountdown()
        {
            _rec = RecState.Idle;
            _lockedScreen = null;
            Render();
        }

        private void BeginRecording()
        {
            string file = Path.Combine(VideoFolder, "LUM_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4");
            // Windows 10 2004+ : la bulle et sa barre sont invisibles pour la capture ; on y redessine
            // uniquement la bulle. Sinon la bulle est capturée telle quelle (barre visible au survol).
            _excluded = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            _rec = RecState.Recording;
            _shownSecond = -1;
            Render();
            var rec = new ScreenRecorder(Handle, file, _mic, _settings.RecordFlip, _excluded ? new Action<Graphics, Rectangle>(DrawOverlay) : null);
            try
            {
                rec.Start();
            }
            catch (Exception ex)
            {
                ResetAfterRecording();
                MessageBox.Show(this, "Impossible de démarrer l'enregistrement :\n" + ex.Message, "LUM",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _recorder = rec;
            Render();
        }

        private void StopRecording(bool discard)
        {
            if (_rec != RecState.Recording || _recorder == null) return;
            _rec = RecState.Finishing;
            Render();
            _recorder.Stop(discard, delegate(string path, Exception error)
            {
                try { BeginInvoke(new Action(delegate { OnRecordingFinished(path, error); })); } catch { }
            });
        }

        private void DiscardRecording()
        {
            if (_rec == RecState.Countdown) { CancelCountdown(); return; }
            if (_rec != RecState.Recording) return;
            bool wasPaused = _recorder.Paused;
            _recorder.Pause();
            var answer = MessageBox.Show(this, "Supprimer cet enregistrement ?", "LUM",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) StopRecording(true);
            else if (!wasPaused) _recorder.Resume();
        }

        private void OnRecordingFinished(string path, Exception error)
        {
            bool noAudio = _recorder != null && !_recorder.HasAudio && _mic != ScreenRecorder.MicNone;
            ResetAfterRecording();
            if (error != null)
                MessageBox.Show(this, "L'enregistrement s'est arrêté sur une erreur :\n" + error.Message +
                    (path != null && File.Exists(path) ? "\n\nLa vidéo a peut-être été partiellement enregistrée :\n" + path : ""),
                    "LUM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (path != null && File.Exists(path))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
                if (noAudio && !_closeAfterSave)
                    MessageBox.Show(this, "Vidéo enregistrée sans son : aucun micro utilisable n'a été trouvé.", "LUM",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            if (_closeAfterSave) Close();
        }

        private void ResetAfterRecording()
        {
            if (_excluded) Native.SetWindowDisplayAffinity(Handle, 0);
            _excluded = false;
            _recorder = null;
            _rec = RecState.Idle;
            _lockedScreen = null;
            lock (_overlayLock) { if (_overlay != null) { _overlay.Dispose(); _overlay = null; } }
            Render();
        }

        // Copie de la bulle (sans la barre) que le thread d'enregistrement incruste dans la vidéo.
        private void StoreOverlay(Bitmap bmp)
        {
            Bitmap copy = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(copy)) g.DrawImageUnscaled(bmp, 0, 0);
            lock (_overlayLock)
            {
                if (_overlay != null) _overlay.Dispose();
                _overlay = copy;
            }
        }

        // Appelé depuis le thread d'enregistrement : windowRect = position réelle de la fenêtre dans l'image.
        private void DrawOverlay(Graphics g, Rectangle windowRect)
        {
            lock (_overlayLock)
            {
                if (_overlay == null) return;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.CompositingMode = CompositingMode.SourceOver;
                if (windowRect.Width == _overlay.Width && windowRect.Height == _overlay.Height)
                    g.DrawImageUnscaled(_overlay, windowRect.X, windowRect.Y);
                else
                    g.DrawImage(_overlay, windowRect); // écran avec un autre facteur d'échelle
            }
        }

        // ============================================================ réglages
        private void SaveSettings()
        {
            _settings.MicDevice = _mic;
            _settings.SizeIndex = _sizeIndex;
            _settings.Mirror = _mirror;
            _settings.CameraIndex = _cameraIndex;
            _settings.X = Left; _settings.Y = Top; _settings.HasPosition = true;
            _settings.Save();
        }
    }

    internal static class Native
    {
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
        public const int WM_MOVING = 0x0216, WM_EXITSIZEMOVE = 0x0232, WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        public const int ULW_ALPHA = 2;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    }
}
