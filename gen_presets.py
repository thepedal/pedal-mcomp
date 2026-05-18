# gen_presets.py — Pedal MComp v1.0 preset bank generator.
#
# Emits Pedal MComp_Presets.prs.xml in the format documented in Build §3.
# Run with:    python3 gen_presets.py
# Output:      ./Pedal MComp_Presets.prs.xml (UTF-8 with BOM)
#
# Kept in source for reproducibility but NOT deployed to the gear folder
# (per Build §3.4 — generator scripts live alongside source but the gear
# folder gets only the .dll and the .prs.xml).
#
# Sparse-override pattern (PedalInvFFT §23.2): each preset is a dict of
# parameter-name → value overrides. Unspecified parameters fall back to
# DEFAULTS, which mirrors the DefValue declarations in PedalMComp.cs.
# Append-only changes to PedalMComp.cs (Build §3.3) keep the bank valid
# across future version bumps — new params get the machine's DefValue
# unless a preset explicitly mentions them.

import math
from pathlib import Path

# ── Parameter declaration order ─────────────────────────────────────────
# Must match PedalMComp.cs declaration order, which IS the preset contract.
# To add a new parameter: APPEND to the end of both this list and the
# DEFAULTS dict, never insert in the middle (Build §3.3).
PARAM_INDEX = {
    "Listen":         0,
    "Xover L-LM":     1,
    "Xover LM-HM":    2,
    "Xover HM-H":     3,
    "Knee":           4,
    "Detection":      5,
    "L Threshold":    6,
    "L Ratio":        7,
    "L Attack":       8,
    "L Release":      9,
    "L Makeup":      10,
    "L Bypass":      11,
    "LM Threshold": 12,
    "LM Ratio":     13,
    "LM Attack":    14,
    "LM Release":   15,
    "LM Makeup":    16,
    "LM Bypass":    17,
    "HM Threshold": 18,
    "HM Ratio":     19,
    "HM Attack":    20,
    "HM Release":   21,
    "HM Makeup":    22,
    "HM Bypass":    23,
    "H Threshold":  24,
    "H Ratio":      25,
    "H Attack":     26,
    "H Release":    27,
    "H Makeup":     28,
    "H Bypass":     29,
    "Output Gain":  30,
    "Dry-Wet":      31,
}

# Mirrors the DefValue= entries in PedalMComp.cs / gen_param_labels.py PARAMS.
DEFAULTS = {
    "Listen":          0,
    "Xover L-LM":     52,    # ~120 Hz
    "Xover LM-HM":    59,    # ~700 Hz
    "Xover HM-H":     75,    # ~4 kHz
    "Knee":            6,
    "Detection":       0,    # Peak
    "L Threshold":    18,   "L Ratio":   31, "L Attack":   95, "L Release":   89, "L Makeup":   0, "L Bypass":   0,
    "LM Threshold":   18,   "LM Ratio":  31, "LM Attack":  77, "LM Release":  77, "LM Makeup":  0, "LM Bypass":  0,
    "HM Threshold":   18,   "HM Ratio":  31, "HM Attack":  65, "HM Release":  65, "HM Makeup":  0, "HM Bypass":  0,
    "H Threshold":    18,   "H Ratio":   31, "H Attack":   57, "H Release":   57, "H Makeup":   0, "H Bypass":   0,
    "Output Gain":    24,   # 0 dB
    "Dry-Wet":       100,   # fully wet
}

assert set(PARAM_INDEX) == set(DEFAULTS), "PARAM_INDEX and DEFAULTS must match"
assert len(PARAM_INDEX) == 32, "Pedal MComp v1 has exactly 32 globals"


# ── Index-mapping helpers ───────────────────────────────────────────────
# These mirror the MapHzLog / MapMsLog / MapRatio formulas in PedalMComp.cs.
# Used to spell out preset intent in physical units (Hz, ms, ratio) rather
# than raw indices — the script translates here.

def hz_to_idx(hz, lo, hi):
    return round(127 * math.log(hz / lo) / math.log(hi / lo))

def ms_to_idx(ms, lo, hi):
    return round(127 * math.log(ms / lo) / math.log(hi / lo))

def ratio_to_idx(r):
    # ratio 1..40 maps to 0..126; sentinel index 127 means Limit (∞:1)
    if r >= 1e6 or r == float("inf"): return 127
    return round(126 * math.log(r) / math.log(40))

def xo1(hz): return hz_to_idx(hz, 40,  600)     # Xover L-LM (40..600 Hz)
def xo2(hz): return hz_to_idx(hz, 200, 3000)    # Xover LM-HM (200..3 kHz)
def xo3(hz): return hz_to_idx(hz, 800, 12000)   # Xover HM-H (800..12 kHz)
def atk(ms): return ms_to_idx(ms, 0.1, 200)     # Attack (0.1..200 ms)
def rel(ms): return ms_to_idx(ms, 1.0, 2000)    # Release (1..2000 ms)
def rat(r):  return ratio_to_idx(r)


# ── Preset definitions ──────────────────────────────────────────────────
# Each preset is a dict of {param_name: value}. Omitted params use DEFAULTS.
# Values can be raw ints OR — for readability — wrapped through the helpers
# above (hz/ms/ratio → index).

PRESETS = {

    # ─────── Utility ───────

    "Default": {
        # explicit "matches machine defaults" preset — useful starting point
    },

    "Bypass": {
        "L Bypass": 1, "LM Bypass": 1, "HM Bypass": 1, "H Bypass": 1,
        "Dry-Wet": 0,
    },

    # ─────── Mastering ───────

    "Mastering - Transparent": {
        # Very gentle, almost invisible 1.5:1 on a finished mix
        "L Threshold": 12, "L Ratio": rat(1.5),  "L Attack": atk(50),  "L Release": rel(500),
        "LM Threshold": 12, "LM Ratio": rat(1.5), "LM Attack": atk(15), "LM Release": rel(300),
        "HM Threshold": 12, "HM Ratio": rat(1.5), "HM Attack": atk(8),  "HM Release": rel(200),
        "H Threshold":  12, "H Ratio":  rat(1.5), "H Attack":  atk(5),  "H Release":  rel(150),
        "Knee": 12, "Detection": 1,     # wide soft knee, RMS
        "Output Gain": 25,              # +1 dB
    },

    "Mastering - Glue": {
        # Classic bus-comp glue, 2:1
        "L Threshold": 15, "L Ratio": rat(2.0),  "L Attack": atk(30),  "L Release": rel(200),
        "LM Threshold": 15, "LM Ratio": rat(2.0), "LM Attack": atk(10), "LM Release": rel(100),
        "HM Threshold": 15, "HM Ratio": rat(2.0), "HM Attack": atk(5),  "HM Release": rel(50),
        "H Threshold":  15, "H Ratio":  rat(2.0), "H Attack":  atk(3),  "H Release":  rel(30),
        "L Makeup": 1, "LM Makeup": 1, "HM Makeup": 1, "H Makeup": 1,
        "Knee": 8, "Detection": 1,
        "Output Gain": 25,
    },

    "Mastering - Punch": {
        # Lets transients through, brings out presence
        "L Threshold": 18, "L Ratio": rat(2.0),  "L Attack": atk(20),  "L Release": rel(100),
        "LM Threshold": 18, "LM Ratio": rat(2.0), "LM Attack": atk(5),  "LM Release": rel(50),
        "HM Threshold": 18, "HM Ratio": rat(2.5), "HM Attack": atk(3),  "HM Release": rel(30),
        "H Threshold":  18, "H Ratio":  rat(3.0), "H Attack":  atk(0.5),"H Release":  rel(15),
        "L Makeup": 1, "LM Makeup": 2, "HM Makeup": 2, "H Makeup": 3,
        "Knee": 6, "Detection": 0,
        "Output Gain": 26,              # +2 dB
    },

    "Mastering - Loudness": {
        # More aggressive 3:1, faster, with substantial makeup
        "L Threshold": 24, "L Ratio": rat(3.0),  "L Attack": atk(5),   "L Release": rel(50),
        "LM Threshold": 24, "LM Ratio": rat(3.0), "LM Attack": atk(3),  "LM Release": rel(30),
        "HM Threshold": 24, "HM Ratio": rat(3.0), "HM Attack": atk(1.5),"HM Release": rel(15),
        "H Threshold":  24, "H Ratio":  rat(3.0), "H Attack":  atk(0.5),"H Release":  rel(10),
        "L Makeup": 4, "LM Makeup": 4, "HM Makeup": 4, "H Makeup": 4,
        "Knee": 4, "Detection": 0,
        "Output Gain": 28,              # +4 dB
    },

    # ─────── Mix Bus ───────

    "Mix Bus - Tighten": {
        # Moderate 2:1, mostly defaults
        "L Threshold": 18, "L Ratio": rat(2.0),
        "LM Threshold": 18, "LM Ratio": rat(2.0),
        "HM Threshold": 18, "HM Ratio": rat(2.0),
        "H Threshold":  18, "H Ratio":  rat(2.0),
        "L Makeup": 2, "LM Makeup": 2, "HM Makeup": 2, "H Makeup": 2,
        "Knee": 6, "Detection": 0,
        "Output Gain": 25,
    },

    "Mix Bus - Vintage": {
        # Slow attack, slower release, RMS detection — that vintage VCA character
        "L Threshold": 18, "L Ratio": rat(2.5),  "L Attack": atk(50),  "L Release": rel(300),
        "LM Threshold": 18, "LM Ratio": rat(2.5), "LM Attack": atk(30), "LM Release": rel(150),
        "HM Threshold": 18, "HM Ratio": rat(2.5), "HM Attack": atk(20), "HM Release": rel(100),
        "H Threshold":  18, "H Ratio":  rat(2.5), "H Attack":  atk(10), "H Release":  rel(80),
        "L Makeup": 3, "LM Makeup": 3, "HM Makeup": 2, "H Makeup": 2,
        "Knee": 12, "Detection": 1,
        "Output Gain": 26,
    },

    # ─────── Drum Bus ───────

    "Drum Bus - Punch": {
        # Slow attack on lows lets kick punch through; fast on highs controls cymbal bleed
        "L Threshold": 18, "L Ratio": rat(4.0),  "L Attack": atk(5),   "L Release": rel(30),
        "LM Threshold": 18, "LM Ratio": rat(4.0), "LM Attack": atk(5),  "LM Release": rel(30),
        "HM Threshold": 15, "HM Ratio": rat(2.0), "HM Attack": atk(20), "HM Release": rel(100),
        "H Threshold":  21, "H Ratio":  rat(1.5), "H Attack":  atk(20), "H Release":  rel(100),
        "L Makeup": 3, "LM Makeup": 3, "HM Makeup": 1, "H Makeup": 1,
        "Knee": 6, "Detection": 0,
        "Output Gain": 25,
    },

    "Drum Bus - Smash": {
        # Heavy parallel-style compression. Often used in parallel via Dry-Wet
        "L Threshold": 27, "L Ratio": rat(8.0),  "L Attack": atk(1),   "L Release": rel(15),
        "LM Threshold": 27, "LM Ratio": rat(8.0), "LM Attack": atk(1),  "LM Release": rel(15),
        "HM Threshold": 27, "HM Ratio": rat(8.0), "HM Attack": atk(1),  "HM Release": rel(15),
        "H Threshold":  27, "H Ratio":  rat(8.0), "H Attack":  atk(0.5),"H Release":  rel(15),
        "L Makeup": 6, "LM Makeup": 6, "HM Makeup": 6, "H Makeup": 6,
        "Knee": 3, "Detection": 0,
        "Output Gain": 27,              # +3 dB
        "Dry-Wet": 50,                  # 50/50 parallel
    },

    "Drum Bus - Vintage": {
        # That vintage drum-bus pump: slow attack, RMS, soft knee, moderate ratio
        "L Threshold": 18, "L Ratio": rat(4.0),  "L Attack": atk(20),  "L Release": rel(50),
        "LM Threshold": 18, "LM Ratio": rat(4.0), "LM Attack": atk(20), "LM Release": rel(50),
        "HM Threshold": 18, "HM Ratio": rat(3.0), "HM Attack": atk(20), "HM Release": rel(50),
        "H Threshold":  18, "H Ratio":  rat(2.0), "H Attack":  atk(10), "H Release":  rel(30),
        "L Makeup": 4, "LM Makeup": 4, "HM Makeup": 3, "H Makeup": 2,
        "Knee": 12, "Detection": 1,
        "Output Gain": 26,
    },

    # ─────── Vocal ───────

    "Vocal - De-Ess": {
        # Compress only the sibilance band; L/LM bypassed entirely.
        # Crossover HM-H moved up to ~6 kHz to isolate sibilance from presence.
        "Xover HM-H": xo3(6000),
        "L Bypass": 1, "LM Bypass": 1,
        "HM Threshold": 12, "HM Ratio": rat(8.0),  "HM Attack": atk(0.5), "HM Release": rel(15),
        "H Threshold":  15, "H Ratio":  rat(8.0),  "H Attack":  atk(0.5), "H Release":  rel(15),
        "Knee": 6, "Detection": 0,
        "Output Gain": 24,              # unity — same level, just tamed sibilance
    },

    "Vocal - Air": {
        # Gentle compression with extra makeup up top — adds presence/clarity
        "L Threshold": 18, "L Ratio": rat(1.5),
        "LM Threshold": 18, "LM Ratio": rat(1.5),
        "HM Threshold": 15, "HM Ratio": rat(2.0),
        "H Threshold":  15, "H Ratio":  rat(2.0),
        "L Makeup": 0, "LM Makeup": 1, "HM Makeup": 2, "H Makeup": 4,
        "Knee": 8, "Detection": 1,
        "Output Gain": 25,
    },

    "Vocal - Body Control": {
        # Tames the low-mid boom that vocals get; other bands gentle
        "L Threshold": 18, "L Ratio": rat(1.5),
        "LM Threshold": 18, "LM Ratio": rat(4.0),  "LM Attack": atk(10), "LM Release": rel(80),
        "HM Threshold": 18, "HM Ratio": rat(2.0),
        "H Threshold":  18, "H Ratio":  rat(1.5),
        "LM Makeup": 2,
        "Knee": 6, "Detection": 0,
        "Output Gain": 25,
    },

    "Vocal - Smooth": {
        # Transparent vocal compression for tracking
        "L Threshold": 15, "L Ratio": rat(2.0),  "L Release": rel(300),
        "LM Threshold": 15, "LM Ratio": rat(2.0), "LM Release": rel(200),
        "HM Threshold": 15, "HM Ratio": rat(2.0), "HM Release": rel(150),
        "H Threshold":  15, "H Ratio":  rat(2.0), "H Release":  rel(100),
        "L Makeup": 2, "LM Makeup": 2, "HM Makeup": 2, "H Makeup": 2,
        "Knee": 10, "Detection": 1,
        "Output Gain": 25,
    },

    # ─────── Bass ───────

    "Bass - Tighten": {
        # Controls sub, lets mid character through, gentle on highs
        "L Threshold": 18, "L Ratio": rat(4.0),  "L Attack": atk(10),  "L Release": rel(100),
        "LM Threshold": 18, "LM Ratio": rat(2.0), "LM Attack": atk(20), "LM Release": rel(100),
        "HM Threshold": 18, "HM Ratio": rat(1.5),
        "H Threshold":  18, "H Ratio":  rat(1.5),
        "L Makeup": 3, "LM Makeup": 1, "HM Makeup": 0, "H Makeup": 0,
        "Knee": 6, "Detection": 0,
        "Output Gain": 25,
    },

    "Bass - Sub Limit": {
        # Heavy sub limiting for safe bass mixing; midrange untouched
        "Xover L-LM": xo1(80),         # tighter sub band, only ~80 Hz and below
        "L Threshold": 12, "L Ratio": 127, "L Attack": atk(1),  "L Release": rel(30),    # 127 = Limit
        "LM Threshold": 18, "LM Ratio": rat(1.5),
        "HM Threshold": 18, "HM Ratio": rat(1.5),
        "H Threshold":  18, "H Ratio":  rat(1.5),
        "Knee": 3, "Detection": 0,
        "Output Gain": 24,
    },

    # ─────── FX / Character ───────

    "FX - Pumping": {
        # That EDM sidechain pumping look (faked via release-driven gain modulation).
        # All four bands hit hard with very fast release.
        "L Threshold": 30, "L Ratio": rat(10),  "L Attack": atk(1),   "L Release": rel(50),
        "LM Threshold": 30, "LM Ratio": rat(10), "LM Attack": atk(1),  "LM Release": rel(50),
        "HM Threshold": 30, "HM Ratio": rat(10), "HM Attack": atk(1),  "HM Release": rel(50),
        "H Threshold":  30, "H Ratio":  rat(10), "H Attack":  atk(1),  "H Release":  rel(50),
        "L Makeup": 8, "LM Makeup": 8, "HM Makeup": 8, "H Makeup": 8,
        "Knee": 3, "Detection": 0,
        "Output Gain": 24,
    },

    "FX - Squash": {
        # Brick-wall feel: hard knee, very high ratio, fast attack
        "L Threshold": 27, "L Ratio": rat(20),  "L Attack": atk(0.5), "L Release": rel(30),
        "LM Threshold": 27, "LM Ratio": rat(20), "LM Attack": atk(0.5),"LM Release": rel(30),
        "HM Threshold": 27, "HM Ratio": rat(20), "HM Attack": atk(0.5),"HM Release": rel(30),
        "H Threshold":  27, "H Ratio":  rat(20), "H Attack":  atk(0.5),"H Release":  rel(30),
        "L Makeup": 6, "LM Makeup": 6, "HM Makeup": 6, "H Makeup": 6,
        "Knee": 0, "Detection": 0,
        "Output Gain": 25,
    },

    "FX - Lo-Fi": {
        # Heavy mid compression with the highs damped — that crushed AM-radio quality
        "L Threshold": 24, "L Ratio": rat(4.0),  "L Attack": atk(5),
        "LM Threshold": 24, "LM Ratio": rat(8.0), "LM Attack": atk(5),
        "HM Threshold": 24, "HM Ratio": rat(8.0), "HM Attack": atk(5),
        "H Threshold":   6, "H Ratio":  rat(20),  "H Attack":  atk(0.5), "H Release": rel(15),
        "L Makeup": 3, "LM Makeup": 5, "HM Makeup": 5, "H Makeup": 0,
        "Knee": 4, "Detection": 0,
        "Output Gain": 26,
    },
}


# ── XML emission ────────────────────────────────────────────────────────

def emit_xml(presets, machine_name="Pedal MComp"):
    lines = ['<?xml version="1.0" encoding="utf-8"?>', '<PresetDictionary>']
    for preset_name, overrides in presets.items():
        # Validate that all override keys are real parameter names
        for k in overrides:
            if k not in PARAM_INDEX:
                raise ValueError(f"Preset '{preset_name}': unknown parameter '{k}'")
        # Build full param map (defaults + overrides)
        params = dict(DEFAULTS)
        params.update(overrides)
        # Validate ranges
        for k, v in params.items():
            if not isinstance(v, int):
                raise ValueError(f"Preset '{preset_name}': non-int value for {k}: {v!r}")

        lines.append(f'  <Item Key="{xml_escape(preset_name)}">')
        lines.append(f'    <Preset Machine="{xml_escape(machine_name)}">')
        lines.append('      <Parameters>')
        # Emit in declaration order, not dict iteration order
        for name, idx in sorted(PARAM_INDEX.items(), key=lambda kv: kv[1]):
            v = params[name]
            lines.append(
                f'        <Parameter Name="{xml_escape(name)}" '
                f'Group="1" Index="{idx}" Track="0" Value="{v}" />'
            )
        lines.append('      </Parameters>')
        lines.append('      <Attributes />')
        lines.append('      <Comment></Comment>')
        lines.append('    </Preset>')
        lines.append('  </Item>')
    lines.append('</PresetDictionary>')
    return "\n".join(lines) + "\n"


def xml_escape(s):
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
             .replace('"', "&quot;").replace("'", "&apos;"))


# ── Main ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    xml = emit_xml(PRESETS)
    out_path = Path(__file__).parent / "Pedal MComp_Presets.prs.xml"
    # UTF-8 with BOM per Build §3.1
    with open(out_path, "w", encoding="utf-8-sig") as f:
        f.write(xml)
    print(f"Wrote {out_path}")
    print(f"  Presets: {len(PRESETS)}")
    print(f"  Parameters per preset: {len(PARAM_INDEX)}")
