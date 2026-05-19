# gen_param_labels.py — generator for the ValueDescriptions string arrays
# inlined into PedalMComp.cs.
#
# C# attribute arguments must be compile-time constants, which means each
# ValueDescriptions array has to be a literal new[] { ... } in the source.
# Hand-maintaining 128-entry arrays of formatted unit strings is brittle;
# this script emits them deterministically from the parameter ranges.
#
# Kept in source per Pedal invFFT §23.4 convention — generator scripts live
# alongside the deployed source but are NOT deployed to the gear folder.
#
# Workflow:
#   python3 gen_param_labels.py             # prints all parameter decls (default)
#   python3 gen_param_labels.py --labels    # prints just the arrays
#
# Range encoding contract — must match PedalMComp.cs:
#
#  Param        Encoding                                  Index count
#  ─────────────────────────────────────────────────────────────────
#  Threshold    linear 0..60 = -0 dB .. -60 dB             61
#  Knee         linear 0..24 = 0 dB .. 24 dB               25
#  Makeup       linear 0..24 = 0 dB .. 24 dB               25
#  OutputGain   linear 0..48, offset -24 = ±24 dB          49
#  DryWet       linear 0..100 = 0 %..100 %                101
#  Attack       log 0..127, 0.1 ms .. 200 ms              128
#  Release      log 0..127, 1 ms .. 2000 ms               128
#  Ratio        log 0..126 → 1..40, value 127 = Limit     128
#  XoverLLM     log 0..127, 40 Hz .. 600 Hz               128
#  XoverLMHM    log 0..127, 200 Hz .. 3000 Hz             128
#  XoverHMH     log 0..127, 800 Hz .. 12000 Hz            128

import math
import sys


# ── Per-value formatters ────────────────────────────────────────────────

def fmt_ms(ms):
    # Resolution chosen so adjacent log-spaced values never collapse to the
    # same display string. Log step factor is ~1.06 per index, so at 1 ms
    # neighbours differ by ~0.06 ms (needs 2 decimals to distinguish), at
    # 10 ms by ~0.6 ms (1 decimal), at 100 ms by ~6 ms (integer).
    if ms < 1:    return f"{ms:.3f} ms"
    if ms < 10:   return f"{ms:.2f} ms"
    if ms < 100:  return f"{ms:.1f} ms"
    return f"{round(ms)} ms"

def fmt_hz(hz):
    if hz < 1000: return f"{round(hz)} Hz"
    return f"{hz / 1000:.2f} kHz"

def fmt_threshold_db(i):
    return "0.0 dB" if i == 0 else f"-{i}.0 dB"

def fmt_knee_or_makeup_db(i):
    return f"{i}.0 dB"

def fmt_output_gain_db(i):
    g = i - 24
    if g == 0: return "0.0 dB"
    return f"{g:+d}.0 dB"

def fmt_drywet_pct(i):
    return f"{i} %"

def fmt_ratio_idx(i):
    if i >= 127: return "Limit"
    r = 40.0 ** (i / 126.0)
    if r < 2:  return f"{r:.2f}:1"
    if r < 10: return f"{r:.1f}:1"
    return f"{round(r)}:1"


def fmt_lookahead_ms(ms):
    # Visual resolution chosen so adjacent log-mapped values rarely collapse:
    # range is 0.1..10 ms over 126 indices, so log step ~1.037× per index.
    # At 0.1 ms neighbours differ by ~0.004 ms (2 decimals OK), at 10 ms
    # by ~0.37 ms (1 decimal OK).
    if ms < 1.0:  return f"{ms:.2f} ms"
    if ms < 10.0: return f"{ms:.2f} ms"
    return f"{ms:.1f} ms"

def lookahead_array():
    # Index 0 is the off sentinel — zero-latency fast path in audio code.
    # Indices 1..127 log-map 0.1..10 ms (×100 range over 126 steps).
    out = ["Off"]
    for v in range(1, 128):
        ms = 0.1 * (100.0 ** ((v - 1) / 126.0))
        out.append(fmt_lookahead_ms(ms))
    return out


# ── Array generators ────────────────────────────────────────────────────

def linear(n, fmt):
    return [fmt(i) for i in range(n)]

def log_range(n, lo, hi, fmt):
    return [fmt(lo * (hi / lo) ** (i / (n - 1))) for i in range(n)]


# ── Build all arrays ────────────────────────────────────────────────────

ARRAYS = {
    "Threshold":  linear(61,  fmt_threshold_db),
    "Knee":       linear(25,  fmt_knee_or_makeup_db),
    "Makeup":     linear(25,  fmt_knee_or_makeup_db),
    "OutputGain": linear(49,  fmt_output_gain_db),
    "DryWet":     linear(101, fmt_drywet_pct),
    "Attack":     log_range(128, 0.1, 200,  fmt_ms),
    "Release":    log_range(128, 1.0, 2000, fmt_ms),
    "Ratio":      [fmt_ratio_idx(i) for i in range(128)],
    "XoverLLM":   log_range(128, 40,  600,   fmt_hz),
    "XoverLMHM":  log_range(128, 200, 3000,  fmt_hz),
    "XoverHMH":   log_range(128, 800, 12000, fmt_hz),
    # v1.3 additions:
    "Lookahead":  lookahead_array(),
}


def emit_array_literal(items, indent):
    sp = " " * indent
    lines = []
    for chunk_start in range(0, len(items), 8):
        chunk = items[chunk_start:chunk_start + 8]
        line = sp + ", ".join(f'"{s}"' for s in chunk)
        if chunk_start + len(chunk) < len(items):
            line += ","
        lines.append(line)
    return "\n".join(lines)


# ── Parameter declarations (paste-ready blocks) ─────────────────────────

# Each entry: (csharp_property_name, display_name, description,
#              min, max, default, array_key_or_none, inline_vd_or_none)
PARAMS = [
    ("Listen", "Listen",
     "Solo a single band for monitoring; All sums every band",
     0, 4, 0, None, ["All", "L", "LM", "HM", "H"]),

    ("XoverLLM", "Xover L-LM",
     "Crossover frequency between Low and Lo-Mid bands (40-600 Hz, log)",
     0, 127, 52, "XoverLLM", None),
    ("XoverLMHM", "Xover LM-HM",
     "Crossover frequency between Lo-Mid and Hi-Mid bands (200-3000 Hz, log)",
     0, 127, 59, "XoverLMHM", None),
    ("XoverHMH", "Xover HM-H",
     "Crossover frequency between Hi-Mid and High bands (800-12000 Hz, log)",
     0, 127, 75, "XoverHMH", None),

    ("Knee", "Knee",
     "Soft-knee width in dB, shared across all bands",
     0, 24, 6, "Knee", None),
    ("Detection", "Detection",
     "Level detection mode, shared across bands",
     0, 1, 0, None, ["Peak", "RMS"]),
]

# Per-band defaults — attack/release vary by band (slow on lows, fast on highs)
BAND_DEFAULTS = {
    "L":  (95, 89),
    "LM": (77, 77),
    "HM": (65, 65),
    "H":  (57, 57),
}

for band in ["L", "LM", "HM", "H"]:
    atk_idx, rel_idx = BAND_DEFAULTS[band]
    PARAMS.append((f"{band}_Threshold", f"{band} Threshold",
        f"{band} band threshold in dB below 0 dBFS",
        0, 60, 18, "Threshold", None))
    PARAMS.append((f"{band}_Ratio", f"{band} Ratio",
        f"{band} band ratio, log-mapped 1:1 to 40:1; top value is Limit",
        0, 127, 31, "Ratio", None))
    PARAMS.append((f"{band}_Attack", f"{band} Attack",
        f"{band} band attack time, log-mapped 0.1 ms to 200 ms",
        0, 127, atk_idx, "Attack", None))
    PARAMS.append((f"{band}_Release", f"{band} Release",
        f"{band} band release time, log-mapped 1 ms to 2000 ms",
        0, 127, rel_idx, "Release", None))
    PARAMS.append((f"{band}_Makeup", f"{band} Makeup",
        f"{band} band makeup gain in dB",
        0, 24, 0, "Makeup", None))
    PARAMS.append((f"{band}_Bypass", f"{band} Bypass",
        f"Bypass compression on the {band} band; band still routes through the crossover",
        0, 1, 0, None, ["Off", "On"]))

PARAMS.append(("OutputGain", "Output Gain",
    "Global output gain after summation, in dB; index 24 is unity",
    0, 48, 24, "OutputGain", None))
PARAMS.append(("DryWet", "Dry-Wet",
    "Mix between dry input (0) and compressed output (100)",
    0, 100, 100, "DryWet", None))

# ── v1.3 additions ──────────────────────────────────────────────────────
PARAMS.append(("Lookahead", "Lookahead",
    "Pre-delays audio so the compressor reacts before transients reach the output. "
    "Adds equivalent processing latency — comb-filtering occurs if a parallel dry "
    "path is in use. Off = zero-latency.",
    0, 127, 0, "Lookahead", None))
PARAMS.append(("PhaseLinear", "Phase Linear",
    "Adds all-pass compensation so the four bands sum phase-coherently at "
    "crossovers. Adds a small fixed group delay (1-3 ms depending on crossover "
    "frequencies).",
    0, 1, 0, None, ["Off", "On"]))
PARAMS.append(("SpectrumView", "Spectrum View",
    "Show a real-time spectrum analyser below the OUT meter, showing the "
    "post-effect output signal. Does not affect audio.",
    0, 1, 0, None, ["Off", "On"]))


def emit_param_decl(p):
    name, disp, desc, mn, mx, df, akey, extra = p
    lines = []
    lines.append("        [ParameterDecl(")
    lines.append(f'            Name        = "{disp}",')
    lines.append(f'            Description = "{desc}",')
    if akey is None and extra is None:
        lines.append(f'            MinValue = {mn}, MaxValue = {mx}, DefValue = {df})]')
    elif extra is not None:
        lit = ", ".join(f'"{s}"' for s in extra)
        lines.append(f'            MinValue = {mn}, MaxValue = {mx}, DefValue = {df},')
        lines.append(f'            ValueDescriptions = new[] {{ {lit} }})]')
    else:
        lines.append(f'            MinValue = {mn}, MaxValue = {mx}, DefValue = {df},')
        lines.append('            ValueDescriptions = new[] {')
        lines.append(emit_array_literal(ARRAYS[akey], indent=16))
        lines.append("            })]")
    lines.append(f'        public int {name} {{ get; set; }}')
    return "\n".join(lines)


# ── Main ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "decls"

    if mode == "--labels":
        for name, items in ARRAYS.items():
            print(f"// {name} ({len(items)} entries)")
            print("new[] {")
            print(emit_array_literal(items, indent=4))
            print("}")
            print()
    else:
        for p in PARAMS:
            print(emit_param_decl(p))
            print()
