// BandCompressor.cs — single-band stereo-linked soft-knee compressor.
//
// Direct port of the Pedal Comp v1 DSP. Per the v1 design decision (copy
// rather than extract), this is a standalone class with no runtime
// dependency on the Pedal Comp assembly — any divergent v1.x improvements
// here propagate only to MComp, not to Pedal Comp.
//
// Architecture: feed-forward, log-domain.
//   1. Detect input level (peak or RMS) per sample, stereo-linked.
//   2. Apply attack/release smoothing in dB domain to the detected level.
//   3. Compute gain reduction (dB) via the soft-knee static curve.
//   4. Convert GR to linear, multiply in makeup gain, apply to L+R.
//
// Coefficient cache and FastMath usage follow PedalComp §5/§6.

using System;

namespace PedalMComp
{
    internal sealed class BandCompressor
    {
        // ── Detection state ─────────────────────────────────────────────
        private float _envDb     = -120f;   // smoothed level estimate, dB
        private float _rmsEnvSq  = 0f;      // squared linear RMS averaging

        // ── Metering (audio thread writes, UI thread reads) ─────────────
        // volatile float gives ordering on x64 for single-writer/single-
        // reader display purposes per PedalComp §7. Both values in dB:
        //   MeterInDb — smoothed input level into this band's compressor
        //   MeterGrDb — current gain reduction (positive dB)
        public volatile float MeterInDb = -120f;
        public volatile float MeterGrDb = 0f;

        // ── Lookahead delay line (stereo) ───────────────────────────────
        // Pre-allocated at construction at MAX_LOOKAHEAD_SAMPLES so no
        // audio-thread allocations. Sized to cover the parameter's full
        // 10 ms range up to 192 kHz (1920 samples) plus headroom.
        private const int MAX_LOOKAHEAD_SAMPLES = 2048;
        private readonly float[] _delayL = new float[MAX_LOOKAHEAD_SAMPLES];
        private readonly float[] _delayR = new float[MAX_LOOKAHEAD_SAMPLES];
        private int _delayWriteIdx = 0;

        // ── Coefficient cache ───────────────────────────────────────────
        private int   _cSr        = -1;
        private float _cAttackMs  = -1f;
        private float _cReleaseMs = -1f;
        private float _attackCoef;          // exp(-1/(τ·sr))
        private float _releaseCoef;
        private float _rmsCoef;             // 10 ms RMS averaging window

        public void UpdateCoefs(int sr, float attackMs, float releaseMs)
        {
            if (sr == _cSr && attackMs == _cAttackMs && releaseMs == _cReleaseMs) return;
            _cSr        = sr;
            _cAttackMs  = attackMs;
            _cReleaseMs = releaseMs;

            // exp(-1/(τ·sr)) — coef → 0 as τ → 0 (instant), → 1 as τ → ∞ (held).
            // Divide-by-zero is harmless under IEEE: -1/0 → -Inf, exp(-Inf) → 0,
            // which gives coef=0 (instant tracking) when the user dials 0 ms.
            // In practice the log range starts at 0.1 ms so the zero case
            // doesn't fire, but the guard costs nothing and matches the
            // generic time-constant pattern.
            float aSamples = attackMs  * 0.001f * sr;
            float rSamples = releaseMs * 0.001f * sr;
            _attackCoef  = aSamples > 0f ? MathF.Exp(-1f / aSamples) : 0f;
            _releaseCoef = rSamples > 0f ? MathF.Exp(-1f / rSamples) : 0f;

            // ~10 ms RMS averaging window. Tied to sr but not to user params,
            // so it's grouped here for convenience and just rides along in
            // the cache check.
            _rmsCoef = MathF.Exp(-1f / (0.010f * sr));
        }

        // Static soft-knee gain-reduction curve. Marked internal static per
        // PedalComp §8 so a future GUI class can call the same formula
        // directly when drawing the transfer curve.
        //
        //   x  = input level in dB
        //   T  = threshold in dB
        //   W  = knee width in dB (0 → hard knee)
        //   R  = ratio (1.0 → no compression, very large → limit)
        //
        // Returns positive dB of gain reduction to subtract.
        internal static float SoftKneeGR(float xDb, float threshDb, float kneeDb, float ratio)
        {
            float overshoot = xDb - threshDb;
            float halfKnee  = kneeDb * 0.5f;

            // Below knee → no compression.
            if (overshoot <= -halfKnee) return 0f;

            // 1/R: ratio=1 → 1.0 (slope unchanged, GR=0); ratio→∞ → 0 (limiting).
            float invRatio = 1f / ratio;

            // Above knee → linear compression: GR = overshoot · (1 - 1/R).
            if (overshoot >= halfKnee)
                return overshoot * (1f - invRatio);

            // Within knee → quadratic interpolation. Standard Reiss curve:
            //   GR = (overshoot + W/2)² / (2·W) · (1 - 1/R)
            // Falls out of the kneeDb=0 hard-knee case naturally because
            // overshoot=0 gets routed to the "below knee" branch via -halfKnee=0.
            float kneeOver = overshoot + halfKnee;
            return kneeOver * kneeOver / (2f * kneeDb) * (1f - invRatio);
        }

        // Process one stereo sample. Modifies L/R in place.
        // - Detection runs unconditionally so the metering stays live even
        //   when the band is bypassed (useful for tuning: user can see how
        //   hot a band is before deciding to compress it).
        // - bypass=true skips ONLY the gain application; detection still
        //   updates _envDb and the volatile meter fields.
        // - lookaheadSamples > 0 routes audio through a delay line so the
        //   detector "sees" the future relative to the output (compression
        //   kicks in *before* the transient at the output). Detector reads
        //   live signal; gain applies to the delayed sample. The delay
        //   runs regardless of bypass — all bands must share identical
        //   delay so they stay aligned at the sum.
        public void Process(
            ref float L, ref float R,
            float threshDb, float ratio, float kneeDb,
            float makeupLin, bool isRms, bool bypass,
            int lookaheadSamples)
        {
            // ── 1. Stereo-linked level detection (always, on live signal) ──
            float detLin;
            if (isRms)
            {
                float ssq = (L * L + R * R) * 0.5f;
                _rmsEnvSq = _rmsCoef * _rmsEnvSq + (1f - _rmsCoef) * ssq;
                // Denormal flush per Core §30: _rmsEnvSq is squared (always
                // ≥0) and decays toward zero on sustained silence. Single
                // comparison since negative values are impossible here.
                // _envDb below doesn't need protection — it decays toward
                // -120 dB, far from denormal range on the negative side.
                if (_rmsEnvSq < 1e-25f) _rmsEnvSq = 0f;
                detLin    = MathF.Sqrt(_rmsEnvSq);
            }
            else
            {
                detLin = MathF.Max(MathF.Abs(L), MathF.Abs(R));
            }

            float detDb = FastMath.LinToDb(detLin);

            // ── 2. Attack/release smoothing in dB domain ────────────────
            float coef = detDb > _envDb ? _attackCoef : _releaseCoef;
            _envDb = coef * _envDb + (1f - coef) * detDb;

            // Publish input level for the meter regardless of bypass.
            MeterInDb = _envDb;

            // ── 3. Lookahead delay line (if enabled) ────────────────────
            // Always writes/reads when lookahead > 0, regardless of bypass:
            // all 4 bands must share identical delay so the sum stays
            // phase-coherent. The zero-lookahead fast path skips the
            // delay machinery entirely.
            if (lookaheadSamples > 0)
            {
                _delayL[_delayWriteIdx] = L;
                _delayR[_delayWriteIdx] = R;
                int readIdx = _delayWriteIdx - lookaheadSamples;
                if (readIdx < 0) readIdx += MAX_LOOKAHEAD_SAMPLES;
                L = _delayL[readIdx];
                R = _delayR[readIdx];
                _delayWriteIdx++;
                if (_delayWriteIdx >= MAX_LOOKAHEAD_SAMPLES) _delayWriteIdx = 0;
            }

            // ── 4. Bypass: skip gain application but keep meters honest ──
            if (bypass)
            {
                MeterGrDb = 0f;
                return;
            }

            // ── 5. Static curve → gain reduction in dB ──────────────────
            float grDb = SoftKneeGR(_envDb, threshDb, kneeDb, ratio);
            MeterGrDb = grDb;

            // ── 6. Apply gain reduction + makeup in linear domain ───────
            // Applied to L/R which are now the DELAYED signal if lookahead > 0,
            // or the live signal otherwise.
            float gainLin = FastMath.DbToLin(-grDb) * makeupLin;
            L *= gainLin;
            R *= gainLin;
        }

        // Zero detection state. Call at song-start or after extended silence.
        public void Reset()
        {
            _envDb    = -120f;
            _rmsEnvSq = 0f;
            MeterInDb = -120f;
            MeterGrDb = 0f;
            Array.Clear(_delayL, 0, MAX_LOOKAHEAD_SAMPLES);
            Array.Clear(_delayR, 0, MAX_LOOKAHEAD_SAMPLES);
            _delayWriteIdx = 0;
        }
    }
}
