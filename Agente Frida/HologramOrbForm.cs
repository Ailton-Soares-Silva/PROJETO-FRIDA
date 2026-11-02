// language: C# 5 (.NET Framework 4.8), file: HologramOrbForm.cs, target: Windows 10/11 x64
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FridaAgent
{
    public enum OrbActivityState
    {
        Hidden = 0,
        Listening = 1,
        Thinking = 2,
        Executing = 3,
        Speaking = 4,
        FollowUp = 5
    }

    internal static class OrbNative
    {
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;

        public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int ULW_ALPHA = 0x00000002;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern bool DeleteObject(IntPtr hObject);
    }

    public class HologramOrbForm : Form
    {
        private struct Particle3D
        {
            public float nx, ny, nz;
            public float shellRadius;
            public float phase;
            public float size;
        }

        private struct ProjectedNode
        {
            public float sx, sy, z, scale;
        }

        private const int CanvasW = 370;
        private const int CanvasH = 344;
        private const float CenterX = 185f;
        private const float CenterY = 172f;

        private readonly FridaSettings _settings;
        private readonly string _configPath;
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly Particle3D[] _particles;

        private OrbActivityState _state = OrbActivityState.Hidden;
        private string _statusTitle = "FRIDA";
        private string _subtitleText = "";

        private float _visibility = 0f;
        private float _targetVisibility = 0f;
        private float _time = 0f;
        private float _smoothEnergy = 0f;
        private readonly float[] _liveSpectrum = new float[16];
        private readonly float[] _targetSpectrum = new float[16];
        private readonly object _specLock = new object();

        private bool _dragging = false;
        private Point _dragCursorStart;
        private Point _dragFormStart;
        private int _posX;
        private int _posY;

        public HologramOrbForm(FridaSettings settings, string configPath)
        {
            _settings = settings;
            _configPath = configPath;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            Screen primary = Screen.PrimaryScreen;
            if (_settings.orb_x >= 0 && _settings.orb_y >= 0)
            {
                _posX = _settings.orb_x;
                _posY = _settings.orb_y;
            }
            else
            {
                _posX = primary.WorkingArea.Right - CanvasW - 26;
                _posY = primary.WorkingArea.Top + 34;
            }

            Bounds = new Rectangle(_posX, _posY, CanvasW, CanvasH);

            _particles = BuildNeuralSphereParticles(185);

            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 16; // ~60 FPS volumetric projection
            _animTimer.Tick += OnAnimationTick;

            MouseDown += OnOrbMouseDown;
            MouseMove += OnOrbMouseMove;
            MouseUp += OnOrbMouseUp;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= OrbNative.WS_EX_TOPMOST
                            | OrbNative.WS_EX_LAYERED
                            | OrbNative.WS_EX_NOACTIVATE
                            | OrbNative.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == OrbNative.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(OrbNative.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref m);
        }

        private void OnOrbMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragCursorStart = Cursor.Position;
                _dragFormStart = new Point(_posX, _posY);
            }
        }

        private void OnOrbMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                Point cur = Cursor.Position;
                _posX = _dragFormStart.X + (cur.X - _dragCursorStart.X);
                _posY = _dragFormStart.Y + (cur.Y - _dragCursorStart.Y);
            }
        }

        private void OnOrbMouseUp(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                _settings.orb_x = _posX;
                _settings.orb_y = _posY;
                FridaSettings.Save(_settings, _configPath);
            }
        }

        public void SetActivityState(OrbActivityState state, string statusTitle, string subtitle)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(delegate { SetActivityState(state, statusTitle, subtitle); }));
                return;
            }

            _state = state;
            if (!string.IsNullOrEmpty(statusTitle)) _statusTitle = statusTitle;
            if (subtitle != null) _subtitleText = subtitle;

            if (state == OrbActivityState.Hidden)
            {
                _targetVisibility = 0f;
            }
            else
            {
                bool wasHidden = (_visibility <= 0.02f);
                _targetVisibility = 1f;
                OrbNative.ShowWindow(Handle, OrbNative.SW_SHOWNOACTIVATE);
                OrbNative.SetWindowPos(
                    Handle, OrbNative.HWND_TOPMOST, 0, 0, 0, 0,
                    OrbNative.SWP_NOMOVE | OrbNative.SWP_NOSIZE | OrbNative.SWP_NOACTIVATE);
                if (wasHidden)
                {
                    _visibility = 0.16f;
                    RenderVolumetricHologram();
                }
                if (!_animTimer.Enabled)
                {
                    _animTimer.Start();
                }
            }
        }

        public void PushAudioSpectrum(float rmsNormalized, float[] bands16)
        {
            lock (_specLock)
            {
                for (int i = 0; i < 16; i++)
                {
                    float v = (bands16 != null && i < bands16.Length) ? bands16[i] : rmsNormalized;
                    if (v < 0f) v = 0f;
                    if (v > 1.4f) v = 1.4f;
                    _targetSpectrum[i] = v;
                }
            }
        }

        private static Particle3D[] BuildNeuralSphereParticles(int count)
        {
            Particle3D[] arr = new Particle3D[count];
            Random rng = new Random(6801);
            float goldenAngle = (float)(Math.PI * (3.0 - Math.Sqrt(5.0)));

            for (int i = 0; i < count; i++)
            {
                float y = 1f - (i / (float)(count - 1)) * 2f;
                float radiusAtY = (float)Math.Sqrt(Math.Max(0f, 1f - y * y));
                float theta = goldenAngle * i;

                Particle3D p = new Particle3D();
                p.nx = (float)Math.Cos(theta) * radiusAtY;
                p.ny = y;
                p.nz = (float)Math.Sin(theta) * radiusAtY;
                p.shellRadius = (i % 3 == 0) ? 62f : ((i % 5 == 0) ? 118f : 94f);
                p.phase = (float)(rng.NextDouble() * Math.PI * 2.0);
                p.size = 1.6f + (float)(rng.NextDouble() * 2.0);
                arr[i] = p;
            }
            return arr;
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;

            // Smooth appear / vanish interpolation
            float visStep = (_targetVisibility > _visibility) ? 0.085f : 0.055f;
            if (Math.Abs(_targetVisibility - _visibility) <= visStep)
            {
                _visibility = _targetVisibility;
            }
            else
            {
                _visibility += (_targetVisibility > _visibility) ? visStep : -visStep;
            }

            if (_visibility <= 0.005f && _targetVisibility <= 0.005f)
            {
                _animTimer.Stop();
                using (Bitmap blank = new Bitmap(2, 2, PixelFormat.Format32bppArgb))
                {
                    CommitFrame(blank);
                }
                OrbNative.ShowWindow(Handle, OrbNative.SW_HIDE);
                return;
            }

            // Smooth spectrum bands & compute total energy
            float sumEnergy = 0f;
            lock (_specLock)
            {
                for (int i = 0; i < 16; i++)
                {
                    float target = _targetSpectrum[i];
                    // Add organic procedural neural motion when thinking/executing
                    if (_state == OrbActivityState.Thinking || _state == OrbActivityState.Executing)
                    {
                        target = Math.Max(target, 0.28f + 0.24f * (float)Math.Sin(_time * 6.5f + i * 0.7f));
                    }
                    else if (_state == OrbActivityState.FollowUp)
                    {
                        target = Math.Max(target, 0.08f + 0.05f * (float)Math.Sin(_time * 2.2f + i * 0.5f));
                    }

                    float lerp = (target > _liveSpectrum[i]) ? 0.42f : 0.18f;
                    _liveSpectrum[i] += (target - _liveSpectrum[i]) * lerp;
                    _targetSpectrum[i] *= 0.88f; // Natural decay between audio frames
                    sumEnergy += _liveSpectrum[i];
                }
            }

            float avgEnergy = sumEnergy / 16f;
            _smoothEnergy += (avgEnergy - _smoothEnergy) * 0.30f;

            float speedMult = 1.0f + _smoothEnergy * 2.1f;
            if (_state == OrbActivityState.Thinking || _state == OrbActivityState.Executing)
            {
                speedMult = 2.35f;
            }
            _time += 0.018f * speedMult;

            RenderVolumetricHologram();
        }

        private void RenderVolumetricHologram()
        {
            using (Bitmap surface = new Bitmap(CanvasW, CanvasH, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(surface))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    float scaleEase = 0.45f + 0.55f * (float)Math.Sin(_visibility * Math.PI * 0.5);
                    float pulseScale = scaleEase * (1.0f + _smoothEnergy * 0.18f);
                    float masterAlpha = _visibility;

                    // 1. Volumetric Deep-Cobalt / Cyan Atmospheric Glow
                    DrawAtmosphericGlow(g, CenterX, CenterY, 152f * pulseScale, masterAlpha, _smoothEnergy);

                    // 2. 3D Gyroscopic Architectural Wireframe Shells (5 counter-rotating orbital planes)
                    DrawGyroShell(g, 126f * pulseScale, _time * 0.75f, 0.55f, _time * 0.35f, 44, true, masterAlpha);
                    DrawGyroShell(g, 108f * pulseScale, -_time * 0.95f, 1.18f, -_time * 0.50f, 36, false, masterAlpha);
                    DrawGyroShell(g, 88f * pulseScale, _time * 1.25f, -0.78f, _time * 0.85f, 32, true, masterAlpha);
                    DrawGyroShell(g, 66f * pulseScale, -_time * 1.65f, 0.35f, -_time * 1.10f, 24, false, masterAlpha);

                    // 3. Signature JARVIS/FRIDAY Segmented Holographic Ladder-Tracks (Image 1 audio-reactive ribs!)
                    DrawHolographicLadderTrack(g, 114f * pulseScale, 132f * pulseScale, _time * 0.62f, 0.42f, 0.25f, masterAlpha, 0);
                    DrawHolographicLadderTrack(g, 92f * pulseScale, 110f * pulseScale, -_time * 0.84f, -0.95f, 0.72f, masterAlpha, 5);
                    DrawHolographicLadderTrack(g, 72f * pulseScale, 86f * pulseScale, _time * 1.15f, 1.32f, -0.45f, masterAlpha, 10);

                    // 4. 3D Swirling Neural Particles & Connecting Synaptic Filaments
                    DrawNeuralParticleCloud(g, pulseScale, masterAlpha);

                    // 5. Inner Rotating Singularity C-Rings & Plasma Core (Center of Image 1)
                    DrawInnerSingularityCore(g, pulseScale, masterAlpha);
                }

                CommitFrame(surface);
            }
        }

        private void DrawAtmosphericGlow(Graphics g, float cx, float cy, float radius, float alpha, float energy)
        {
            if (radius < 10f) return;
            RectangleF rect = new RectangleF(cx - radius, cy - radius, radius * 2f, radius * 2f);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(rect);
                using (PathGradientBrush pgb = new PathGradientBrush(gp))
                {
                    int coreA = ClampByte((95 + energy * 95f) * alpha);
                    pgb.CenterPoint = new PointF(cx, cy);
                    pgb.CenterColor = Color.FromArgb(coreA, 0, 155, 255);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 35, 110) };
                    g.FillPath(pgb, gp);
                }
            }
        }

        private void DrawGyroShell(Graphics g, float radius, float rotX, float rotY, float rotZ, int segments, bool brokenArcs, float alpha)
        {
            PointF prev = PointF.Empty;
            float prevZ = 0f;
            bool hasPrev = false;

            for (int i = 0; i <= segments; i++)
            {
                if (brokenArcs && ((i / 5) % 3 == 1))
                {
                    hasPrev = false;
                    continue;
                }

                float ang = (float)(i * 2.0 * Math.PI / segments);
                int bandIdx = i % 16;
                float waveR = radius * (1.0f + _liveSpectrum[bandIdx] * 0.14f * (float)Math.Sin(ang * 4f + _time * 5f));

                float x = (float)Math.Cos(ang) * waveR;
                float y = (float)Math.Sin(ang) * waveR;
                float z = 0f;

                Rotate3D(ref x, ref y, ref z, rotX, rotY, rotZ);

                float fov = 360f;
                float persp = fov / (fov + z + 180f);
                PointF cur = new PointF(CenterX + x * persp, CenterY + y * persp);

                if (hasPrev)
                {
                    float avgZ = (prevZ + z) * 0.5f;
                    bool front = avgZ >= 0f;
                    int a = ClampByte((front ? 185f : 65f) * alpha);
                    Color col = front
                        ? Color.FromArgb(a, 20, 215, 255)
                        : Color.FromArgb(a, 0, 95, 220);
                    float w = front ? 1.55f : 0.9f;
                    using (Pen p = new Pen(col, w))
                    {
                        g.DrawLine(p, prev, cur);
                    }
                }

                prev = cur;
                prevZ = z;
                hasPrev = true;
            }
        }

        private void DrawHolographicLadderTrack(Graphics g, float rInner, float rOuterBase, float rotX, float rotY, float rotZ, float alpha, int bandOffset)
        {
            const int rungs = 40;
            PointF prevIn = PointF.Empty;
            PointF prevOut = PointF.Empty;
            bool hasArc = false;

            for (int i = 0; i <= rungs; i++)
            {
                // Segmented clusters like Image 1's holographic arc ladders
                int cluster = (i / 7) % 3;
                if (cluster == 2)
                {
                    hasArc = false;
                    continue;
                }

                float ang = (float)(i * 2.0 * Math.PI / rungs);
                int bIdx = (i + bandOffset) & 15;
                float dynamicOut = rOuterBase + (_liveSpectrum[bIdx] * 30f);

                float ix = (float)Math.Cos(ang) * rInner;
                float iy = (float)Math.Sin(ang) * rInner;
                float iz = 0f;

                float ox = (float)Math.Cos(ang) * dynamicOut;
                float oy = (float)Math.Sin(ang) * dynamicOut;
                float oz = 0f;

                Rotate3D(ref ix, ref iy, ref iz, rotX, rotY, rotZ);
                Rotate3D(ref ox, ref oy, ref oz, rotX, rotY, rotZ);

                float fov = 360f;
                float pIn = fov / (fov + iz + 180f);
                float pOut = fov / (fov + oz + 180f);

                PointF ptIn = new PointF(CenterX + ix * pIn, CenterY + iy * pIn);
                PointF ptOut = new PointF(CenterX + ox * pOut, CenterY + oy * pOut);

                bool front = oz >= -15f;
                int rungAlpha = ClampByte((front ? (140f + _liveSpectrum[bIdx] * 110f) : 52f) * alpha);
                Color rungColor = front
                    ? Color.FromArgb(rungAlpha, 90, 235, 255)
                    : Color.FromArgb(rungAlpha, 0, 110, 230);

                using (Pen rungPen = new Pen(rungColor, front ? 1.35f : 0.85f))
                {
                    g.DrawLine(rungPen, ptIn, ptOut);
                    if (hasArc)
                    {
                        g.DrawLine(rungPen, prevIn, ptIn);
                        g.DrawLine(rungPen, prevOut, ptOut);
                    }
                }

                prevIn = ptIn;
                prevOut = ptOut;
                hasArc = true;
            }
        }

        private void DrawNeuralParticleCloud(Graphics g, float pulseScale, float alpha)
        {
            int n = _particles.Length;
            ProjectedNode[] proj = new ProjectedNode[n];

            for (int i = 0; i < n; i++)
            {
                Particle3D p = _particles[i];
                int bIdx = i & 15;
                float harmonic = (float)(Math.Sin(_time * 4.2f + p.phase) * Math.Cos(_time * 2.7f - p.ny * 4f));
                float r = (p.shellRadius + _liveSpectrum[bIdx] * 26f * (0.5f + 0.5f * harmonic)) * pulseScale;

                float x = p.nx * r;
                float y = p.ny * r;
                float z = p.nz * r;

                Rotate3D(ref x, ref y, ref z, _time * 0.45f, _time * 0.82f, _time * 0.28f);

                float fov = 360f;
                float s = fov / (fov + z + 180f);
                ProjectedNode pn = new ProjectedNode();
                pn.sx = CenterX + x * s;
                pn.sy = CenterY + y * s;
                pn.z = z;
                pn.scale = s;
                proj[i] = pn;
            }

            // Connect nearby front-hemisphere particles with glowing cyan neural filaments
            for (int i = 0; i < n; i += 2)
            {
                if (proj[i].z < -25f) continue;
                int next = (i + 7) % n;
                float dx = proj[i].sx - proj[next].sx;
                float dy = proj[i].sy - proj[next].sy;
                float distSq = dx * dx + dy * dy;
                if (distSq < 1450f)
                {
                    int lineA = ClampByte((1f - distSq / 1450f) * 115f * alpha);
                    using (Pen synPen = new Pen(Color.FromArgb(lineA, 40, 210, 255), 0.9f))
                    {
                        g.DrawLine(synPen, proj[i].sx, proj[i].sy, proj[next].sx, proj[next].sy);
                    }
                }
            }

            // Render glowing blue/ice-white nodes
            for (int i = 0; i < n; i++)
            {
                ProjectedNode pn = proj[i];
                bool front = pn.z >= 0f;
                float sz = _particles[i].size * pn.scale * (1f + _smoothEnergy * 0.35f);
                int a = ClampByte((front ? 235f : 85f) * alpha);

                Color c = front
                    ? ((i % 4 == 0) ? Color.FromArgb(a, 215, 252, 255) : Color.FromArgb(a, 35, 218, 255))
                    : Color.FromArgb(a, 0, 108, 235);

                using (SolidBrush sb = new SolidBrush(c))
                {
                    g.FillEllipse(sb, pn.sx - sz * 0.5f, pn.sy - sz * 0.5f, sz, sz);
                }
            }
        }

        private void DrawInnerSingularityCore(Graphics g, float pulseScale, float alpha)
        {
            float coreR = (20f + _smoothEnergy * 16f) * pulseScale;

            // Inner glowing C-arcs (center detail of Image 1)
            for (int ring = 0; ring < 3; ring++)
            {
                float rr = coreR + 8f + ring * 9f;
                float startAng = (_time * (ring % 2 == 0 ? 155f : -190f) + ring * 70f) % 360f;
                float sweep = 205f + _liveSpectrum[ring * 3] * 65f;
                int arcA = ClampByte((220 - ring * 40) * alpha);
                using (Pen arcPen = new Pen(Color.FromArgb(arcA, 120, 242, 255), 2.2f - ring * 0.4f))
                {
                    g.DrawArc(arcPen, CenterX - rr, CenterY - rr, rr * 2f, rr * 2f, startAng, sweep);
                }
            }

            // Bright cyan-white plasma center
            RectangleF glowRect = new RectangleF(CenterX - coreR * 1.6f, CenterY - coreR * 1.6f, coreR * 3.2f, coreR * 3.2f);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(glowRect);
                using (PathGradientBrush pgb = new PathGradientBrush(gp))
                {
                    pgb.CenterColor = Color.FromArgb(ClampByte(245f * alpha), 220, 253, 255);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 195, 255) };
                    g.FillPath(pgb, gp);
                }
            }
        }

        private static void Rotate3D(ref float x, ref float y, ref float z, float rx, float ry, float rz)
        {
            // Rotate X
            float cosX = (float)Math.Cos(rx), sinX = (float)Math.Sin(rx);
            float y1 = y * cosX - z * sinX;
            float z1 = y * sinX + z * cosX;
            y = y1; z = z1;

            // Rotate Y
            float cosY = (float)Math.Cos(ry), sinY = (float)Math.Sin(ry);
            float x2 = x * cosY + z * sinY;
            float z2 = -x * sinY + z * cosY;
            x = x2; z = z2;

            // Rotate Z
            float cosZ = (float)Math.Cos(rz), sinZ = (float)Math.Sin(rz);
            float x3 = x * cosZ - y * sinZ;
            float y3 = x * sinZ + y * cosZ;
            x = x3; y = y3;
        }

        private static int ClampByte(float val)
        {
            if (val <= 0f) return 0;
            if (val >= 255f) return 255;
            return (int)val;
        }

        private void CommitFrame(Bitmap bmp)
        {
            IntPtr screenDc = OrbNative.GetDC(IntPtr.Zero);
            IntPtr memDc = OrbNative.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = OrbNative.SelectObject(memDc, hBitmap);

                OrbNative.SIZE size = new OrbNative.SIZE(bmp.Width, bmp.Height);
                OrbNative.POINT ptSrc = new OrbNative.POINT(0, 0);
                OrbNative.POINT ptDst = new OrbNative.POINT(_posX, _posY);

                OrbNative.BLENDFUNCTION blend = new OrbNative.BLENDFUNCTION();
                blend.BlendOp = OrbNative.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = OrbNative.AC_SRC_ALPHA;

                OrbNative.UpdateLayeredWindow(
                    Handle, screenDc, ref ptDst, ref size,
                    memDc, ref ptSrc, 0, ref blend, OrbNative.ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) OrbNative.SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) OrbNative.DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) OrbNative.DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) OrbNative.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}

