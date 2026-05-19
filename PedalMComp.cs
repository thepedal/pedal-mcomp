// PedalMComp.cs — Pedal MComp v1.0.
//
// 4-band stereo soft-knee compressor with Linkwitz-Riley 24 dB/oct crossovers
// in a balanced binary-tree topology (all four bands share the same group
// delay). Per-band threshold/ratio/attack/release/makeup/bypass; shared knee,
// detection mode (peak/RMS), output gain, dry-wet, and a per-band Listen
// selector.
//
// Architecture:
//
//                      ┌─ LP@xover1 ──→ LOW
//          ┌─ LP@xover2 ┤
//          │            └─ HP@xover1 ──→ LO-MID
//   input ─┤
//          │            ┌─ LP@xover3 ──→ HI-MID
//          └─ HP@xover2 ┤
//                       └─ HP@xover3 ──→ HIGH
//
// Parameter declarations and their ValueDescriptions arrays are generated
// by gen_param_labels.py (lives in source, not deployed per Pedal invFFT
// §23.4 convention). If parameter ranges or encodings change, edit the
// PARAMS table in that script and paste the regenerated block into the
// "Parameter declarations" section below.
//
// 32 globals total (declaration order frozen per Build §3.3):
//   1   Listen        2-4 Xover L-LM / LM-HM / HM-H
//   5   Knee          6  Detection
//   7-12  L  band:  Threshold / Ratio / Attack / Release / Makeup / Bypass
//   13-18 LM band:  same six
//   19-24 HM band:  same six
//   25-30 H  band:  same six
//   31  Output Gain  32  Dry/Wet

using System;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace PedalMComp
{
    [MachineDecl(
        Name      = "Pedal MComp",
        ShortName = "MComp",
        Author    = "WD",
        MaxTracks = 1)]
    public class PedalMCompMachine : IBuzzMachine
    {
        // ─────────────────────────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────────────────────────

        private const float SCALE     = 1f / 32768f;
        private const float INV_SCALE = 32768f;

        // Each crossover gets its own Hz range. Min/max don't overlap by
        // design so the rack dial maps to musically useful values for that
        // boundary. Must match the log_range bounds in gen_param_labels.py.
        private const float XO1_MIN_HZ =   40f, XO1_MAX_HZ =   600f;
        private const float XO2_MIN_HZ =  200f, XO2_MAX_HZ =  3000f;
        private const float XO3_MIN_HZ =  800f, XO3_MAX_HZ = 12000f;

        // Ratio encoding: 0..126 maps log-linearly from 1.0 to 40.0;
        // value 127 is the explicit limit position (effectively ∞:1).
        private const float RATIO_MAX_INDEX   = 126f;
        private const float RATIO_MAX         = 40f;
        private const float RATIO_LIMIT_VALUE = 1e6f;

        // Attack/Release encoding: 0..127 log-mapped. Ranges chosen to match
        // gen_param_labels.py — the ValueDescriptions strings would lie if
        // these bounds disagreed.
        private const float ATTACK_MIN_MS  =   0.1f, ATTACK_MAX_MS  =  200f;
        private const float RELEASE_MIN_MS =   1.0f, RELEASE_MAX_MS = 2000f;

        // ─────────────────────────────────────────────────────────────────
        //  Host + DSP state
        // ─────────────────────────────────────────────────────────────────

        private readonly IBuzzMachineHost _host;

        // Six LR4 stages: two for the mid split, two each for the inner splits.
        private readonly Lr4Filter _xoMidLP  = new Lr4Filter(Lr4Mode.Lowpass);
        private readonly Lr4Filter _xoMidHP  = new Lr4Filter(Lr4Mode.Highpass);
        private readonly Lr4Filter _xoLowLP  = new Lr4Filter(Lr4Mode.Lowpass);
        private readonly Lr4Filter _xoLowHP  = new Lr4Filter(Lr4Mode.Highpass);
        private readonly Lr4Filter _xoHighLP = new Lr4Filter(Lr4Mode.Lowpass);
        private readonly Lr4Filter _xoHighHP = new Lr4Filter(Lr4Mode.Highpass);

        // All-pass phase correction (v1.3): one per branch, each tuned to
        // the OPPOSITE branch's crossover Fc, so both branches end up with
        // identical phase response and the sum is phase-coherent. Used
        // only when PhaseLinear != 0.
        private readonly Allpass _apHighOnLowBranch = new Allpass();   // Fc = high_xover
        private readonly Allpass _apLowOnHighBranch = new Allpass();   // Fc = low_xover

        // Spectrum analyser (v1.3): fed mono mix per sample, runs FFT on
        // its own cadence, exposes magnitudes for the GUI to read.
        private readonly SpectrumAnalyzer _spectrum = new SpectrumAnalyzer();

        // Dry-side delay buffers (v1.3 lookahead): when Lookahead > 0 the
        // wet path is delayed by N samples inside each BandCompressor.
        // For the dry/wet mix to remain phase-coherent, the dry signal
        // must take an identical delay. Same MAX size as BandCompressor's
        // per-band buffers (2048 = ~10.7 ms at 192 kHz).
        private const int MAX_DRY_DELAY_SAMPLES = 2048;
        private readonly float[] _dryDelayL = new float[MAX_DRY_DELAY_SAMPLES];
        private readonly float[] _dryDelayR = new float[MAX_DRY_DELAY_SAMPLES];
        private int _dryDelayWriteIdx = 0;

        // Four band compressors, indexed 0=L, 1=LM, 2=HM, 3=H.
        private readonly BandCompressor[] _bands = new BandCompressor[4];

        // Scratch arrays for per-band coefficients, preallocated to avoid
        // GC pressure on the audio thread.
        private readonly float[] _threshDb  = new float[4];
        private readonly float[] _ratio     = new float[4];
        private readonly float[] _makeupLin = new float[4];
        private readonly bool[]  _bypass    = new bool[4];

        // ── Output meter state (audio thread writes per-buffer, UI reads) ──
        // Smoothed peak envelope of post-everything output (post Output Gain,
        // post Dry-Wet mix). Release-only smoothing — attacks are instant,
        // releases follow ~100 ms decay. PedalComp §7 volatile pattern.
        private float _outEnvL = 0f;
        private float _outEnvR = 0f;
        public volatile float MeterOutLeftDb  = -120f;
        public volatile float MeterOutRightDb = -120f;

        public PedalMCompMachine(IBuzzMachineHost host)
        {
            _host = host;
            for (int i = 0; i < 4; i++) _bands[i] = new BandCompressor();
        }

        // ─────────────────────────────────────────────────────────────────
        //  GUI accessors — read-only views the GUI class uses to render
        //  meters and transfer curves. The GUI lives in the same assembly
        //  so internal access is fine (PedalComp §8 sharing pattern).
        // ─────────────────────────────────────────────────────────────────

        internal BandCompressor[] Bands => _bands;

        // Spectrum analyser exposed for the GUI's spectrum view (read-only
        // from UI thread; updated by audio thread via Feed()).
        internal SpectrumAnalyzer Spectrum => _spectrum;

        // Mirror of the const Hz bounds — GUI uses these to convert raw
        // xover indices back to display Hz. Internal because they're an
        // implementation detail of the parameter encoding.
        internal const float XO1_MIN_HZ_PUB =   40f, XO1_MAX_HZ_PUB =   600f;
        internal const float XO2_MIN_HZ_PUB =  200f, XO2_MAX_HZ_PUB =  3000f;
        internal const float XO3_MIN_HZ_PUB =  800f, XO3_MAX_HZ_PUB = 12000f;
        internal const float ATTACK_MIN_MS_PUB  = 0.1f, ATTACK_MAX_MS_PUB  =  200f;
        internal const float RELEASE_MIN_MS_PUB = 1.0f, RELEASE_MAX_MS_PUB = 2000f;

        // Convert raw 0..127 index → Hz / ms / ratio. Marked internal static
        // per PedalComp §8 so the GUI uses the same mapping as the DSP path.
        internal static float MapHzLogPub(int v, float loHz, float hiHz)
            => loHz * MathF.Pow(hiHz / loHz, v / 127f);

        internal static float MapMsLogPub(int v, float loMs, float hiMs)
            => loMs * MathF.Pow(hiMs / loMs, v / 127f);

        internal static float MapRatioPub(int v)
            => v >= 127 ? 1e6f : MathF.Pow(40f, v / 126f);

        // ─────────────────────────────────────────────────────────────────
        //  Parameter declarations (frozen order — Build §3.3)
        //
        //  This block is generated by gen_param_labels.py — see comments at
        //  the top of this file. To regenerate after a range change:
        //    python3 gen_param_labels.py > params_block.cs.txt
        //  then replace this section with the contents of params_block.cs.txt.
        // ─────────────────────────────────────────────────────────────────

        [ParameterDecl(
            Name        = "Listen",
            Description = "Solo a single band for monitoring; All sums every band",
            MinValue = 0, MaxValue = 4, DefValue = 0,
            ValueDescriptions = new[] { "All", "L", "LM", "HM", "H" })]
        public int Listen { get; set; }

        [ParameterDecl(
            Name        = "Xover L-LM",
            Description = "Crossover frequency between Low and Lo-Mid bands (40-600 Hz, log)",
            MinValue = 0, MaxValue = 127, DefValue = 52,
            ValueDescriptions = new[] {
                "40 Hz", "41 Hz", "42 Hz", "43 Hz", "44 Hz", "45 Hz", "45 Hz", "46 Hz",
                "47 Hz", "48 Hz", "50 Hz", "51 Hz", "52 Hz", "53 Hz", "54 Hz", "55 Hz",
                "56 Hz", "57 Hz", "59 Hz", "60 Hz", "61 Hz", "63 Hz", "64 Hz", "65 Hz",
                "67 Hz", "68 Hz", "70 Hz", "71 Hz", "73 Hz", "74 Hz", "76 Hz", "77 Hz",
                "79 Hz", "81 Hz", "83 Hz", "84 Hz", "86 Hz", "88 Hz", "90 Hz", "92 Hz",
                "94 Hz", "96 Hz", "98 Hz", "100 Hz", "102 Hz", "104 Hz", "107 Hz", "109 Hz",
                "111 Hz", "114 Hz", "116 Hz", "119 Hz", "121 Hz", "124 Hz", "127 Hz", "129 Hz",
                "132 Hz", "135 Hz", "138 Hz", "141 Hz", "144 Hz", "147 Hz", "150 Hz", "153 Hz",
                "157 Hz", "160 Hz", "163 Hz", "167 Hz", "171 Hz", "174 Hz", "178 Hz", "182 Hz",
                "186 Hz", "190 Hz", "194 Hz", "198 Hz", "202 Hz", "207 Hz", "211 Hz", "216 Hz",
                "220 Hz", "225 Hz", "230 Hz", "235 Hz", "240 Hz", "245 Hz", "250 Hz", "256 Hz",
                "261 Hz", "267 Hz", "273 Hz", "278 Hz", "284 Hz", "291 Hz", "297 Hz", "303 Hz",
                "310 Hz", "316 Hz", "323 Hz", "330 Hz", "337 Hz", "345 Hz", "352 Hz", "360 Hz",
                "367 Hz", "375 Hz", "383 Hz", "392 Hz", "400 Hz", "409 Hz", "418 Hz", "427 Hz",
                "436 Hz", "445 Hz", "455 Hz", "465 Hz", "475 Hz", "485 Hz", "495 Hz", "506 Hz",
                "517 Hz", "528 Hz", "539 Hz", "551 Hz", "563 Hz", "575 Hz", "587 Hz", "600 Hz"
            })]
        public int XoverLLM { get; set; }

        [ParameterDecl(
            Name        = "Xover LM-HM",
            Description = "Crossover frequency between Lo-Mid and Hi-Mid bands (200-3000 Hz, log)",
            MinValue = 0, MaxValue = 127, DefValue = 59,
            ValueDescriptions = new[] {
                "200 Hz", "204 Hz", "209 Hz", "213 Hz", "218 Hz", "223 Hz", "227 Hz", "232 Hz",
                "237 Hz", "242 Hz", "248 Hz", "253 Hz", "258 Hz", "264 Hz", "270 Hz", "275 Hz",
                "281 Hz", "287 Hz", "294 Hz", "300 Hz", "306 Hz", "313 Hz", "320 Hz", "327 Hz",
                "334 Hz", "341 Hz", "348 Hz", "356 Hz", "363 Hz", "371 Hz", "379 Hz", "387 Hz",
                "396 Hz", "404 Hz", "413 Hz", "422 Hz", "431 Hz", "440 Hz", "450 Hz", "459 Hz",
                "469 Hz", "479 Hz", "490 Hz", "500 Hz", "511 Hz", "522 Hz", "533 Hz", "545 Hz",
                "557 Hz", "569 Hz", "581 Hz", "593 Hz", "606 Hz", "619 Hz", "633 Hz", "646 Hz",
                "660 Hz", "674 Hz", "689 Hz", "704 Hz", "719 Hz", "734 Hz", "750 Hz", "766 Hz",
                "783 Hz", "800 Hz", "817 Hz", "835 Hz", "853 Hz", "871 Hz", "890 Hz", "909 Hz",
                "929 Hz", "949 Hz", "969 Hz", "990 Hz", "1.01 kHz", "1.03 kHz", "1.06 kHz", "1.08 kHz",
                "1.10 kHz", "1.12 kHz", "1.15 kHz", "1.17 kHz", "1.20 kHz", "1.23 kHz", "1.25 kHz", "1.28 kHz",
                "1.31 kHz", "1.33 kHz", "1.36 kHz", "1.39 kHz", "1.42 kHz", "1.45 kHz", "1.48 kHz", "1.52 kHz",
                "1.55 kHz", "1.58 kHz", "1.62 kHz", "1.65 kHz", "1.69 kHz", "1.72 kHz", "1.76 kHz", "1.80 kHz",
                "1.84 kHz", "1.88 kHz", "1.92 kHz", "1.96 kHz", "2.00 kHz", "2.04 kHz", "2.09 kHz", "2.13 kHz",
                "2.18 kHz", "2.23 kHz", "2.27 kHz", "2.32 kHz", "2.37 kHz", "2.42 kHz", "2.48 kHz", "2.53 kHz",
                "2.58 kHz", "2.64 kHz", "2.70 kHz", "2.75 kHz", "2.81 kHz", "2.87 kHz", "2.94 kHz", "3.00 kHz"
            })]
        public int XoverLMHM { get; set; }

        [ParameterDecl(
            Name        = "Xover HM-H",
            Description = "Crossover frequency between Hi-Mid and High bands (800-12000 Hz, log)",
            MinValue = 0, MaxValue = 127, DefValue = 75,
            ValueDescriptions = new[] {
                "800 Hz", "817 Hz", "835 Hz", "853 Hz", "871 Hz", "890 Hz", "909 Hz", "929 Hz",
                "949 Hz", "969 Hz", "990 Hz", "1.01 kHz", "1.03 kHz", "1.06 kHz", "1.08 kHz", "1.10 kHz",
                "1.13 kHz", "1.15 kHz", "1.17 kHz", "1.20 kHz", "1.23 kHz", "1.25 kHz", "1.28 kHz", "1.31 kHz",
                "1.33 kHz", "1.36 kHz", "1.39 kHz", "1.42 kHz", "1.45 kHz", "1.48 kHz", "1.52 kHz", "1.55 kHz",
                "1.58 kHz", "1.62 kHz", "1.65 kHz", "1.69 kHz", "1.72 kHz", "1.76 kHz", "1.80 kHz", "1.84 kHz",
                "1.88 kHz", "1.92 kHz", "1.96 kHz", "2.00 kHz", "2.04 kHz", "2.09 kHz", "2.13 kHz", "2.18 kHz",
                "2.23 kHz", "2.27 kHz", "2.32 kHz", "2.37 kHz", "2.42 kHz", "2.48 kHz", "2.53 kHz", "2.58 kHz",
                "2.64 kHz", "2.70 kHz", "2.76 kHz", "2.81 kHz", "2.88 kHz", "2.94 kHz", "3.00 kHz", "3.07 kHz",
                "3.13 kHz", "3.20 kHz", "3.27 kHz", "3.34 kHz", "3.41 kHz", "3.48 kHz", "3.56 kHz", "3.64 kHz",
                "3.71 kHz", "3.79 kHz", "3.88 kHz", "3.96 kHz", "4.04 kHz", "4.13 kHz", "4.22 kHz", "4.31 kHz",
                "4.40 kHz", "4.50 kHz", "4.60 kHz", "4.70 kHz", "4.80 kHz", "4.90 kHz", "5.01 kHz", "5.11 kHz",
                "5.22 kHz", "5.34 kHz", "5.45 kHz", "5.57 kHz", "5.69 kHz", "5.81 kHz", "5.94 kHz", "6.07 kHz",
                "6.20 kHz", "6.33 kHz", "6.47 kHz", "6.61 kHz", "6.75 kHz", "6.89 kHz", "7.04 kHz", "7.19 kHz",
                "7.35 kHz", "7.51 kHz", "7.67 kHz", "7.83 kHz", "8.00 kHz", "8.18 kHz", "8.35 kHz", "8.53 kHz",
                "8.72 kHz", "8.90 kHz", "9.09 kHz", "9.29 kHz", "9.49 kHz", "9.70 kHz", "9.90 kHz", "10.12 kHz",
                "10.34 kHz", "10.56 kHz", "10.79 kHz", "11.02 kHz", "11.26 kHz", "11.50 kHz", "11.75 kHz", "12.00 kHz"
            })]
        public int XoverHMH { get; set; }

        [ParameterDecl(
            Name        = "Knee",
            Description = "Soft-knee width in dB, shared across all bands",
            MinValue = 0, MaxValue = 24, DefValue = 6,
            ValueDescriptions = new[] {
                "0.0 dB", "1.0 dB", "2.0 dB", "3.0 dB", "4.0 dB", "5.0 dB", "6.0 dB", "7.0 dB",
                "8.0 dB", "9.0 dB", "10.0 dB", "11.0 dB", "12.0 dB", "13.0 dB", "14.0 dB", "15.0 dB",
                "16.0 dB", "17.0 dB", "18.0 dB", "19.0 dB", "20.0 dB", "21.0 dB", "22.0 dB", "23.0 dB",
                "24.0 dB"
            })]
        public int Knee { get; set; }

        [ParameterDecl(
            Name        = "Detection",
            Description = "Level detection mode, shared across bands",
            MinValue = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Peak", "RMS" })]
        public int Detection { get; set; }

        [ParameterDecl(
            Name        = "L Threshold",
            Description = "L band threshold in dB below 0 dBFS",
            MinValue = 0, MaxValue = 60, DefValue = 18,
            ValueDescriptions = new[] {
                "0.0 dB", "-1.0 dB", "-2.0 dB", "-3.0 dB", "-4.0 dB", "-5.0 dB", "-6.0 dB", "-7.0 dB",
                "-8.0 dB", "-9.0 dB", "-10.0 dB", "-11.0 dB", "-12.0 dB", "-13.0 dB", "-14.0 dB", "-15.0 dB",
                "-16.0 dB", "-17.0 dB", "-18.0 dB", "-19.0 dB", "-20.0 dB", "-21.0 dB", "-22.0 dB", "-23.0 dB",
                "-24.0 dB", "-25.0 dB", "-26.0 dB", "-27.0 dB", "-28.0 dB", "-29.0 dB", "-30.0 dB", "-31.0 dB",
                "-32.0 dB", "-33.0 dB", "-34.0 dB", "-35.0 dB", "-36.0 dB", "-37.0 dB", "-38.0 dB", "-39.0 dB",
                "-40.0 dB", "-41.0 dB", "-42.0 dB", "-43.0 dB", "-44.0 dB", "-45.0 dB", "-46.0 dB", "-47.0 dB",
                "-48.0 dB", "-49.0 dB", "-50.0 dB", "-51.0 dB", "-52.0 dB", "-53.0 dB", "-54.0 dB", "-55.0 dB",
                "-56.0 dB", "-57.0 dB", "-58.0 dB", "-59.0 dB", "-60.0 dB"
            })]
        public int L_Threshold { get; set; }

        [ParameterDecl(
            Name        = "L Ratio",
            Description = "L band ratio, log-mapped 1:1 to 40:1; top value is Limit",
            MinValue = 0, MaxValue = 127, DefValue = 31,
            ValueDescriptions = new[] {
                "1.00:1", "1.03:1", "1.06:1", "1.09:1", "1.12:1", "1.16:1", "1.19:1", "1.23:1",
                "1.26:1", "1.30:1", "1.34:1", "1.38:1", "1.42:1", "1.46:1", "1.51:1", "1.55:1",
                "1.60:1", "1.64:1", "1.69:1", "1.74:1", "1.80:1", "1.85:1", "1.90:1", "1.96:1",
                "2.0:1", "2.1:1", "2.1:1", "2.2:1", "2.3:1", "2.3:1", "2.4:1", "2.5:1",
                "2.6:1", "2.6:1", "2.7:1", "2.8:1", "2.9:1", "3.0:1", "3.0:1", "3.1:1",
                "3.2:1", "3.3:1", "3.4:1", "3.5:1", "3.6:1", "3.7:1", "3.8:1", "4.0:1",
                "4.1:1", "4.2:1", "4.3:1", "4.5:1", "4.6:1", "4.7:1", "4.9:1", "5.0:1",
                "5.2:1", "5.3:1", "5.5:1", "5.6:1", "5.8:1", "6.0:1", "6.1:1", "6.3:1",
                "6.5:1", "6.7:1", "6.9:1", "7.1:1", "7.3:1", "7.5:1", "7.8:1", "8.0:1",
                "8.2:1", "8.5:1", "8.7:1", "9.0:1", "9.3:1", "9.5:1", "9.8:1", "10:1",
                "10:1", "11:1", "11:1", "11:1", "12:1", "12:1", "12:1", "13:1",
                "13:1", "14:1", "14:1", "14:1", "15:1", "15:1", "16:1", "16:1",
                "17:1", "17:1", "18:1", "18:1", "19:1", "19:1", "20:1", "20:1",
                "21:1", "22:1", "22:1", "23:1", "24:1", "24:1", "25:1", "26:1",
                "27:1", "27:1", "28:1", "29:1", "30:1", "31:1", "32:1", "33:1",
                "34:1", "35:1", "36:1", "37:1", "38:1", "39:1", "40:1", "Limit"
            })]
        public int L_Ratio { get; set; }

        [ParameterDecl(
            Name        = "L Attack",
            Description = "L band attack time, log-mapped 0.1 ms to 200 ms",
            MinValue = 0, MaxValue = 127, DefValue = 95,
            ValueDescriptions = new[] {
                "0.100 ms", "0.106 ms", "0.113 ms", "0.120 ms", "0.127 ms", "0.135 ms", "0.143 ms", "0.152 ms",
                "0.161 ms", "0.171 ms", "0.182 ms", "0.193 ms", "0.205 ms", "0.218 ms", "0.231 ms", "0.245 ms",
                "0.261 ms", "0.277 ms", "0.294 ms", "0.312 ms", "0.331 ms", "0.351 ms", "0.373 ms", "0.396 ms",
                "0.421 ms", "0.446 ms", "0.474 ms", "0.503 ms", "0.534 ms", "0.567 ms", "0.602 ms", "0.639 ms",
                "0.679 ms", "0.721 ms", "0.765 ms", "0.812 ms", "0.862 ms", "0.916 ms", "0.972 ms", "1.03 ms",
                "1.10 ms", "1.16 ms", "1.24 ms", "1.31 ms", "1.39 ms", "1.48 ms", "1.57 ms", "1.67 ms",
                "1.77 ms", "1.88 ms", "1.99 ms", "2.12 ms", "2.25 ms", "2.39 ms", "2.53 ms", "2.69 ms",
                "2.85 ms", "3.03 ms", "3.22 ms", "3.42 ms", "3.63 ms", "3.85 ms", "4.09 ms", "4.34 ms",
                "4.61 ms", "4.89 ms", "5.19 ms", "5.51 ms", "5.85 ms", "6.22 ms", "6.60 ms", "7.01 ms",
                "7.44 ms", "7.90 ms", "8.38 ms", "8.90 ms", "9.45 ms", "10.0 ms", "10.7 ms", "11.3 ms",
                "12.0 ms", "12.7 ms", "13.5 ms", "14.4 ms", "15.3 ms", "16.2 ms", "17.2 ms", "18.3 ms",
                "19.4 ms", "20.6 ms", "21.8 ms", "23.2 ms", "24.6 ms", "26.1 ms", "27.8 ms", "29.5 ms",
                "31.3 ms", "33.2 ms", "35.3 ms", "37.4 ms", "39.7 ms", "42.2 ms", "44.8 ms", "47.6 ms",
                "50.5 ms", "53.6 ms", "56.9 ms", "60.4 ms", "64.1 ms", "68.1 ms", "72.3 ms", "76.8 ms",
                "81.5 ms", "86.5 ms", "91.9 ms", "97.5 ms", "104 ms", "110 ms", "117 ms", "124 ms",
                "132 ms", "140 ms", "148 ms", "157 ms", "167 ms", "177 ms", "188 ms", "200 ms"
            })]
        public int L_Attack { get; set; }

        [ParameterDecl(
            Name        = "L Release",
            Description = "L band release time, log-mapped 1 ms to 2000 ms",
            MinValue = 0, MaxValue = 127, DefValue = 89,
            ValueDescriptions = new[] {
                "1.00 ms", "1.06 ms", "1.13 ms", "1.20 ms", "1.27 ms", "1.35 ms", "1.43 ms", "1.52 ms",
                "1.61 ms", "1.71 ms", "1.82 ms", "1.93 ms", "2.05 ms", "2.18 ms", "2.31 ms", "2.45 ms",
                "2.61 ms", "2.77 ms", "2.94 ms", "3.12 ms", "3.31 ms", "3.51 ms", "3.73 ms", "3.96 ms",
                "4.21 ms", "4.46 ms", "4.74 ms", "5.03 ms", "5.34 ms", "5.67 ms", "6.02 ms", "6.39 ms",
                "6.79 ms", "7.21 ms", "7.65 ms", "8.12 ms", "8.62 ms", "9.16 ms", "9.72 ms", "10.3 ms",
                "11.0 ms", "11.6 ms", "12.4 ms", "13.1 ms", "13.9 ms", "14.8 ms", "15.7 ms", "16.7 ms",
                "17.7 ms", "18.8 ms", "19.9 ms", "21.2 ms", "22.5 ms", "23.9 ms", "25.3 ms", "26.9 ms",
                "28.5 ms", "30.3 ms", "32.2 ms", "34.2 ms", "36.3 ms", "38.5 ms", "40.9 ms", "43.4 ms",
                "46.1 ms", "48.9 ms", "51.9 ms", "55.1 ms", "58.5 ms", "62.2 ms", "66.0 ms", "70.1 ms",
                "74.4 ms", "79.0 ms", "83.8 ms", "89.0 ms", "94.5 ms", "100 ms", "107 ms", "113 ms",
                "120 ms", "127 ms", "135 ms", "144 ms", "153 ms", "162 ms", "172 ms", "183 ms",
                "194 ms", "206 ms", "218 ms", "232 ms", "246 ms", "261 ms", "278 ms", "295 ms",
                "313 ms", "332 ms", "353 ms", "374 ms", "397 ms", "422 ms", "448 ms", "476 ms",
                "505 ms", "536 ms", "569 ms", "604 ms", "641 ms", "681 ms", "723 ms", "768 ms",
                "815 ms", "865 ms", "919 ms", "975 ms", "1035 ms", "1099 ms", "1167 ms", "1239 ms",
                "1315 ms", "1397 ms", "1483 ms", "1574 ms", "1671 ms", "1774 ms", "1884 ms", "2000 ms"
            })]
        public int L_Release { get; set; }

        [ParameterDecl(
            Name        = "L Makeup",
            Description = "L band makeup gain in dB",
            MinValue = 0, MaxValue = 24, DefValue = 0,
            ValueDescriptions = new[] {
                "0.0 dB", "1.0 dB", "2.0 dB", "3.0 dB", "4.0 dB", "5.0 dB", "6.0 dB", "7.0 dB",
                "8.0 dB", "9.0 dB", "10.0 dB", "11.0 dB", "12.0 dB", "13.0 dB", "14.0 dB", "15.0 dB",
                "16.0 dB", "17.0 dB", "18.0 dB", "19.0 dB", "20.0 dB", "21.0 dB", "22.0 dB", "23.0 dB",
                "24.0 dB"
            })]
        public int L_Makeup { get; set; }

        [ParameterDecl(
            Name        = "L Bypass",
            Description = "Bypass compression on the L band; band still routes through the crossover",
            MinValue = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int L_Bypass { get; set; }

        [ParameterDecl(
            Name        = "LM Threshold",
            Description = "LM band threshold in dB below 0 dBFS",
            MinValue = 0, MaxValue = 60, DefValue = 18,
            ValueDescriptions = new[] {
                "0.0 dB", "-1.0 dB", "-2.0 dB", "-3.0 dB", "-4.0 dB", "-5.0 dB", "-6.0 dB", "-7.0 dB",
                "-8.0 dB", "-9.0 dB", "-10.0 dB", "-11.0 dB", "-12.0 dB", "-13.0 dB", "-14.0 dB", "-15.0 dB",
                "-16.0 dB", "-17.0 dB", "-18.0 dB", "-19.0 dB", "-20.0 dB", "-21.0 dB", "-22.0 dB", "-23.0 dB",
                "-24.0 dB", "-25.0 dB", "-26.0 dB", "-27.0 dB", "-28.0 dB", "-29.0 dB", "-30.0 dB", "-31.0 dB",
                "-32.0 dB", "-33.0 dB", "-34.0 dB", "-35.0 dB", "-36.0 dB", "-37.0 dB", "-38.0 dB", "-39.0 dB",
                "-40.0 dB", "-41.0 dB", "-42.0 dB", "-43.0 dB", "-44.0 dB", "-45.0 dB", "-46.0 dB", "-47.0 dB",
                "-48.0 dB", "-49.0 dB", "-50.0 dB", "-51.0 dB", "-52.0 dB", "-53.0 dB", "-54.0 dB", "-55.0 dB",
                "-56.0 dB", "-57.0 dB", "-58.0 dB", "-59.0 dB", "-60.0 dB"
            })]
        public int LM_Threshold { get; set; }

        [ParameterDecl(
            Name        = "LM Ratio",
            Description = "LM band ratio, log-mapped 1:1 to 40:1; top value is Limit",
            MinValue = 0, MaxValue = 127, DefValue = 31,
            ValueDescriptions = new[] {
                "1.00:1", "1.03:1", "1.06:1", "1.09:1", "1.12:1", "1.16:1", "1.19:1", "1.23:1",
                "1.26:1", "1.30:1", "1.34:1", "1.38:1", "1.42:1", "1.46:1", "1.51:1", "1.55:1",
                "1.60:1", "1.64:1", "1.69:1", "1.74:1", "1.80:1", "1.85:1", "1.90:1", "1.96:1",
                "2.0:1", "2.1:1", "2.1:1", "2.2:1", "2.3:1", "2.3:1", "2.4:1", "2.5:1",
                "2.6:1", "2.6:1", "2.7:1", "2.8:1", "2.9:1", "3.0:1", "3.0:1", "3.1:1",
                "3.2:1", "3.3:1", "3.4:1", "3.5:1", "3.6:1", "3.7:1", "3.8:1", "4.0:1",
                "4.1:1", "4.2:1", "4.3:1", "4.5:1", "4.6:1", "4.7:1", "4.9:1", "5.0:1",
                "5.2:1", "5.3:1", "5.5:1", "5.6:1", "5.8:1", "6.0:1", "6.1:1", "6.3:1",
                "6.5:1", "6.7:1", "6.9:1", "7.1:1", "7.3:1", "7.5:1", "7.8:1", "8.0:1",
                "8.2:1", "8.5:1", "8.7:1", "9.0:1", "9.3:1", "9.5:1", "9.8:1", "10:1",
                "10:1", "11:1", "11:1", "11:1", "12:1", "12:1", "12:1", "13:1",
                "13:1", "14:1", "14:1", "14:1", "15:1", "15:1", "16:1", "16:1",
                "17:1", "17:1", "18:1", "18:1", "19:1", "19:1", "20:1", "20:1",
                "21:1", "22:1", "22:1", "23:1", "24:1", "24:1", "25:1", "26:1",
                "27:1", "27:1", "28:1", "29:1", "30:1", "31:1", "32:1", "33:1",
                "34:1", "35:1", "36:1", "37:1", "38:1", "39:1", "40:1", "Limit"
            })]
        public int LM_Ratio { get; set; }

        [ParameterDecl(
            Name        = "LM Attack",
            Description = "LM band attack time, log-mapped 0.1 ms to 200 ms",
            MinValue = 0, MaxValue = 127, DefValue = 77,
            ValueDescriptions = new[] {
                "0.100 ms", "0.106 ms", "0.113 ms", "0.120 ms", "0.127 ms", "0.135 ms", "0.143 ms", "0.152 ms",
                "0.161 ms", "0.171 ms", "0.182 ms", "0.193 ms", "0.205 ms", "0.218 ms", "0.231 ms", "0.245 ms",
                "0.261 ms", "0.277 ms", "0.294 ms", "0.312 ms", "0.331 ms", "0.351 ms", "0.373 ms", "0.396 ms",
                "0.421 ms", "0.446 ms", "0.474 ms", "0.503 ms", "0.534 ms", "0.567 ms", "0.602 ms", "0.639 ms",
                "0.679 ms", "0.721 ms", "0.765 ms", "0.812 ms", "0.862 ms", "0.916 ms", "0.972 ms", "1.03 ms",
                "1.10 ms", "1.16 ms", "1.24 ms", "1.31 ms", "1.39 ms", "1.48 ms", "1.57 ms", "1.67 ms",
                "1.77 ms", "1.88 ms", "1.99 ms", "2.12 ms", "2.25 ms", "2.39 ms", "2.53 ms", "2.69 ms",
                "2.85 ms", "3.03 ms", "3.22 ms", "3.42 ms", "3.63 ms", "3.85 ms", "4.09 ms", "4.34 ms",
                "4.61 ms", "4.89 ms", "5.19 ms", "5.51 ms", "5.85 ms", "6.22 ms", "6.60 ms", "7.01 ms",
                "7.44 ms", "7.90 ms", "8.38 ms", "8.90 ms", "9.45 ms", "10.0 ms", "10.7 ms", "11.3 ms",
                "12.0 ms", "12.7 ms", "13.5 ms", "14.4 ms", "15.3 ms", "16.2 ms", "17.2 ms", "18.3 ms",
                "19.4 ms", "20.6 ms", "21.8 ms", "23.2 ms", "24.6 ms", "26.1 ms", "27.8 ms", "29.5 ms",
                "31.3 ms", "33.2 ms", "35.3 ms", "37.4 ms", "39.7 ms", "42.2 ms", "44.8 ms", "47.6 ms",
                "50.5 ms", "53.6 ms", "56.9 ms", "60.4 ms", "64.1 ms", "68.1 ms", "72.3 ms", "76.8 ms",
                "81.5 ms", "86.5 ms", "91.9 ms", "97.5 ms", "104 ms", "110 ms", "117 ms", "124 ms",
                "132 ms", "140 ms", "148 ms", "157 ms", "167 ms", "177 ms", "188 ms", "200 ms"
            })]
        public int LM_Attack { get; set; }

        [ParameterDecl(
            Name        = "LM Release",
            Description = "LM band release time, log-mapped 1 ms to 2000 ms",
            MinValue = 0, MaxValue = 127, DefValue = 77,
            ValueDescriptions = new[] {
                "1.00 ms", "1.06 ms", "1.13 ms", "1.20 ms", "1.27 ms", "1.35 ms", "1.43 ms", "1.52 ms",
                "1.61 ms", "1.71 ms", "1.82 ms", "1.93 ms", "2.05 ms", "2.18 ms", "2.31 ms", "2.45 ms",
                "2.61 ms", "2.77 ms", "2.94 ms", "3.12 ms", "3.31 ms", "3.51 ms", "3.73 ms", "3.96 ms",
                "4.21 ms", "4.46 ms", "4.74 ms", "5.03 ms", "5.34 ms", "5.67 ms", "6.02 ms", "6.39 ms",
                "6.79 ms", "7.21 ms", "7.65 ms", "8.12 ms", "8.62 ms", "9.16 ms", "9.72 ms", "10.3 ms",
                "11.0 ms", "11.6 ms", "12.4 ms", "13.1 ms", "13.9 ms", "14.8 ms", "15.7 ms", "16.7 ms",
                "17.7 ms", "18.8 ms", "19.9 ms", "21.2 ms", "22.5 ms", "23.9 ms", "25.3 ms", "26.9 ms",
                "28.5 ms", "30.3 ms", "32.2 ms", "34.2 ms", "36.3 ms", "38.5 ms", "40.9 ms", "43.4 ms",
                "46.1 ms", "48.9 ms", "51.9 ms", "55.1 ms", "58.5 ms", "62.2 ms", "66.0 ms", "70.1 ms",
                "74.4 ms", "79.0 ms", "83.8 ms", "89.0 ms", "94.5 ms", "100 ms", "107 ms", "113 ms",
                "120 ms", "127 ms", "135 ms", "144 ms", "153 ms", "162 ms", "172 ms", "183 ms",
                "194 ms", "206 ms", "218 ms", "232 ms", "246 ms", "261 ms", "278 ms", "295 ms",
                "313 ms", "332 ms", "353 ms", "374 ms", "397 ms", "422 ms", "448 ms", "476 ms",
                "505 ms", "536 ms", "569 ms", "604 ms", "641 ms", "681 ms", "723 ms", "768 ms",
                "815 ms", "865 ms", "919 ms", "975 ms", "1035 ms", "1099 ms", "1167 ms", "1239 ms",
                "1315 ms", "1397 ms", "1483 ms", "1574 ms", "1671 ms", "1774 ms", "1884 ms", "2000 ms"
            })]
        public int LM_Release { get; set; }

        [ParameterDecl(
            Name        = "LM Makeup",
            Description = "LM band makeup gain in dB",
            MinValue = 0, MaxValue = 24, DefValue = 0,
            ValueDescriptions = new[] {
                "0.0 dB", "1.0 dB", "2.0 dB", "3.0 dB", "4.0 dB", "5.0 dB", "6.0 dB", "7.0 dB",
                "8.0 dB", "9.0 dB", "10.0 dB", "11.0 dB", "12.0 dB", "13.0 dB", "14.0 dB", "15.0 dB",
                "16.0 dB", "17.0 dB", "18.0 dB", "19.0 dB", "20.0 dB", "21.0 dB", "22.0 dB", "23.0 dB",
                "24.0 dB"
            })]
        public int LM_Makeup { get; set; }

        [ParameterDecl(
            Name        = "LM Bypass",
            Description = "Bypass compression on the LM band; band still routes through the crossover",
            MinValue = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int LM_Bypass { get; set; }

        [ParameterDecl(
            Name        = "HM Threshold",
            Description = "HM band threshold in dB below 0 dBFS",
            MinValue = 0, MaxValue = 60, DefValue = 18,
            ValueDescriptions = new[] {
                "0.0 dB", "-1.0 dB", "-2.0 dB", "-3.0 dB", "-4.0 dB", "-5.0 dB", "-6.0 dB", "-7.0 dB",
                "-8.0 dB", "-9.0 dB", "-10.0 dB", "-11.0 dB", "-12.0 dB", "-13.0 dB", "-14.0 dB", "-15.0 dB",
                "-16.0 dB", "-17.0 dB", "-18.0 dB", "-19.0 dB", "-20.0 dB", "-21.0 dB", "-22.0 dB", "-23.0 dB",
                "-24.0 dB", "-25.0 dB", "-26.0 dB", "-27.0 dB", "-28.0 dB", "-29.0 dB", "-30.0 dB", "-31.0 dB",
                "-32.0 dB", "-33.0 dB", "-34.0 dB", "-35.0 dB", "-36.0 dB", "-37.0 dB", "-38.0 dB", "-39.0 dB",
                "-40.0 dB", "-41.0 dB", "-42.0 dB", "-43.0 dB", "-44.0 dB", "-45.0 dB", "-46.0 dB", "-47.0 dB",
                "-48.0 dB", "-49.0 dB", "-50.0 dB", "-51.0 dB", "-52.0 dB", "-53.0 dB", "-54.0 dB", "-55.0 dB",
                "-56.0 dB", "-57.0 dB", "-58.0 dB", "-59.0 dB", "-60.0 dB"
            })]
        public int HM_Threshold { get; set; }

        [ParameterDecl(
            Name        = "HM Ratio",
            Description = "HM band ratio, log-mapped 1:1 to 40:1; top value is Limit",
            MinValue = 0, MaxValue = 127, DefValue = 31,
            ValueDescriptions = new[] {
                "1.00:1", "1.03:1", "1.06:1", "1.09:1", "1.12:1", "1.16:1", "1.19:1", "1.23:1",
                "1.26:1", "1.30:1", "1.34:1", "1.38:1", "1.42:1", "1.46:1", "1.51:1", "1.55:1",
                "1.60:1", "1.64:1", "1.69:1", "1.74:1", "1.80:1", "1.85:1", "1.90:1", "1.96:1",
                "2.0:1", "2.1:1", "2.1:1", "2.2:1", "2.3:1", "2.3:1", "2.4:1", "2.5:1",
                "2.6:1", "2.6:1", "2.7:1", "2.8:1", "2.9:1", "3.0:1", "3.0:1", "3.1:1",
                "3.2:1", "3.3:1", "3.4:1", "3.5:1", "3.6:1", "3.7:1", "3.8:1", "4.0:1",
                "4.1:1", "4.2:1", "4.3:1", "4.5:1", "4.6:1", "4.7:1", "4.9:1", "5.0:1",
                "5.2:1", "5.3:1", "5.5:1", "5.6:1", "5.8:1", "6.0:1", "6.1:1", "6.3:1",
                "6.5:1", "6.7:1", "6.9:1", "7.1:1", "7.3:1", "7.5:1", "7.8:1", "8.0:1",
                "8.2:1", "8.5:1", "8.7:1", "9.0:1", "9.3:1", "9.5:1", "9.8:1", "10:1",
                "10:1", "11:1", "11:1", "11:1", "12:1", "12:1", "12:1", "13:1",
                "13:1", "14:1", "14:1", "14:1", "15:1", "15:1", "16:1", "16:1",
                "17:1", "17:1", "18:1", "18:1", "19:1", "19:1", "20:1", "20:1",
                "21:1", "22:1", "22:1", "23:1", "24:1", "24:1", "25:1", "26:1",
                "27:1", "27:1", "28:1", "29:1", "30:1", "31:1", "32:1", "33:1",
                "34:1", "35:1", "36:1", "37:1", "38:1", "39:1", "40:1", "Limit"
            })]
        public int HM_Ratio { get; set; }

        [ParameterDecl(
            Name        = "HM Attack",
            Description = "HM band attack time, log-mapped 0.1 ms to 200 ms",
            MinValue = 0, MaxValue = 127, DefValue = 65,
            ValueDescriptions = new[] {
                "0.100 ms", "0.106 ms", "0.113 ms", "0.120 ms", "0.127 ms", "0.135 ms", "0.143 ms", "0.152 ms",
                "0.161 ms", "0.171 ms", "0.182 ms", "0.193 ms", "0.205 ms", "0.218 ms", "0.231 ms", "0.245 ms",
                "0.261 ms", "0.277 ms", "0.294 ms", "0.312 ms", "0.331 ms", "0.351 ms", "0.373 ms", "0.396 ms",
                "0.421 ms", "0.446 ms", "0.474 ms", "0.503 ms", "0.534 ms", "0.567 ms", "0.602 ms", "0.639 ms",
                "0.679 ms", "0.721 ms", "0.765 ms", "0.812 ms", "0.862 ms", "0.916 ms", "0.972 ms", "1.03 ms",
                "1.10 ms", "1.16 ms", "1.24 ms", "1.31 ms", "1.39 ms", "1.48 ms", "1.57 ms", "1.67 ms",
                "1.77 ms", "1.88 ms", "1.99 ms", "2.12 ms", "2.25 ms", "2.39 ms", "2.53 ms", "2.69 ms",
                "2.85 ms", "3.03 ms", "3.22 ms", "3.42 ms", "3.63 ms", "3.85 ms", "4.09 ms", "4.34 ms",
                "4.61 ms", "4.89 ms", "5.19 ms", "5.51 ms", "5.85 ms", "6.22 ms", "6.60 ms", "7.01 ms",
                "7.44 ms", "7.90 ms", "8.38 ms", "8.90 ms", "9.45 ms", "10.0 ms", "10.7 ms", "11.3 ms",
                "12.0 ms", "12.7 ms", "13.5 ms", "14.4 ms", "15.3 ms", "16.2 ms", "17.2 ms", "18.3 ms",
                "19.4 ms", "20.6 ms", "21.8 ms", "23.2 ms", "24.6 ms", "26.1 ms", "27.8 ms", "29.5 ms",
                "31.3 ms", "33.2 ms", "35.3 ms", "37.4 ms", "39.7 ms", "42.2 ms", "44.8 ms", "47.6 ms",
                "50.5 ms", "53.6 ms", "56.9 ms", "60.4 ms", "64.1 ms", "68.1 ms", "72.3 ms", "76.8 ms",
                "81.5 ms", "86.5 ms", "91.9 ms", "97.5 ms", "104 ms", "110 ms", "117 ms", "124 ms",
                "132 ms", "140 ms", "148 ms", "157 ms", "167 ms", "177 ms", "188 ms", "200 ms"
            })]
        public int HM_Attack { get; set; }

        [ParameterDecl(
            Name        = "HM Release",
            Description = "HM band release time, log-mapped 1 ms to 2000 ms",
            MinValue = 0, MaxValue = 127, DefValue = 65,
            ValueDescriptions = new[] {
                "1.00 ms", "1.06 ms", "1.13 ms", "1.20 ms", "1.27 ms", "1.35 ms", "1.43 ms", "1.52 ms",
                "1.61 ms", "1.71 ms", "1.82 ms", "1.93 ms", "2.05 ms", "2.18 ms", "2.31 ms", "2.45 ms",
                "2.61 ms", "2.77 ms", "2.94 ms", "3.12 ms", "3.31 ms", "3.51 ms", "3.73 ms", "3.96 ms",
                "4.21 ms", "4.46 ms", "4.74 ms", "5.03 ms", "5.34 ms", "5.67 ms", "6.02 ms", "6.39 ms",
                "6.79 ms", "7.21 ms", "7.65 ms", "8.12 ms", "8.62 ms", "9.16 ms", "9.72 ms", "10.3 ms",
                "11.0 ms", "11.6 ms", "12.4 ms", "13.1 ms", "13.9 ms", "14.8 ms", "15.7 ms", "16.7 ms",
                "17.7 ms", "18.8 ms", "19.9 ms", "21.2 ms", "22.5 ms", "23.9 ms", "25.3 ms", "26.9 ms",
                "28.5 ms", "30.3 ms", "32.2 ms", "34.2 ms", "36.3 ms", "38.5 ms", "40.9 ms", "43.4 ms",
                "46.1 ms", "48.9 ms", "51.9 ms", "55.1 ms", "58.5 ms", "62.2 ms", "66.0 ms", "70.1 ms",
                "74.4 ms", "79.0 ms", "83.8 ms", "89.0 ms", "94.5 ms", "100 ms", "107 ms", "113 ms",
                "120 ms", "127 ms", "135 ms", "144 ms", "153 ms", "162 ms", "172 ms", "183 ms",
                "194 ms", "206 ms", "218 ms", "232 ms", "246 ms", "261 ms", "278 ms", "295 ms",
                "313 ms", "332 ms", "353 ms", "374 ms", "397 ms", "422 ms", "448 ms", "476 ms",
                "505 ms", "536 ms", "569 ms", "604 ms", "641 ms", "681 ms", "723 ms", "768 ms",
                "815 ms", "865 ms", "919 ms", "975 ms", "1035 ms", "1099 ms", "1167 ms", "1239 ms",
                "1315 ms", "1397 ms", "1483 ms", "1574 ms", "1671 ms", "1774 ms", "1884 ms", "2000 ms"
            })]
        public int HM_Release { get; set; }

        [ParameterDecl(
            Name        = "HM Makeup",
            Description = "HM band makeup gain in dB",
            MinValue = 0, MaxValue = 24, DefValue = 0,
            ValueDescriptions = new[] {
                "0.0 dB", "1.0 dB", "2.0 dB", "3.0 dB", "4.0 dB", "5.0 dB", "6.0 dB", "7.0 dB",
                "8.0 dB", "9.0 dB", "10.0 dB", "11.0 dB", "12.0 dB", "13.0 dB", "14.0 dB", "15.0 dB",
                "16.0 dB", "17.0 dB", "18.0 dB", "19.0 dB", "20.0 dB", "21.0 dB", "22.0 dB", "23.0 dB",
                "24.0 dB"
            })]
        public int HM_Makeup { get; set; }

        [ParameterDecl(
            Name        = "HM Bypass",
            Description = "Bypass compression on the HM band; band still routes through the crossover",
            MinValue = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int HM_Bypass { get; set; }

        [ParameterDecl(
            Name        = "H Threshold",
            Description = "H band threshold in dB below 0 dBFS",
            MinValue = 0, MaxValue = 60, DefValue = 18,
            ValueDescriptions = new[] {
                "0.0 dB", "-1.0 dB", "-2.0 dB", "-3.0 dB", "-4.0 dB", "-5.0 dB", "-6.0 dB", "-7.0 dB",
                "-8.0 dB", "-9.0 dB", "-10.0 dB", "-11.0 dB", "-12.0 dB", "-13.0 dB", "-14.0 dB", "-15.0 dB",
                "-16.0 dB", "-17.0 dB", "-18.0 dB", "-19.0 dB", "-20.0 dB", "-21.0 dB", "-22.0 dB", "-23.0 dB",
                "-24.0 dB", "-25.0 dB", "-26.0 dB", "-27.0 dB", "-28.0 dB", "-29.0 dB", "-30.0 dB", "-31.0 dB",
                "-32.0 dB", "-33.0 dB", "-34.0 dB", "-35.0 dB", "-36.0 dB", "-37.0 dB", "-38.0 dB", "-39.0 dB",
                "-40.0 dB", "-41.0 dB", "-42.0 dB", "-43.0 dB", "-44.0 dB", "-45.0 dB", "-46.0 dB", "-47.0 dB",
                "-48.0 dB", "-49.0 dB", "-50.0 dB", "-51.0 dB", "-52.0 dB", "-53.0 dB", "-54.0 dB", "-55.0 dB",
                "-56.0 dB", "-57.0 dB", "-58.0 dB", "-59.0 dB", "-60.0 dB"
            })]
        public int H_Threshold { get; set; }

        [ParameterDecl(
            Name        = "H Ratio",
            Description = "H band ratio, log-mapped 1:1 to 40:1; top value is Limit",
            MinValue = 0, MaxValue = 127, DefValue = 31,
            ValueDescriptions = new[] {
                "1.00:1", "1.03:1", "1.06:1", "1.09:1", "1.12:1", "1.16:1", "1.19:1", "1.23:1",
                "1.26:1", "1.30:1", "1.34:1", "1.38:1", "1.42:1", "1.46:1", "1.51:1", "1.55:1",
                "1.60:1", "1.64:1", "1.69:1", "1.74:1", "1.80:1", "1.85:1", "1.90:1", "1.96:1",
                "2.0:1", "2.1:1", "2.1:1", "2.2:1", "2.3:1", "2.3:1", "2.4:1", "2.5:1",
                "2.6:1", "2.6:1", "2.7:1", "2.8:1", "2.9:1", "3.0:1", "3.0:1", "3.1:1",
                "3.2:1", "3.3:1", "3.4:1", "3.5:1", "3.6:1", "3.7:1", "3.8:1", "4.0:1",
                "4.1:1", "4.2:1", "4.3:1", "4.5:1", "4.6:1", "4.7:1", "4.9:1", "5.0:1",
                "5.2:1", "5.3:1", "5.5:1", "5.6:1", "5.8:1", "6.0:1", "6.1:1", "6.3:1",
                "6.5:1", "6.7:1", "6.9:1", "7.1:1", "7.3:1", "7.5:1", "7.8:1", "8.0:1",
                "8.2:1", "8.5:1", "8.7:1", "9.0:1", "9.3:1", "9.5:1", "9.8:1", "10:1",
                "10:1", "11:1", "11:1", "11:1", "12:1", "12:1", "12:1", "13:1",
                "13:1", "14:1", "14:1", "14:1", "15:1", "15:1", "16:1", "16:1",
                "17:1", "17:1", "18:1", "18:1", "19:1", "19:1", "20:1", "20:1",
                "21:1", "22:1", "22:1", "23:1", "24:1", "24:1", "25:1", "26:1",
                "27:1", "27:1", "28:1", "29:1", "30:1", "31:1", "32:1", "33:1",
                "34:1", "35:1", "36:1", "37:1", "38:1", "39:1", "40:1", "Limit"
            })]
        public int H_Ratio { get; set; }

        [ParameterDecl(
            Name        = "H Attack",
            Description = "H band attack time, log-mapped 0.1 ms to 200 ms",
            MinValue = 0, MaxValue = 127, DefValue = 57,
            ValueDescriptions = new[] {
                "0.100 ms", "0.106 ms", "0.113 ms", "0.120 ms", "0.127 ms", "0.135 ms", "0.143 ms", "0.152 ms",
                "0.161 ms", "0.171 ms", "0.182 ms", "0.193 ms", "0.205 ms", "0.218 ms", "0.231 ms", "0.245 ms",
                "0.261 ms", "0.277 ms", "0.294 ms", "0.312 ms", "0.331 ms", "0.351 ms", "0.373 ms", "0.396 ms",
                "0.421 ms", "0.446 ms", "0.474 ms", "0.503 ms", "0.534 ms", "0.567 ms", "0.602 ms", "0.639 ms",
                "0.679 ms", "0.721 ms", "0.765 ms", "0.812 ms", "0.862 ms", "0.916 ms", "0.972 ms", "1.03 ms",
                "1.10 ms", "1.16 ms", "1.24 ms", "1.31 ms", "1.39 ms", "1.48 ms", "1.57 ms", "1.67 ms",
                "1.77 ms", "1.88 ms", "1.99 ms", "2.12 ms", "2.25 ms", "2.39 ms", "2.53 ms", "2.69 ms",
                "2.85 ms", "3.03 ms", "3.22 ms", "3.42 ms", "3.63 ms", "3.85 ms", "4.09 ms", "4.34 ms",
                "4.61 ms", "4.89 ms", "5.19 ms", "5.51 ms", "5.85 ms", "6.22 ms", "6.60 ms", "7.01 ms",
                "7.44 ms", "7.90 ms", "8.38 ms", "8.90 ms", "9.45 ms", "10.0 ms", "10.7 ms", "11.3 ms",
                "12.0 ms", "12.7 ms", "13.5 ms", "14.4 ms", "15.3 ms", "16.2 ms", "17.2 ms", "18.3 ms",
                "19.4 ms", "20.6 ms", "21.8 ms", "23.2 ms", "24.6 ms", "26.1 ms", "27.8 ms", "29.5 ms",
                "31.3 ms", "33.2 ms", "35.3 ms", "37.4 ms", "39.7 ms", "42.2 ms", "44.8 ms", "47.6 ms",
                "50.5 ms", "53.6 ms", "56.9 ms", "60.4 ms", "64.1 ms", "68.1 ms", "72.3 ms", "76.8 ms",
                "81.5 ms", "86.5 ms", "91.9 ms", "97.5 ms", "104 ms", "110 ms", "117 ms", "124 ms",
                "132 ms", "140 ms", "148 ms", "157 ms", "167 ms", "177 ms", "188 ms", "200 ms"
            })]
        public int H_Attack { get; set; }

        [ParameterDecl(
            Name        = "H Release",
            Description = "H band release time, log-mapped 1 ms to 2000 ms",
            MinValue = 0, MaxValue = 127, DefValue = 57,
            ValueDescriptions = new[] {
                "1.00 ms", "1.06 ms", "1.13 ms", "1.20 ms", "1.27 ms", "1.35 ms", "1.43 ms", "1.52 ms",
                "1.61 ms", "1.71 ms", "1.82 ms", "1.93 ms", "2.05 ms", "2.18 ms", "2.31 ms", "2.45 ms",
                "2.61 ms", "2.77 ms", "2.94 ms", "3.12 ms", "3.31 ms", "3.51 ms", "3.73 ms", "3.96 ms",
                "4.21 ms", "4.46 ms", "4.74 ms", "5.03 ms", "5.34 ms", "5.67 ms", "6.02 ms", "6.39 ms",
                "6.79 ms", "7.21 ms", "7.65 ms", "8.12 ms", "8.62 ms", "9.16 ms", "9.72 ms", "10.3 ms",
                "11.0 ms", "11.6 ms", "12.4 ms", "13.1 ms", "13.9 ms", "14.8 ms", "15.7 ms", "16.7 ms",
                "17.7 ms", "18.8 ms", "19.9 ms", "21.2 ms", "22.5 ms", "23.9 ms", "25.3 ms", "26.9 ms",
                "28.5 ms", "30.3 ms", "32.2 ms", "34.2 ms", "36.3 ms", "38.5 ms", "40.9 ms", "43.4 ms",
                "46.1 ms", "48.9 ms", "51.9 ms", "55.1 ms", "58.5 ms", "62.2 ms", "66.0 ms", "70.1 ms",
                "74.4 ms", "79.0 ms", "83.8 ms", "89.0 ms", "94.5 ms", "100 ms", "107 ms", "113 ms",
                "120 ms", "127 ms", "135 ms", "144 ms", "153 ms", "162 ms", "172 ms", "183 ms",
                "194 ms", "206 ms", "218 ms", "232 ms", "246 ms", "261 ms", "278 ms", "295 ms",
                "313 ms", "332 ms", "353 ms", "374 ms", "397 ms", "422 ms", "448 ms", "476 ms",
                "505 ms", "536 ms", "569 ms", "604 ms", "641 ms", "681 ms", "723 ms", "768 ms",
                "815 ms", "865 ms", "919 ms", "975 ms", "1035 ms", "1099 ms", "1167 ms", "1239 ms",
                "1315 ms", "1397 ms", "1483 ms", "1574 ms", "1671 ms", "1774 ms", "1884 ms", "2000 ms"
            })]
        public int H_Release { get; set; }

        [ParameterDecl(
            Name        = "H Makeup",
            Description = "H band makeup gain in dB",
            MinValue = 0, MaxValue = 24, DefValue = 0,
            ValueDescriptions = new[] {
                "0.0 dB", "1.0 dB", "2.0 dB", "3.0 dB", "4.0 dB", "5.0 dB", "6.0 dB", "7.0 dB",
                "8.0 dB", "9.0 dB", "10.0 dB", "11.0 dB", "12.0 dB", "13.0 dB", "14.0 dB", "15.0 dB",
                "16.0 dB", "17.0 dB", "18.0 dB", "19.0 dB", "20.0 dB", "21.0 dB", "22.0 dB", "23.0 dB",
                "24.0 dB"
            })]
        public int H_Makeup { get; set; }

        [ParameterDecl(
            Name        = "H Bypass",
            Description = "Bypass compression on the H band; band still routes through the crossover",
            MinValue = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int H_Bypass { get; set; }

        [ParameterDecl(
            Name        = "Output Gain",
            Description = "Global output gain after summation, in dB; index 24 is unity",
            MinValue = 0, MaxValue = 48, DefValue = 24,
            ValueDescriptions = new[] {
                "-24.0 dB", "-23.0 dB", "-22.0 dB", "-21.0 dB", "-20.0 dB", "-19.0 dB", "-18.0 dB", "-17.0 dB",
                "-16.0 dB", "-15.0 dB", "-14.0 dB", "-13.0 dB", "-12.0 dB", "-11.0 dB", "-10.0 dB", "-9.0 dB",
                "-8.0 dB", "-7.0 dB", "-6.0 dB", "-5.0 dB", "-4.0 dB", "-3.0 dB", "-2.0 dB", "-1.0 dB",
                "0.0 dB", "+1.0 dB", "+2.0 dB", "+3.0 dB", "+4.0 dB", "+5.0 dB", "+6.0 dB", "+7.0 dB",
                "+8.0 dB", "+9.0 dB", "+10.0 dB", "+11.0 dB", "+12.0 dB", "+13.0 dB", "+14.0 dB", "+15.0 dB",
                "+16.0 dB", "+17.0 dB", "+18.0 dB", "+19.0 dB", "+20.0 dB", "+21.0 dB", "+22.0 dB", "+23.0 dB",
                "+24.0 dB"
            })]
        public int OutputGain { get; set; }

        [ParameterDecl(
            Name        = "Dry-Wet",
            Description = "Mix between dry input (0) and compressed output (100)",
            MinValue = 0, MaxValue = 100, DefValue = 100,
            ValueDescriptions = new[] {
                "0 %", "1 %", "2 %", "3 %", "4 %", "5 %", "6 %", "7 %",
                "8 %", "9 %", "10 %", "11 %", "12 %", "13 %", "14 %", "15 %",
                "16 %", "17 %", "18 %", "19 %", "20 %", "21 %", "22 %", "23 %",
                "24 %", "25 %", "26 %", "27 %", "28 %", "29 %", "30 %", "31 %",
                "32 %", "33 %", "34 %", "35 %", "36 %", "37 %", "38 %", "39 %",
                "40 %", "41 %", "42 %", "43 %", "44 %", "45 %", "46 %", "47 %",
                "48 %", "49 %", "50 %", "51 %", "52 %", "53 %", "54 %", "55 %",
                "56 %", "57 %", "58 %", "59 %", "60 %", "61 %", "62 %", "63 %",
                "64 %", "65 %", "66 %", "67 %", "68 %", "69 %", "70 %", "71 %",
                "72 %", "73 %", "74 %", "75 %", "76 %", "77 %", "78 %", "79 %",
                "80 %", "81 %", "82 %", "83 %", "84 %", "85 %", "86 %", "87 %",
                "88 %", "89 %", "90 %", "91 %", "92 %", "93 %", "94 %", "95 %",
                "96 %", "97 %", "98 %", "99 %", "100 %"
            })]
        public int DryWet { get; set; }


        // ─────────────────────────────────────────────────────────────────
        //  v1.3 additions — appended per Build §3.3, defaults preserve
        //  v1.2 behaviour so the 20 presets are bit-identical without
        //  these toggles.
        // ─────────────────────────────────────────────────────────────────

        [ParameterDecl(
            Name = "Lookahead",
            Description = "Pre-delays audio so the compressor reacts before transients reach the output. Adds equivalent processing latency — comb-filtering occurs if a parallel dry path is in use. Off = zero-latency.",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            ValueDescriptions = new[] {
                "Off", "0.10 ms", "0.10 ms", "0.11 ms", "0.11 ms", "0.12 ms", "0.12 ms", "0.12 ms",
                "0.13 ms", "0.13 ms", "0.14 ms", "0.14 ms", "0.15 ms", "0.16 ms", "0.16 ms", "0.17 ms",
                "0.17 ms", "0.18 ms", "0.19 ms", "0.19 ms", "0.20 ms", "0.21 ms", "0.22 ms", "0.22 ms",
                "0.23 ms", "0.24 ms", "0.25 ms", "0.26 ms", "0.27 ms", "0.28 ms", "0.29 ms", "0.30 ms",
                "0.31 ms", "0.32 ms", "0.33 ms", "0.35 ms", "0.36 ms", "0.37 ms", "0.39 ms", "0.40 ms",
                "0.42 ms", "0.43 ms", "0.45 ms", "0.46 ms", "0.48 ms", "0.50 ms", "0.52 ms", "0.54 ms",
                "0.56 ms", "0.58 ms", "0.60 ms", "0.62 ms", "0.64 ms", "0.67 ms", "0.69 ms", "0.72 ms",
                "0.75 ms", "0.77 ms", "0.80 ms", "0.83 ms", "0.86 ms", "0.90 ms", "0.93 ms", "0.96 ms",
                "1.00 ms", "1.04 ms", "1.08 ms", "1.12 ms", "1.16 ms", "1.20 ms", "1.25 ms", "1.29 ms",
                "1.34 ms", "1.39 ms", "1.44 ms", "1.49 ms", "1.55 ms", "1.61 ms", "1.67 ms", "1.73 ms",
                "1.79 ms", "1.86 ms", "1.93 ms", "2.00 ms", "2.08 ms", "2.15 ms", "2.23 ms", "2.32 ms",
                "2.40 ms", "2.49 ms", "2.59 ms", "2.68 ms", "2.78 ms", "2.89 ms", "2.99 ms", "3.11 ms",
                "3.22 ms", "3.34 ms", "3.46 ms", "3.59 ms", "3.73 ms", "3.87 ms", "4.01 ms", "4.16 ms",
                "4.31 ms", "4.48 ms", "4.64 ms", "4.81 ms", "4.99 ms", "5.18 ms", "5.37 ms", "5.57 ms",
                "5.78 ms", "5.99 ms", "6.22 ms", "6.45 ms", "6.69 ms", "6.94 ms", "7.20 ms", "7.46 ms",
                "7.74 ms", "8.03 ms", "8.33 ms", "8.64 ms", "8.96 ms", "9.30 ms", "9.64 ms", "10.0 ms"
            })]
        public int Lookahead { get; set; }

        [ParameterDecl(
            Name = "Phase Linear",
            Description = "Adds all-pass compensation so the four bands sum phase-coherently at crossovers. Adds a small fixed group delay (1-3 ms depending on crossover frequencies).",
            MinValue = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int PhaseLinear { get; set; }

        [ParameterDecl(
            Name = "Spectrum View",
            Description = "Show a real-time spectrum analyser below the OUT meter, showing the post-effect output signal. Does not affect audio.",
            MinValue = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int SpectrumView { get; set; }


        // ─────────────────────────────────────────────────────────────────
        //  Conversion helpers (parameter int → DSP float)
        // ─────────────────────────────────────────────────────────────────

        // Log-map [0..127] → [lo..hi] Hz. Must match log_range() in
        // gen_param_labels.py over the same bounds.
        private static float MapHzLog(int v, float loHz, float hiHz)
        {
            float t = v / 127f;
            return loHz * MathF.Pow(hiHz / loHz, t);
        }

        // Log-map [0..127] → [lo..hi] ms. Same formula as MapHzLog, kept
        // separate for clarity at call sites.
        private static float MapMsLog(int v, float loMs, float hiMs)
        {
            float t = v / 127f;
            return loMs * MathF.Pow(hiMs / loMs, t);
        }

        // Log-map ratio with explicit infinity sentinel at the top.
        // Indices 0..126 cover 1.0..40.0; index 127 = ∞:1 (Limit).
        private static float MapRatio(int v)
        {
            if (v >= 127) return RATIO_LIMIT_VALUE;
            float t = v / RATIO_MAX_INDEX;
            return MathF.Pow(RATIO_MAX, t);
        }

        // ─────────────────────────────────────────────────────────────────
        //  Work() — per-buffer audio processing
        // ─────────────────────────────────────────────────────────────────

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // MasterInfo.SamplesPerSec returns float (Core §29). Cast to int
            // for our coefficient calculations — sample rates are universally
            // integral in practice (44100, 48000, 96000), and the cache
            // dirty-check in each filter's UpdateCoefs naturally picks up
            // any runtime rate change via the (sr != _cachedSr) test.
            int sr = (int)(_host?.MasterInfo?.SamplesPerSec ?? 44100f);
            if (sr <= 0) sr = 44100;

            // ── Update crossover coefficients (cached, cheap when stable) ──
            float fc1 = MapHzLog(XoverLLM,  XO1_MIN_HZ, XO1_MAX_HZ);
            float fc2 = MapHzLog(XoverLMHM, XO2_MIN_HZ, XO2_MAX_HZ);
            float fc3 = MapHzLog(XoverHMH,  XO3_MIN_HZ, XO3_MAX_HZ);

            // Defensive ordering: even though per-crossover ranges don't
            // overlap by design, runtime modulation could push them into
            // odd configurations. Enforce a ½-octave minimum spacing.
            if (fc1 > fc2 * 0.707f) fc1 = fc2 * 0.707f;
            if (fc3 < fc2 * 1.414f) fc3 = fc2 * 1.414f;

            _xoMidLP.UpdateCoefs(fc2, sr);
            _xoMidHP.UpdateCoefs(fc2, sr);
            _xoLowLP.UpdateCoefs(fc1, sr);
            _xoLowHP.UpdateCoefs(fc1, sr);
            _xoHighLP.UpdateCoefs(fc3, sr);
            _xoHighHP.UpdateCoefs(fc3, sr);

            // ── Phase-linear all-pass coefficient update ──────────────
            // Each branch is corrected with the OPPOSITE branch's
            // crossover Fc, so all four bands share the same phase
            // response after compensation.
            bool phaseLinear = PhaseLinear != 0;
            if (phaseLinear)
            {
                _apHighOnLowBranch.UpdateCoefs(fc3, sr);   // AP@high on low branch
                _apLowOnHighBranch.UpdateCoefs(fc1, sr);   // AP@low on high branch
            }

            // ── Lookahead samples ──────────────────────────────────────
            // Parameter idx 0 = Off → samples 0 (zero-latency fast path).
            // idx 1..127 log-mapped 0.1..10 ms → convert to samples for sr.
            int lookaheadSamples = 0;
            if (Lookahead > 0)
            {
                float ms = 0.1f * MathF.Pow(100f, (Lookahead - 1) / 126f);
                lookaheadSamples = (int)MathF.Round(ms * sr * 0.001f);
                // Clamp to delay-line capacity (2048 samples = ~10.7 ms at 192 kHz)
                if (lookaheadSamples > 2047) lookaheadSamples = 2047;
                if (lookaheadSamples < 0)    lookaheadSamples = 0;
            }

            bool spectrumOn = SpectrumView != 0;

            // ── Update band compressor coefficients (log-mapped ms) ──
            _bands[0].UpdateCoefs(sr,
                MapMsLog(L_Attack,  ATTACK_MIN_MS,  ATTACK_MAX_MS),
                MapMsLog(L_Release, RELEASE_MIN_MS, RELEASE_MAX_MS));
            _bands[1].UpdateCoefs(sr,
                MapMsLog(LM_Attack,  ATTACK_MIN_MS,  ATTACK_MAX_MS),
                MapMsLog(LM_Release, RELEASE_MIN_MS, RELEASE_MAX_MS));
            _bands[2].UpdateCoefs(sr,
                MapMsLog(HM_Attack,  ATTACK_MIN_MS,  ATTACK_MAX_MS),
                MapMsLog(HM_Release, RELEASE_MIN_MS, RELEASE_MAX_MS));
            _bands[3].UpdateCoefs(sr,
                MapMsLog(H_Attack,  ATTACK_MIN_MS,  ATTACK_MAX_MS),
                MapMsLog(H_Release, RELEASE_MIN_MS, RELEASE_MAX_MS));

            // ── Precompute per-band parameters as DSP floats ──
            _threshDb[0]  = -(float)L_Threshold;
            _threshDb[1]  = -(float)LM_Threshold;
            _threshDb[2]  = -(float)HM_Threshold;
            _threshDb[3]  = -(float)H_Threshold;

            _ratio[0]     = MapRatio(L_Ratio);
            _ratio[1]     = MapRatio(LM_Ratio);
            _ratio[2]     = MapRatio(HM_Ratio);
            _ratio[3]     = MapRatio(H_Ratio);

            _makeupLin[0] = FastMath.DbToLin(L_Makeup);
            _makeupLin[1] = FastMath.DbToLin(LM_Makeup);
            _makeupLin[2] = FastMath.DbToLin(HM_Makeup);
            _makeupLin[3] = FastMath.DbToLin(H_Makeup);

            _bypass[0]    = L_Bypass  != 0;
            _bypass[1]    = LM_Bypass != 0;
            _bypass[2]    = HM_Bypass != 0;
            _bypass[3]    = H_Bypass  != 0;

            float kneeDb  = Knee;
            bool  isRms   = Detection == 1;

            float outGainLin = FastMath.DbToLin(OutputGain - 24);   // bipolar offset
            float wet        = DryWet * 0.01f;
            float dry        = 1f - wet;
            int   listen     = Listen;

            // Output-meter envelope: ~100 ms release (-60 dB/sec falloff).
            // Attack is instantaneous (peak follows input upward immediately).
            float outRelCoef = MathF.Exp(-1f / (0.1f * sr));
            float outEnvL = _outEnvL;
            float outEnvR = _outEnvR;

            // ── Per-sample loop ──
            for (int i = 0; i < n; i++)
            {
                float dryL = input[i].L * SCALE;
                float dryR = input[i].R * SCALE;

                // Stage 1: split at the middle crossover (xover2).
                float halfLowL  = dryL, halfLowR  = dryR;
                _xoMidLP.Process(ref halfLowL, ref halfLowR);

                float halfHighL = dryL, halfHighR = dryR;
                _xoMidHP.Process(ref halfHighL, ref halfHighR);

                // Phase-linear correction: opposite-branch all-pass on each
                // half, so after this both halves have the same phase
                // response and sum coherently. Skip entirely when off.
                if (phaseLinear)
                {
                    _apHighOnLowBranch.Process(ref halfLowL,  ref halfLowR);
                    _apLowOnHighBranch.Process(ref halfHighL, ref halfHighR);
                }

                // Stage 2a: split low half at xover1 → L band + LM band.
                float lowL = halfLowL, lowR = halfLowR;
                _xoLowLP.Process(ref lowL, ref lowR);

                float lomidL = halfLowL, lomidR = halfLowR;
                _xoLowHP.Process(ref lomidL, ref lomidR);

                // Stage 2b: split high half at xover3 → HM band + H band.
                float himidL = halfHighL, himidR = halfHighR;
                _xoHighLP.Process(ref himidL, ref himidR);

                float highL = halfHighL, highR = halfHighR;
                _xoHighHP.Process(ref highL, ref highR);

                // Compress each band independently (stereo-linked detection).
                _bands[0].Process(ref lowL,   ref lowR,
                    _threshDb[0], _ratio[0], kneeDb, _makeupLin[0], isRms, _bypass[0], lookaheadSamples);
                _bands[1].Process(ref lomidL, ref lomidR,
                    _threshDb[1], _ratio[1], kneeDb, _makeupLin[1], isRms, _bypass[1], lookaheadSamples);
                _bands[2].Process(ref himidL, ref himidR,
                    _threshDb[2], _ratio[2], kneeDb, _makeupLin[2], isRms, _bypass[2], lookaheadSamples);
                _bands[3].Process(ref highL,  ref highR,
                    _threshDb[3], _ratio[3], kneeDb, _makeupLin[3], isRms, _bypass[3], lookaheadSamples);

                // Listen mode: All sums everything, else isolate one band.
                float wetL, wetR;
                switch (listen)
                {
                    case 1: wetL = lowL;   wetR = lowR;   break;
                    case 2: wetL = lomidL; wetR = lomidR; break;
                    case 3: wetL = himidL; wetR = himidR; break;
                    case 4: wetL = highL;  wetR = highR;  break;
                    default:
                        wetL = lowL + lomidL + himidL + highL;
                        wetR = lowR + lomidR + himidR + highR;
                        break;
                }

                wetL *= outGainLin;
                wetR *= outGainLin;

                // Dry-path delay alignment for lookahead. Without this the
                // dry/wet mix combs because dryL/R are live samples while
                // wetL/R are delayed by `lookaheadSamples` from inside
                // each BandCompressor. Skip entirely when lookahead is 0.
                float dryMixL, dryMixR;
                if (lookaheadSamples > 0)
                {
                    _dryDelayL[_dryDelayWriteIdx] = dryL;
                    _dryDelayR[_dryDelayWriteIdx] = dryR;
                    int readIdx = _dryDelayWriteIdx - lookaheadSamples;
                    if (readIdx < 0) readIdx += MAX_DRY_DELAY_SAMPLES;
                    dryMixL = _dryDelayL[readIdx];
                    dryMixR = _dryDelayR[readIdx];
                    _dryDelayWriteIdx++;
                    if (_dryDelayWriteIdx >= MAX_DRY_DELAY_SAMPLES) _dryDelayWriteIdx = 0;
                }
                else
                {
                    dryMixL = dryL;
                    dryMixR = dryR;
                }

                float outL = dryMixL * dry + wetL * wet;
                float outR = dryMixR * dry + wetR * wet;

                // Feed spectrum analyser from the POST-effect signal so
                // it shows the actual output content — including the
                // combined effect of band compression, Output Gain, and
                // Dry-Wet mix. Skipped entirely when SpectrumView is off.
                // The feed itself is cheap (ring buffer write + counter);
                // the FFT inside costs ~30 µs every ~33 ms.
                if (spectrumOn)
                    _spectrum.Feed((outL + outR) * 0.5f, sr);

                // Output-meter envelope: instant attack, smoothed release.
                // Peak detection on the post-everything output.
                float absL = MathF.Abs(outL);
                outEnvL = absL > outEnvL
                    ? absL
                    : outRelCoef * outEnvL + (1f - outRelCoef) * absL;
                float absR = MathF.Abs(outR);
                outEnvR = absR > outEnvR
                    ? absR
                    : outRelCoef * outEnvR + (1f - outRelCoef) * absR;
                // Denormal flush on release decay (Core §30): outEnvL/R are
                // ≥0 by construction so one comparison suffices.
                if (outEnvL < 1e-25f) outEnvL = 0f;
                if (outEnvR < 1e-25f) outEnvR = 0f;

                output[i] = new Sample(outL * INV_SCALE, outR * INV_SCALE);
            }

            // Persist envelope state across buffers, publish to UI via volatile.
            _outEnvL = outEnvL;
            _outEnvR = outEnvR;
            MeterOutLeftDb  = outEnvL > 1e-6f ? FastMath.LinToDb(outEnvL) : -120f;
            MeterOutRightDb = outEnvR > 1e-6f ? FastMath.LinToDb(outEnvR) : -120f;

            return true;
        }
    }
}
