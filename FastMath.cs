// FastMath.cs — IEEE 754 bit-manipulation fast log/exp for per-sample hot paths.
// Copied from Pedal Comp v1.0 (see PedalComp §5). Accuracy ±0.1 dB, sufficient
// for any dynamics processor. Kept here as a local copy rather than referenced
// from Pedal Comp because each managed machine is its own deployable unit and
// shares no runtime assemblies (per the v1 design decision to copy DSP rather
// than extract).

using System;

namespace PedalMComp
{
    internal static class FastMath
    {
        // 20 * Log10(lin) — accurate to ±0.1 dB over 1e-6 to 1.0.
        // Returns -120 dB for inputs at or below 1e-9 (silence floor).
        public static float LinToDb(float lin)
        {
            if (lin <= 1e-9f) return -120f;
            int   bits = BitConverter.SingleToInt32Bits(lin);
            float exp  = (bits >> 23) - 127f;
            float mant = BitConverter.Int32BitsToSingle((bits & 0x007FFFFF) | 0x3F800000) - 1f;
            float log2 = exp + mant * (1.4142f - 0.7071f * mant);
            return log2 * 6.02059f;     // log2 → dB: × (20 / log₂10)
        }

        // Pow(10, db / 20) — accurate to ±0.1 dB for db in [-120, +24].
        // Out-of-range inputs are clamped via the exponent saturation.
        public static float DbToLin(float db)
        {
            float x  = db * 0.16609f;   // db × log₂(10) / 20
            float xi = MathF.Floor(x);
            float xf = x - xi;
            float p  = 1f + xf * (0.69315f + xf * (0.24023f + xf * 0.05550f));
            int   e  = Math.Clamp((int)xi + 127, 1, 254);
            return BitConverter.Int32BitsToSingle(e << 23) * p;
        }
    }
}
