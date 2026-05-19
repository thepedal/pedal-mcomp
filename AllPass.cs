// AllPass.cs — 2nd-order all-pass biquad for LR4 crossover phase correction.
//
// An LR4 (Linkwitz-Riley 4th-order) crossover sum (LP + HP at the same Fc)
// is a 2nd-order all-pass: magnitude-flat across all frequencies but with
// a frequency-dependent phase response. For a 4-band binary-tree
// crossover, each branch's band-sum is one all-pass response; the two
// branches have DIFFERENT all-pass responses (one at low_xover Fc, one at
// high_xover Fc). When summed, the responses misalign in phase at the
// crossover frequencies, causing peaks and notches.
//
// The fix is symmetric all-pass compensation:
//   - Add an AP at high_xover Fc to the low-half branch
//   - Add an AP at low_xover Fc to the high-half branch
//
// After compensation, every band path has the same phase response
// (AP@low * AP@mid * AP@high), so the sum is magnitude-flat AND
// phase-coherent. Total group delay is the cascade of three all-passes
// — typically 1-3 ms depending on crossover frequencies.
//
// Coefficient form (RBJ Audio EQ Cookbook all-pass):
//     alpha = sin(w0) / (2Q)
//     b0 = 1 - alpha    a0 = 1 + alpha
//     b1 = -2cos(w0)    a1 = -2cos(w0)
//     b2 = 1 + alpha    a2 = 1 - alpha
//
// Note the symmetry: b0 = a2, b2 = a0, b1 = a1. That's what makes it
// all-pass — the numerator polynomial is the reverse of the denominator,
// giving |H(jω)| = 1 everywhere.
//
// Uses Q = 1/√2 (Butterworth Q) to match the LR4 crossover's effective Q.

using System;

namespace PedalMComp
{
    internal sealed class Allpass
    {
        private const float Q_BUTTERWORTH = 0.70710678f;

        // Biquad coefficients (a0 normalised out)
        private float _b0, _b1, _b2, _a1, _a2;

        // DFII-T state per channel
        private float _lS1, _lS2;
        private float _rS1, _rS2;

        // Coefficient cache (PedalComp §6)
        private float _cachedFc = -1f;
        private int   _cachedSr = -1;

        public void UpdateCoefs(float fc, int sr)
        {
            fc = Math.Clamp(fc, 10f, sr * 0.49f);
            if (fc == _cachedFc && sr == _cachedSr) return;
            _cachedFc = fc;
            _cachedSr = sr;

            float omega = 2f * MathF.PI * fc / sr;
            float sn    = MathF.Sin(omega);
            float cs    = MathF.Cos(omega);
            float alpha = sn / (2f * Q_BUTTERWORTH);

            float a0   = 1f + alpha;
            float invA = 1f / a0;

            // All-pass: b0 = 1-alpha, b1 = -2cos, b2 = 1+alpha
            _b0 = (1f - alpha) * invA;
            _b1 = (-2f * cs)   * invA;
            _b2 = (1f + alpha) * invA;          // == 1 after normalisation
            _a1 = (-2f * cs)   * invA;
            _a2 = (1f - alpha) * invA;          // == b0
        }

        // DFII-T per-sample stereo process with per-sample denormal flush
        // per Core §30. Same pattern as Crossover.cs.
        public void Process(ref float L, ref float R)
        {
            const float DENORM = 1e-25f;

            float yL = _b0 * L + _lS1;
            _lS1 = _b1 * L + _lS2 - _a1 * yL;
            if (_lS1 > -DENORM && _lS1 < DENORM) _lS1 = 0f;
            _lS2 = _b2 * L         - _a2 * yL;
            if (_lS2 > -DENORM && _lS2 < DENORM) _lS2 = 0f;

            float yR = _b0 * R + _rS1;
            _rS1 = _b1 * R + _rS2 - _a1 * yR;
            if (_rS1 > -DENORM && _rS1 < DENORM) _rS1 = 0f;
            _rS2 = _b2 * R         - _a2 * yR;
            if (_rS2 > -DENORM && _rS2 < DENORM) _rS2 = 0f;

            L = yL;
            R = yR;
        }

        public void Reset()
        {
            _lS1 = _lS2 = 0f;
            _rS1 = _rS2 = 0f;
        }
    }
}
