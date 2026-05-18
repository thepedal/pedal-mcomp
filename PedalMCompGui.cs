// PedalMCompGui.cs — Pedal MComp v1.1 GUI.
//
// Custom WPF GUI embedded in the parameter window. 4 columns side-by-side
// (one per band), each showing:
//   - Band label (L / LM / HM / H)
//   - Input-level meter (vertical bar, post-crossover pre-compressor)
//   - Gain-reduction meter (vertical bar)
//   - Transfer-curve display (input dB → output dB, drawn from the same
//     internal static SoftKneeGR the audio path uses per PedalComp §8)
//   - Threshold readout and ratio readout below the curve
//
// Base class: FrameworkElement, NOT UserControl. UserControl's default
// ControlTemplate is a Border + ContentPresenter that renders AFTER our
// OnRender output and paints the Background brush over our drawing — the
// effect is "blank coloured rectangle, custom content invisible." Switching
// to FrameworkElement removes the template; OnRender is then the only thing
// painting the visual surface. MeasureOverride is overridden explicitly
// because FrameworkElement's default returns Size(0,0).
//
// Threading model (PedalComp §7):
//   - Audio thread writes volatile fields on each BandCompressor:
//     MeterInDb (smoothed input level dB) and MeterGrDb (current GR dB).
//   - UI thread reads them via a DispatcherTimer at ~30 fps, then calls
//     InvalidateVisual() to repaint via OnRender.
//   - No locks needed: single-writer/single-reader on x64 with volatile
//     gives sufficient ordering for display purposes.
//
// Drawing model: everything goes through DrawingContext.Draw* in OnRender.
// Brushes and pens are static and Frozen so they can be safely reused
// across render passes without per-frame allocation. The transfer curve
// is sampled at 64 points per band per frame — ~250 points total, trivial
// for WPF's software rasteriser.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace PedalMComp
{
    // ── Factory ─────────────────────────────────────────────────────────
    // PreferWindowedGUI = false → embedded above the parameter sliders
    // in the standard rack parameter window. Core §26.

    [MachineGUIFactoryDecl(PreferWindowedGUI = false)]
    public class PedalMCompGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalMCompGui();
    }

    // ── GUI ─────────────────────────────────────────────────────────────

    public class PedalMCompGui : FrameworkElement, IMachineGUI
    {
        // ── Layout constants (px) ───────────────────────────────────────
        private const double W           = 560;     // total widget width
        private const double H           = 340;     // total widget height
        private const double COL_W       = 140;     // per-band column width
        private const double TOP_PAD     = 6;
        private const double LABEL_AREA  = 24;
        private const double METER_H     = 150;
        private const double METER_W     = 16;
        private const double METER_GAP   = 8;
        private const double METER_LABEL = 14;      // "IN" / "GR" caption row
        private const double CURVE_W     = 120;
        private const double CURVE_H     = 80;
        private const double CURVE_GAP   = 6;
        private const double TEXT_AREA   = 36;      // threshold/ratio readout

        // Meter display range
        private const float METER_MIN_DB = -60f;    // bottom of IN meter
        private const float GR_MAX_DB    = 24f;     // top of GR meter scale

        // ── Brushes & pens (frozen, shared across all instances) ────────
        private static readonly Brush BG          = FreezeBrush(0x18, 0x18, 0x18);
        private static readonly Brush PANEL_BG    = FreezeBrush(0x10, 0x10, 0x10);
        private static readonly Brush BORDER      = FreezeBrush(0x4A, 0x4A, 0x4A);
        private static readonly Brush LABEL_FG    = FreezeBrush(0xE0, 0xE0, 0xE0);
        private static readonly Brush CAPTION_FG  = FreezeBrush(0x90, 0x90, 0x90);
        private static readonly Brush METER_BG    = FreezeBrush(0x24, 0x24, 0x24);
        private static readonly Brush METER_GREEN = FreezeBrush(0x4D, 0xC5, 0x5A);
        private static readonly Brush METER_AMBER = FreezeBrush(0xE5, 0xD5, 0x3A);
        private static readonly Brush METER_RED   = FreezeBrush(0xE5, 0x5A, 0x4D);
        private static readonly Brush GR_BRUSH    = FreezeBrush(0xFF, 0xA0, 0x40);
        private static readonly Brush GRID_FAINT  = FreezeBrush(0x2A, 0x2A, 0x2A);
        private static readonly Brush DIAG_GREY   = FreezeBrush(0x55, 0x55, 0x55);
        private static readonly Brush CURVE_BLUE  = FreezeBrush(0x40, 0xC0, 0xFF);
        private static readonly Brush THRESH_AMBR = FreezeBrush(0xE5, 0xD5, 0x3A);
        private static readonly Brush BYP_OVERLAY = FreezeBrush(0x00, 0x00, 0x00, 0xB0);

        private static readonly Pen BORDER_PEN = FreezePen(BORDER,     1);
        private static readonly Pen GRID_PEN   = FreezePen(GRID_FAINT, 1);
        private static readonly Pen DIAG_PEN   = FreezePen(DIAG_GREY,  1);
        private static readonly Pen CURVE_PEN  = FreezePen(CURVE_BLUE, 1.5);
        private static readonly Pen THRESH_PEN = MakeDashedPen(THRESH_AMBR);

        private static readonly Typeface TYPEFACE = new Typeface("Segoe UI");

        // ── State ───────────────────────────────────────────────────────
        private PedalMCompMachine _machine;
        private IMachine          _iMachine;
        private readonly DispatcherTimer _timer;
        private double            _pixelsPerDip = 1.0;     // refreshed in OnRender

        private static readonly string[] BAND_NAMES = { "L", "LM", "HM", "H" };

        public PedalMCompGui()
        {
            Width  = W;
            Height = H;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            // ~30 fps. 33 ms balances responsiveness with cost; the audio
            // thread is writing meter values continuously, so the visible
            // ceiling is the timer rate.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, __) => InvalidateVisual();
            _timer.Start();
            Unloaded += (_, __) => _timer.Stop();
        }

        // Explicit measure — FrameworkElement's default MeasureOverride
        // returns Size(0,0), which would leave us invisible. Setting
        // Width/Height feeds the DP-driven measure path but we belt-and-
        // braces by returning the desired size directly.
        protected override Size MeasureOverride(Size availableSize) => new Size(W, H);

        public IMachine Machine
        {
            get => _iMachine;
            set
            {
                _iMachine = value;
                _machine  = value?.ManagedMachine as PedalMCompMachine;
            }
        }

        // ── Rendering ───────────────────────────────────────────────────

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            try { _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { _pixelsPerDip = 1.0; }

            dc.DrawRectangle(BG, null, new Rect(0, 0, W, H));

            // Defensive cast retry: in some ParameterWindowVM ordering
            // paths the Machine setter can fire before IMachine.ManagedMachine
            // is wired, leaving _machine null forever. Re-attempt every
            // frame — once it succeeds, this becomes a no-op.
            if (_machine == null && _iMachine != null)
                _machine = _iMachine.ManagedMachine as PedalMCompMachine;

            // Always render the layout structure; band content lights up
            // when _machine is non-null. Wrapped in try/catch so a render
            // exception doesn't abort the entire visual — WPF silently
            // swallows OnRender exceptions which masks the cause; this
            // catches them and at least keeps the background visible.
            try
            {
                for (int b = 0; b < 4; b++)
                    DrawColumn(dc, b);

                if (_machine == null)
                    DrawCenteredText(dc, "(waiting for machine connection)",
                        W / 2.0, H - 16, 10, false, CAPTION_FG);
            }
            catch
            {
                // Swallow — the partial drawing that succeeded before the
                // throw is still on screen. Repeated frames may succeed
                // (e.g. transient null reads during machine swap-out).
            }
        }

        private void DrawColumn(DrawingContext dc, int b)
        {
            double x0 = b * COL_W;

            // Inter-column separator
            if (b > 0)
                dc.DrawLine(BORDER_PEN, new Point(x0, 6), new Point(x0, H - 6));

            // Band label
            double y = TOP_PAD;
            DrawCenteredText(dc, BAND_NAMES[b], x0 + COL_W / 2.0, y, 15, true, LABEL_FG);
            y += LABEL_AREA;

            // Meter pair, centred in column. Reads default to safe values
            // so the layout still draws when _machine is null (we want the
            // empty meter troughs visible — confirms OnRender is alive).
            double metersTotalW = METER_W + METER_GAP + METER_W;
            double metersX = x0 + (COL_W - metersTotalW) / 2.0;

            float inDb  = -120f;
            float grDb  = 0f;
            bool  byp   = false;
            bool  connected = _machine != null;

            if (connected)
            {
                inDb = _machine.Bands[b].MeterInDb;
                grDb = _machine.Bands[b].MeterGrDb;
                byp  = ReadBypass(b);
            }

            DrawInMeter(dc, metersX,                          y, inDb);
            DrawGrMeter(dc, metersX + METER_W + METER_GAP,    y, grDb);

            // "IN" / "GR" captions under meters
            DrawCenteredText(dc, "IN",
                metersX + METER_W / 2.0, y + METER_H + 2, 9, false, CAPTION_FG);
            DrawCenteredText(dc, "GR",
                metersX + METER_W + METER_GAP + METER_W / 2.0,
                y + METER_H + 2, 9, false, CAPTION_FG);

            y += METER_H + METER_LABEL;

            // Transfer curve. When disconnected we still draw the box +
            // grid + reference diagonal (no actual curve, no threshold).
            double curveX = x0 + (COL_W - CURVE_W) / 2.0;

            if (connected)
            {
                double threshDb = -ReadThreshold(b);
                float  ratio    = PedalMCompMachine.MapRatioPub(ReadRatio(b));
                double kneeDb   = _machine.Knee;
                DrawTransferCurve(dc, curveX, y + CURVE_GAP,
                    threshDb, ratio, kneeDb, inDb, grDb);
            }
            else
            {
                DrawEmptyCurveBox(dc, curveX, y + CURVE_GAP);
            }

            y += CURVE_GAP + CURVE_H + 4;

            // Threshold / Ratio readout below curve
            if (connected)
            {
                double threshDb = -ReadThreshold(b);
                float  ratio    = PedalMCompMachine.MapRatioPub(ReadRatio(b));
                string threshStr = $"T {(int)threshDb} dB";
                string ratioStr  = FormatRatio(ratio);
                DrawCenteredText(dc, threshStr, x0 + COL_W / 2.0, y,      10, false, CAPTION_FG);
                DrawCenteredText(dc, ratioStr,  x0 + COL_W / 2.0, y + 14, 10, false, CAPTION_FG);
            }
            else
            {
                DrawCenteredText(dc, "T --",    x0 + COL_W / 2.0, y,      10, false, CAPTION_FG);
                DrawCenteredText(dc, "R --",    x0 + COL_W / 2.0, y + 14, 10, false, CAPTION_FG);
            }

            // Bypass overlay — semi-transparent black over the column
            if (connected && byp)
            {
                dc.DrawRectangle(BYP_OVERLAY, null,
                    new Rect(x0 + 4, TOP_PAD + LABEL_AREA - 4, COL_W - 8, H - LABEL_AREA - 8));
                DrawCenteredText(dc, "BYPASS",
                    x0 + COL_W / 2.0, H / 2.0 - 6, 12, true, LABEL_FG);
            }
        }

        // Draw just the box, grid, and reference diagonal — no compression
        // curve, threshold marker, or operating point. Used when the
        // machine reference hasn't connected yet.
        private void DrawEmptyCurveBox(DrawingContext dc, double x, double y)
        {
            Rect area = new Rect(x, y, CURVE_W, CURVE_H);
            dc.DrawRectangle(PANEL_BG, BORDER_PEN, area);

            for (int g = 12; g < -METER_MIN_DB; g += 12)
            {
                double frac = g / -METER_MIN_DB;
                double gx = x + (1 - frac) * CURVE_W;
                dc.DrawLine(GRID_PEN, new Point(gx, y), new Point(gx, y + CURVE_H));
                double gy = y + frac * CURVE_H;
                dc.DrawLine(GRID_PEN, new Point(x, gy), new Point(x + CURVE_W, gy));
            }

            dc.DrawLine(DIAG_PEN,
                new Point(x,             y + CURVE_H),
                new Point(x + CURVE_W,   y));
        }

        // ── Meter drawing ───────────────────────────────────────────────

        private void DrawInMeter(DrawingContext dc, double x, double y, float dbLevel)
        {
            // Background trough
            dc.DrawRectangle(METER_BG, BORDER_PEN, new Rect(x, y, METER_W, METER_H));

            // Compute fill height as fraction of [METER_MIN_DB .. 0 dB]
            double frac = Math.Clamp((dbLevel - METER_MIN_DB) / -METER_MIN_DB, 0, 1);
            double fillH = frac * METER_H;
            if (fillH < 1) return;

            // Three-zone gradient by absolute dB level:
            //   below -18 dB → green
            //   -18 to -6   → green→amber gradient (we approximate with bands)
            //   -6 to 0     → red
            double minus18Frac = (-18 - METER_MIN_DB) / -METER_MIN_DB;   // ≈ 0.70
            double minus6Frac  = ( -6 - METER_MIN_DB) / -METER_MIN_DB;   // ≈ 0.90

            double y0   = y + METER_H;          // bottom of bar (high y = low value)
            double yTop = y + (METER_H - fillH);

            // Green segment
            double greenTopY = y + METER_H - Math.Min(fillH, minus18Frac * METER_H);
            dc.DrawRectangle(METER_GREEN, null,
                new Rect(x, greenTopY, METER_W, y0 - greenTopY));

            if (frac > minus18Frac)
            {
                double amberTopY = y + METER_H - Math.Min(fillH, minus6Frac * METER_H);
                dc.DrawRectangle(METER_AMBER, null,
                    new Rect(x, amberTopY, METER_W,
                             (y0 - minus18Frac * METER_H) - amberTopY));
            }
            if (frac > minus6Frac)
            {
                dc.DrawRectangle(METER_RED, null,
                    new Rect(x, yTop, METER_W,
                             (y0 - minus6Frac * METER_H) - yTop));
            }
        }

        private void DrawGrMeter(DrawingContext dc, double x, double y, float grDb)
        {
            // Background trough
            dc.DrawRectangle(METER_BG, BORDER_PEN, new Rect(x, y, METER_W, METER_H));

            // GR fills DOWNWARD from the top — 0 dB GR is no fill,
            // GR_MAX_DB worth of reduction fills the full bar.
            double frac = Math.Clamp(grDb / GR_MAX_DB, 0, 1);
            double fillH = frac * METER_H;
            if (fillH < 1) return;

            dc.DrawRectangle(GR_BRUSH, null,
                new Rect(x, y, METER_W, fillH));
        }

        // ── Transfer curve ──────────────────────────────────────────────

        private void DrawTransferCurve(DrawingContext dc, double x, double y,
                                       double threshDb, float ratio, double kneeDb,
                                       float currentInDb, float currentGrDb)
        {
            // Curve area: input dB on x-axis [METER_MIN_DB .. 0],
            //             output dB on y-axis [METER_MIN_DB .. 0]
            // WPF screen coords: y grows downward.
            Rect area = new Rect(x, y, CURVE_W, CURVE_H);
            dc.DrawRectangle(PANEL_BG, BORDER_PEN, area);

            // Faint grid every 12 dB
            for (int g = 12; g < -METER_MIN_DB; g += 12)
            {
                double frac = g / -METER_MIN_DB;
                // vertical line at -g dB input
                double gx = x + (1 - frac) * CURVE_W;
                dc.DrawLine(GRID_PEN, new Point(gx, y), new Point(gx, y + CURVE_H));
                // horizontal line at -g dB output
                double gy = y + frac * CURVE_H;
                dc.DrawLine(GRID_PEN, new Point(x, gy), new Point(x + CURVE_W, gy));
            }

            // y = x reference (no compression)
            dc.DrawLine(DIAG_PEN,
                new Point(x,             y + CURVE_H),
                new Point(x + CURVE_W,   y));

            // Threshold marker (vertical dashed line at threshDb)
            double tFrac = (threshDb - METER_MIN_DB) / -METER_MIN_DB;
            tFrac = Math.Clamp(tFrac, 0, 1);
            double tx = x + tFrac * CURVE_W;
            dc.DrawLine(THRESH_PEN, new Point(tx, y), new Point(tx, y + CURVE_H));

            // Actual compression curve
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                bool first = true;
                const int N = 64;
                for (int i = 0; i <= N; i++)
                {
                    double inDb  = METER_MIN_DB + (i / (double)N) * -METER_MIN_DB;
                    double gr    = BandCompressor.SoftKneeGR((float)inDb, (float)threshDb,
                                                             (float)kneeDb, ratio);
                    double outDb = inDb - gr;
                    if (outDb < METER_MIN_DB) outDb = METER_MIN_DB;
                    if (outDb > 0)            outDb = 0;
                    double px = x + ((inDb  - METER_MIN_DB) / -METER_MIN_DB) * CURVE_W;
                    double py = y + (1 - (outDb - METER_MIN_DB) / -METER_MIN_DB) * CURVE_H;
                    var pt = new Point(px, py);
                    if (first) { ctx.BeginFigure(pt, false, false); first = false; }
                    else       { ctx.LineTo(pt, true, false); }
                }
            }
            geom.Freeze();
            dc.DrawGeometry(null, CURVE_PEN, geom);

            // Operating-point dot (where the current input level lands on
            // the curve). Useful "here is what the compressor is doing
            // right now" indicator that ties the meters to the curve.
            if (currentInDb > METER_MIN_DB)
            {
                double opIn  = Math.Clamp(currentInDb, METER_MIN_DB, 0);
                double opOut = opIn - currentGrDb;
                if (opOut < METER_MIN_DB) opOut = METER_MIN_DB;
                double px = x + ((opIn  - METER_MIN_DB) / -METER_MIN_DB) * CURVE_W;
                double py = y + (1 - (opOut - METER_MIN_DB) / -METER_MIN_DB) * CURVE_H;
                dc.DrawEllipse(METER_AMBER, null, new Point(px, py), 3, 3);
            }
        }

        // ── Parameter accessors (per-band index → property value) ──────

        private int ReadThreshold(int b)
        {
            switch (b)
            {
                case 0: return _machine.L_Threshold;
                case 1: return _machine.LM_Threshold;
                case 2: return _machine.HM_Threshold;
                default:return _machine.H_Threshold;
            }
        }

        private int ReadRatio(int b)
        {
            switch (b)
            {
                case 0: return _machine.L_Ratio;
                case 1: return _machine.LM_Ratio;
                case 2: return _machine.HM_Ratio;
                default:return _machine.H_Ratio;
            }
        }

        private bool ReadBypass(int b)
        {
            switch (b)
            {
                case 0: return _machine.L_Bypass  != 0;
                case 1: return _machine.LM_Bypass != 0;
                case 2: return _machine.HM_Bypass != 0;
                default:return _machine.H_Bypass  != 0;
            }
        }

        // ── Text & ratio formatting helpers ─────────────────────────────

        private void DrawCenteredText(DrawingContext dc, string s, double cx, double y,
                                      double size, bool bold, Brush fg)
        {
            var ft = new FormattedText(
                s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                TYPEFACE, size, fg, _pixelsPerDip);
            if (bold) ft.SetFontWeight(FontWeights.Bold);
            dc.DrawText(ft, new Point(cx - ft.Width / 2.0, y));
        }

        private static string FormatRatio(float r)
        {
            if (r >= 1e5f)   return "R Limit";
            if (r < 2f)      return $"R {r:0.00}:1";
            if (r < 10f)     return $"R {r:0.0}:1";
            return $"R {(int)Math.Round(r)}:1";
        }

        // ── Frozen-brush/pen factories ──────────────────────────────────

        private static Brush FreezeBrush(byte r, byte g, byte b, byte a = 255)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        private static Pen FreezePen(Brush b, double th)
        {
            var p = new Pen(b, th);
            p.Freeze();
            return p;
        }

        private static Pen MakeDashedPen(Brush b)
        {
            var p = new Pen(b, 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
            p.Freeze();
            return p;
        }
    }
}
