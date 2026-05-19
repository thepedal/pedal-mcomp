// SpectrumAnalyzer.cs — windowed FFT-based magnitude spectrum for the GUI.
//
// Audio thread feeds mono samples (L+R averaged) into a ring buffer.
// Every UPDATE_INTERVAL_SAMPLES samples, a Hann-windowed FFT runs and
// updates the magnitudes array. The GUI thread reads magnitudes at its
// own cadence (~30 fps via DispatcherTimer + OnRender).
//
// Threading: the magnitudes array is updated in-place by the audio thread
// and read directly by the UI thread. Tearing on individual reads is
// possible but invisible at 30 fps display rate and bin-by-bin updates —
// at most one frame shows a partial update before the next frame catches
// up. The "lossy but cheap" pattern standard for analyser displays.
//
// FFT: in-place radix-2 Cooley-Tukey, decimation-in-time. ~30 µs per
// 1024-pt FFT at desktop CPU speeds, called ~30 times per second
// (driven by update interval). Total CPU ~0.1%.

using System;

namespace PedalMComp
{
    internal sealed class SpectrumAnalyzer
    {
        // FFT size — 1024 gives ~47 Hz bin width at 48 kHz, fine enough
        // for crossover-frequency display, coarse enough for cheap FFT.
        public const int FFT_SIZE  = 1024;
        public const int NUM_BINS  = FFT_SIZE / 2;        // useful bins (DC..Nyquist)
        private const int UPDATE_INTERVAL_SAMPLES = 1600; // ~30 Hz at 48 kHz

        // Ring buffer of incoming mono samples
        private readonly float[] _ring = new float[FFT_SIZE];
        private int _ringWriteIdx = 0;
        private int _samplesSinceUpdate = 0;

        // FFT scratch buffers
        private readonly float[] _re = new float[FFT_SIZE];
        private readonly float[] _im = new float[FFT_SIZE];

        // Pre-computed Hann window
        private readonly float[] _window = new float[FFT_SIZE];

        // Pre-computed twiddle table for in-place FFT
        private readonly float[] _cosT;
        private readonly float[] _sinT;

        // Output magnitudes in dBFS — public for direct UI read (tearing
        // acceptable). Smoothed across updates with exponential decay so
        // the displayed line doesn't flicker frame-to-frame.
        public readonly float[] MagnitudesDb = new float[NUM_BINS];

        // Sample rate cached from most recent Feed call — used by the GUI
        // to map bin indices to frequencies for the log-x display.
        public volatile float SampleRate = 48000f;

        public SpectrumAnalyzer()
        {
            // Hann window: w[n] = 0.5 - 0.5*cos(2π·n/(N-1))
            for (int n = 0; n < FFT_SIZE; n++)
                _window[n] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * n / (FFT_SIZE - 1));

            // Twiddle factors: e^(-j·2π·k/N) for k in [0..N/2)
            _cosT = new float[FFT_SIZE / 2];
            _sinT = new float[FFT_SIZE / 2];
            for (int k = 0; k < FFT_SIZE / 2; k++)
            {
                float angle = -2f * MathF.PI * k / FFT_SIZE;
                _cosT[k] = MathF.Cos(angle);
                _sinT[k] = MathF.Sin(angle);
            }

            // Initialise magnitudes to -120 dB so the display starts flat-low.
            for (int k = 0; k < NUM_BINS; k++) MagnitudesDb[k] = -120f;
        }

        // Called once per audio-thread sample with the mono mix value.
        // Returns true when a new FFT was computed this sample.
        public bool Feed(float monoSample, float sr)
        {
            SampleRate = sr;
            _ring[_ringWriteIdx] = monoSample;
            _ringWriteIdx++;
            if (_ringWriteIdx >= FFT_SIZE) _ringWriteIdx = 0;

            _samplesSinceUpdate++;
            if (_samplesSinceUpdate < UPDATE_INTERVAL_SAMPLES) return false;
            _samplesSinceUpdate = 0;

            ComputeFFT();
            return true;
        }

        private void ComputeFFT()
        {
            // Unroll ring buffer into _re with Hann window applied,
            // starting from the oldest sample (next write position).
            int idx = _ringWriteIdx;
            for (int n = 0; n < FFT_SIZE; n++)
            {
                _re[n] = _ring[idx] * _window[n];
                _im[n] = 0f;
                idx++;
                if (idx >= FFT_SIZE) idx = 0;
            }

            // In-place radix-2 Cooley-Tukey FFT, decimation-in-time.
            // Bit-reverse permutation:
            int j = 0;
            for (int i = 1; i < FFT_SIZE; i++)
            {
                int bit = FFT_SIZE >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (_re[i], _re[j]) = (_re[j], _re[i]);
                    (_im[i], _im[j]) = (_im[j], _im[i]);
                }
            }

            // Butterflies
            for (int len = 2; len <= FFT_SIZE; len <<= 1)
            {
                int half = len >> 1;
                int twStep = FFT_SIZE / len;
                for (int i = 0; i < FFT_SIZE; i += len)
                {
                    int tw = 0;
                    for (int k = 0; k < half; k++)
                    {
                        float c  = _cosT[tw];
                        float s  = _sinT[tw];
                        int   i1 = i + k;
                        int   i2 = i1 + half;
                        float tr = c * _re[i2] - s * _im[i2];
                        float ti = c * _im[i2] + s * _re[i2];
                        _re[i2] = _re[i1] - tr;
                        _im[i2] = _im[i1] - ti;
                        _re[i1] = _re[i1] + tr;
                        _im[i1] = _im[i1] + ti;
                        tw += twStep;
                    }
                }
            }

            // Magnitudes in dB. Window+FFT normalisation: divide by FFT_SIZE/2
            // for unit-amplitude sine → 0 dB. Hann window energy correction
            // would also be needed for absolute RMS — but for visual display
            // a relative dB scale is fine.
            const float NORM   = 2f / FFT_SIZE;
            const float DBFLOOR = -120f;
            // Smoothing coefficient (per FFT update, not per sample).
            // 0.6 = fast follow, 0.4 = previous value. Visual decay rather
            // than freeze-frame.
            const float SMOOTH_NEW = 0.6f;
            const float SMOOTH_OLD = 1f - SMOOTH_NEW;

            for (int k = 0; k < NUM_BINS; k++)
            {
                float mag = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * NORM;
                float db  = mag > 1e-6f ? 20f * MathF.Log10(mag) : DBFLOOR;
                MagnitudesDb[k] = SMOOTH_NEW * db + SMOOTH_OLD * MagnitudesDb[k];
            }
        }

        public void Reset()
        {
            Array.Clear(_ring, 0, FFT_SIZE);
            _ringWriteIdx = 0;
            _samplesSinceUpdate = 0;
            for (int k = 0; k < NUM_BINS; k++) MagnitudesDb[k] = -120f;
        }
    }
}
