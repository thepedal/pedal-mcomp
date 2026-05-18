// Crossover.cs — Linkwitz-Riley 4th-order lowpass and highpass filters.
//
// LR4 = squared Butterworth in magnitude response, which is implemented as
// two identical 2nd-order Butterworth biquads cascaded in series. Both
// biquads use Q = 1/sqrt(2) (the Butterworth quality factor).
//
// Split-API design per SH101 §3: UpdateCoefs runs at control rate (typically
// once per buffer), Process runs per sample. Coefficient cache short-circuits
// the no-modulation case per PedalComp §6.
//
// The 4-band binary-tree crossover topology uses 6 instances of this class:
//   - one LP + one HP at the mid crossover (xover LM-HM)
//   - one LP + one HP at the low crossover  (xover L-LM)  on the low half
//   - one LP + one HP at the high crossover (xover HM-H)  on the high half
//
// Every band passes through exactly two LR4 stages, so all four bands have
// matched group delay. Sum of all four bands is magnitude-flat for stationary
// signals (an all-pass phase correction would also make it phase-flat; that's
// deferred to v1.x).

using System;

namespace PedalMComp
{
    internal enum Lr4Mode
    {
        Lowpass,
        Highpass,
    }

    internal sealed class Lr4Filter
    {
        // Butterworth Q for a single 2nd-order section. Cascading two such
        // sections gives LR4 (Linkwitz-Riley 4th-order) magnitude response.
        private const float Q_BUTTERWORTH = 0.70710678f;   // 1/sqrt(2)

        private readonly Lr4Mode _mode;

        // Biquad coefficients (a0 normalised out). Shared between L/R channels.
        private float _b0, _b1, _b2, _a1, _a2;

        // Per-channel state for two cascaded biquads. DFII-T form: each biquad
        // has two state registers (s1, s2). Suffix a/b denotes first/second
        // section of the cascade.
        private float _lS1a, _lS2a, _lS1b, _lS2b;
        private float _rS1a, _rS2a, _rS1b, _rS2b;

        // Coefficient cache (PedalComp §6)
        private float _cachedFc = -1f;
        private int   _cachedSr = -1;

        public Lr4Filter(Lr4Mode mode)
        {
            _mode = mode;
        }

        public void UpdateCoefs(float fc, int sr)
        {
            // Clamp fc into a numerically safe range. tan(π·fc/sr) blows up
            // as fc → sr/2; cap well below Nyquist. The 10 Hz floor prevents
            // useless degenerate filters and divide-by-zero in pre-warp.
            fc = Math.Clamp(fc, 10f, sr * 0.49f);

            if (fc == _cachedFc && sr == _cachedSr) return;
            _cachedFc = fc;
            _cachedSr = sr;

            // Standard RBJ biquad coefficients for 2nd-order Butterworth LP/HP.
            float omega = 2f * MathF.PI * fc / sr;
            float sn    = MathF.Sin(omega);
            float cs    = MathF.Cos(omega);
            float alpha = sn / (2f * Q_BUTTERWORTH);

            float a0   = 1f + alpha;
            float invA = 1f / a0;

            float b0, b1, b2;
            if (_mode == Lr4Mode.Lowpass)
            {
                float k = 1f - cs;
                b0 = k * 0.5f;
                b1 = k;
                b2 = k * 0.5f;
            }
            else
            {
                float k = 1f + cs;
                b0 =  k * 0.5f;
                b1 = -k;
                b2 =  k * 0.5f;
            }

            _b0 = b0 * invA;
            _b1 = b1 * invA;
            _b2 = b2 * invA;
            _a1 = (-2f * cs) * invA;
            _a2 = (1f - alpha) * invA;
        }

        // Process one stereo sample through both biquad sections.
        // DFII-T per-section update:
        //     y     = b0·x + s1
        //     s1'   = b1·x + s2 - a1·y
        //     s2'   =        b2·x - a2·y
        public void Process(ref float L, ref float R)
        {
            // ── First biquad section ──
            float yLa = _b0 * L + _lS1a;
            _lS1a     = _b1 * L + _lS2a - _a1 * yLa;
            _lS2a     = _b2 * L         - _a2 * yLa;

            float yRa = _b0 * R + _rS1a;
            _rS1a     = _b1 * R + _rS2a - _a1 * yRa;
            _rS2a     = _b2 * R         - _a2 * yRa;

            // ── Second biquad section (cascade) ──
            float yLb = _b0 * yLa + _lS1b;
            _lS1b     = _b1 * yLa + _lS2b - _a1 * yLb;
            _lS2b     = _b2 * yLa         - _a2 * yLb;

            float yRb = _b0 * yRa + _rS1b;
            _rS1b     = _b1 * yRa + _rS2b - _a1 * yRb;
            _rS2b     = _b2 * yRa         - _a2 * yRb;

            L = yLb;
            R = yRb;
        }

        // Zero all filter state. Useful at song-start or after silence.
        public void Reset()
        {
            _lS1a = _lS2a = _lS1b = _lS2b = 0f;
            _rS1a = _rS2a = _rS1b = _rS2b = 0f;
        }

        // Flush near-zero state to true zero. Biquads with very small state
        // values can decay into IEEE denormal territory under sustained
        // silence, which causes CPU spikes on x86. Call once per buffer when
        // input is known to be quiet, or unconditionally if cost is irrelevant.
        // TODO v1.x: investigate enabling FTZ/DAZ globally via runtime config
        // and removing this housekeeping.
        public void FlushDenormals()
        {
            const float t = 1e-25f;
            if (MathF.Abs(_lS1a) < t) _lS1a = 0f;
            if (MathF.Abs(_lS2a) < t) _lS2a = 0f;
            if (MathF.Abs(_lS1b) < t) _lS1b = 0f;
            if (MathF.Abs(_lS2b) < t) _lS2b = 0f;
            if (MathF.Abs(_rS1a) < t) _rS1a = 0f;
            if (MathF.Abs(_rS2a) < t) _rS2a = 0f;
            if (MathF.Abs(_rS1b) < t) _rS1b = 0f;
            if (MathF.Abs(_rS2b) < t) _rS2b = 0f;
        }
    }
}
