# Pedal MComp v1.3

A 4-band stereo soft-knee compressor for ReBuzz, with Linkwitz-Riley 24 dB/oct
crossovers in a balanced binary-tree topology. Each band has independent
threshold / ratio / attack / release / makeup / bypass; knee width, detection
mode (peak/RMS), output gain, dry-wet mix, and a per-band Listen selector are
global. Every parameter slider shows its real-world value (dB / ms / Hz /
ratio / %) rather than a raw integer.

**New in v1.1**: custom GUI embedded in the parameter window — per-band input
and gain-reduction meters, transfer-curve display with operating-point dot,
threshold/ratio readout, bypass overlay.

**New in v1.2**: per-sample denormal protection on all LR4 and detector state
variables; CPU on sustained silence drops from ~50× nominal to typical-bypass
levels (Core §30 pattern).

**New in v1.3**:

- **Lookahead** (0-10 ms log-mapped, off by default). When enabled, audio is
  pre-delayed so the compressor reacts before transients reach the output.
  Adds equivalent processing latency — the wet path and a delay-aligned dry
  path stay phase-coherent so the Dry-Wet mix doesn't comb. Off = true
  zero-latency bypass of the delay machinery.
- **Phase Linear** crossover summation. Adds two all-pass biquads (one per
  inner-split branch, tuned to the opposite branch's crossover frequency) so
  all four band paths share the same phase response after summation. Trade-off
  is a small fixed group delay (1-3 ms depending on crossovers). Off by
  default; existing presets are bit-identical.
- **Spectrum view** (toggle). When enabled, a real-time FFT-based spectrum
  analyser appears below the OUT meter showing the post-effect output
  signal, with crossover-frequency markers overlaid. Widget grows
  vertically when on, returns to the v1.2 footprint when off. Visual
  only — does not affect audio.
- **OUT meter**: stereo post-everything output level at the bottom of the
  widget. Three-zone green/amber/red bars matching the IN meters, with a
  white peak-hold dot that decays slowly so brief peaks remain visible.
  Turns red when peaks reach or exceed 0 dBFS.

## Signal flow

```
                      ┌─ LP@xover1 ──→ LOW ──── compressor ──┐
          ┌─ LP@xover2 ┤                                      │
          │            └─ HP@xover1 ──→ LO-MID ─ compressor ──┤
   input ─┤                                                   ├─→ sum
          │            ┌─ LP@xover3 ──→ HI-MID ─ compressor ──┤
          └─ HP@xover2 ┤                                      │
                       └─ HP@xover3 ──→ HIGH ─── compressor ──┘
```

Every band passes through exactly two LR4 stages, so all four bands share the
same group delay. Sum of all four bands is magnitude-flat for stationary
signals.

## Parameters

All continuous parameters use 0..127 integer encoding with logarithmic
mapping to the physical units. The rack slider displays the unit value via
the `ValueDescriptions` array — setting `L Attack` to 95 shows "30 ms"
rather than "95".

| #  | Name          | Range  | Units                                 |
|----|---------------|--------|---------------------------------------|
| 1  | Listen        | 0-4    | All / L / LM / HM / H                 |
| 2  | Xover L-LM    | 0-127  | 40 Hz – 600 Hz, log                   |
| 3  | Xover LM-HM   | 0-127  | 200 Hz – 3 kHz, log                   |
| 4  | Xover HM-H    | 0-127  | 800 Hz – 12 kHz, log                  |
| 5  | Knee          | 0-24   | dB, shared. 0 = hard knee             |
| 6  | Detection     | 0-1    | Peak / RMS, shared                    |
| 7-12  | L &lt;params&gt;  |    | see below                             |
| 13-18 | LM &lt;params&gt; |    | same six                              |
| 19-24 | HM &lt;params&gt; |    | same six                              |
| 25-30 | H &lt;params&gt;  |    | same six                              |
| 31 | Output Gain   | 0-48   | -24 to +24 dB; index 24 = unity       |
| 32 | Dry-Wet       | 0-100  | %                                     |
| 33 | Lookahead     | 0-127  | Off / 0.1 ms – 10 ms, log; default Off|
| 34 | Phase Linear  | 0-1    | Off / On; default Off                 |
| 35 | Spectrum View | 0-1    | Off / On; default Off (GUI only)      |

### Per-band parameters

| Sub-param  | Range  | Units                                       |
|------------|--------|---------------------------------------------|
| Threshold  | 0-60   | -0 dB to -60 dB below 0 dBFS                |
| Ratio      | 0-127  | log 1:1 to 40:1; index 127 = "Limit" (∞:1)  |
| Attack     | 0-127  | log 0.1 ms to 200 ms                        |
| Release    | 0-127  | log 1 ms to 2000 ms                         |
| Makeup     | 0-24   | dB                                          |
| Bypass     | 0-1    | Off / On; band still routes through crossover |

### Defaults

Crossovers near ~120 Hz / ~700 Hz / ~4 kHz. Threshold -18 dBFS, ratio ~2.5:1
on every band. Attack/release scaled per band — slow on Low (30 ms / 200 ms),
fast on High (3 ms / 30 ms) — reflecting that low-frequency content needs
longer time constants to avoid distortion while high-frequency transients
benefit from quick response. Knee 6 dB, peak detection. Output unity, fully
wet.

## Listen mode

`Listen = All` (default) sums all four bands. Setting it to L / LM / HM / H
solos that band so you can hear what the crossovers are doing during setup.
Per-band Bypass interacts: when you solo a band that's bypassed, you hear the
uncompressed crossover output for that band.

## GUI (v1.1)

The custom GUI embeds in the standard parameter window above the sliders.
Four columns side-by-side, one per band:

```
┌─────────────┬─────────────┬─────────────┬─────────────┐
│      L      │     LM      │     HM      │      H      │
│   ┌──┐ ┌──┐ │   ┌──┐ ┌──┐ │   ┌──┐ ┌──┐ │   ┌──┐ ┌──┐ │
│   │██│ │  │ │   │██│ │█ │ │   │█ │ │██│ │   │█ │ │  │ │
│   │██│ │██│ │   │██│ │██│ │   │██│ │██│ │   │██│ │  │ │
│   │██│ │██│ │   │██│ │██│ │   │██│ │██│ │   │██│ │  │ │
│    IN  GR   │    IN  GR   │    IN  GR   │    IN  GR   │
│             │             │             │             │
│  ┌───────┐  │  ┌───────┐  │  ┌───────┐  │  ┌───────┐  │
│  │   /─  │  │  │   /─  │  │  │   /─  │  │  │   /─  │  │
│  │  /    │  │  │  /    │  │  │  /    │  │  │  /    │  │
│  │ /     │  │  │ /     │  │  │ /     │  │  │ /     │  │
│  └───────┘  │  └───────┘  │  └───────┘  │  └───────┘  │
│  T -18 dB   │  T -18 dB   │  T -18 dB   │  T -18 dB   │
│  R 2.5:1    │  R 2.5:1    │  R 2.5:1    │  R 2.5:1    │
└─────────────┴─────────────┴─────────────┴─────────────┘
```

- **IN meter** — post-crossover, pre-compressor input level. Three-zone
  green/amber/red gradient at -18 dBFS and -6 dBFS.
- **GR meter** — current gain reduction, fills downward from the top. Full
  scale = 24 dB GR.
- **Transfer curve** — input dB on the x-axis, output dB on the y-axis,
  both -60 to 0. Reference diagonal in grey shows the no-compression line.
  Amber dashed line marks the threshold. The cyan curve is the actual
  compression characteristic, recomputed each frame from the same
  `BandCompressor.SoftKneeGR` formula the audio path uses. An amber dot
  marks the current operating point (input level + applied GR), so you
  can see *where on the curve the compressor is sitting right now* —
  ties the meters to the curve at a glance.
- **Threshold / Ratio readout** below the curve.
- **Bypass overlay** — a band shows "BYPASS" overlaid in muted colour
  when that band's bypass switch is on. The IN meter keeps updating
  during bypass (handy for "is this band hot enough to need
  compression?" decisions).

The GUI refreshes at ~30 fps via `DispatcherTimer`. Meter values are
written to `volatile float` fields on each `BandCompressor` from the audio
thread and read from the UI thread — no locks, no allocations per frame
(PedalComp §7 pattern).



## Limitations / future work

- **No latency reporting to the host.** ReBuzz has no plugin-delay-compensation
  mechanism in v1.3, so when Lookahead is non-zero the machine's output is
  delayed relative to non-delayed parallel channels in the rack. The internal
  dry path is delay-aligned so the Dry-Wet mix stays coherent; cross-channel
  alignment is the user's responsibility. Default-off avoids accidental
  surprises.
- **Phase Linear adds group delay, not zero phase.** The all-pass compensation
  makes the four bands sum phase-coherently at crossovers, but the combined
  filter chain has a frequency-dependent group delay — typical 1-3 ms total
  across all all-passes for usual crossover settings. "Phase linear" means
  *aligned across bands*, not *zero delay*.
- **GUI is read-only.** Meters and curves display, but the curves aren't
  click-draggable for parameter editing (use the parameter sliders below
  the embedded GUI). Adding drag-edit on the curves is a v1.x candidate.
- **No sidechain input.** Detection is from the band's own signal only. A
  sidechain input that feeds the detector while the program signal is
  compressed is a v2-class feature.
- **No M/S processing**, **no per-band saturation**, **no auto-makeup**,
  **no A/B compare**, **no internal output limiter** — bigger v2-class items
  that may end up as a separate Pedal MComp Pro machine rather than bolt-ons
  to v1.x.

## Preset bank

20 factory presets ship in `Pedal MComp_Presets.prs.xml`, accessible via the
machine's right-click menu in the rack. Categories follow the `Category -
Description` convention (per Pedal invFFT §23.1), which clusters them
alphabetically in the menu:

| Category    | Count | Use case                                        |
|-------------|-------|-------------------------------------------------|
| Utility     | 2     | Default (matches machine defaults), Bypass     |
| Mastering   | 4     | Transparent, Glue, Punch, Loudness             |
| Mix Bus     | 2     | Tighten, Vintage                                |
| Drum Bus    | 3     | Punch, Smash, Vintage                          |
| Vocal       | 4     | De-Ess, Air, Body Control, Smooth              |
| Bass        | 2     | Tighten, Sub Limit                              |
| FX          | 3     | Pumping, Squash, Lo-Fi                          |

The bank is generated by `gen_presets.py` using the sparse-override pattern
(each preset is a dict of name → value overrides; unspecified parameters
fall back to the machine's `DefValue`). To add or modify presets, edit the
`PRESETS` table in the script and re-run it — the script's helper functions
(`atk`, `rel`, `rat`, `xo1` etc.) let you spell out preset values in
physical units (ms, ratio, Hz) which it translates to the appropriate raw
integer indices.

## Build & deploy

Standard project layout per Build §1.2 / §1.3. `dotnet build` deploys both
the DLL and the preset bundle to `C:\Program Files\ReBuzz\Gear\Effects\`;
close ReBuzz before rebuilding if the files are locked.

If you edit the preset bank, regenerate before rebuilding:

```bash
python3 gen_presets.py    # → Pedal MComp_Presets.prs.xml
dotnet build              # → deploys both files
```

## Files

| File                            | Purpose                                            |
|---------------------------------|----------------------------------------------------|
| `Pedal MComp.NET.csproj`        | Build config; net10.0-windows, Effects deploy     |
| `PedalMComp.cs`                 | Main: parameter declarations + Work loop          |
| `PedalMCompGui.cs`              | WPF GUI (bands, OUT meter, spectrum view)         |
| `BandCompressor.cs`             | Single-band DSP (now with lookahead delay line)   |
| `Crossover.cs`                  | LR4 LP/HP biquad pair, cached coefs               |
| `AllPass.cs` *(new in v1.3)*    | 2nd-order all-pass biquad for Phase Linear        |
| `SpectrumAnalyzer.cs` *(v1.3)*  | Ring buffer + windowed FFT for spectrum view      |
| `FastMath.cs`                   | LinToDb/DbToLin from PedalComp §5                 |
| `gen_param_labels.py`           | Generator for inlined ValueDescriptions arrays    |
| `gen_presets.py`                | Generator for the preset bundle XML               |
| `Pedal MComp_Presets.prs.xml`   | Generated preset bank (20 presets, v1.0)          |
| `README.md`                     | This file                                          |

Both `gen_*.py` scripts live in source for reproducibility but are NOT
deployed to the gear folder (per Build §3.4 / Pedal invFFT §23.4
generator-script convention). The deployed bundle is just the `.dll` plus
the `.prs.xml`.

## Source attribution

The single-band compressor DSP (`BandCompressor.cs`) is a direct port of
Pedal Comp v1, copied rather than referenced — each managed machine in this
project is its own deployable unit and shares no runtime assemblies. The
`FastMath` helpers are likewise duplicated from PedalComp §5. Future v1.x
improvements to either side propagate only to that machine, not the other.
