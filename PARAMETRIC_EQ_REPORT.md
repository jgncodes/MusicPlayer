# Phase 2A · Parametric equalizer DSP

MusicPlayer can now render an ordered cascade of up to 16 configurable bells and shelves through its existing playback provider. The ten-band graphic EQ remains the startup mode and the EQ exposed by the WPF window. The parametric DSP and response graph data are available through C#.

## Filter design and limits

| Parameter | Accepted values and meaning |
| --- | --- |
| Filter type | `Peaking`, `LowShelf`, `HighShelf` |
| Frequency | Hz, inclusive range `max(5, 0.00001 × sampleRate)` through `0.495 × sampleRate`. Bell center or shelf half-gain frequency. The margins keep filter design away from the DC and Nyquist singularities. Response queries may use either endpoint. |
| Gain | −24 to +24 dB per parametric band. A 0 dB band is an identity section. Graphic sliders retain ±12 dB. |
| Q | Bell 0.25–16. Shelf 0.25–√0.5. The default is 0.7. Lower Q widens a bell or makes a shelf more gradual. |
| Band count and order | 0–16 slots including disabled bands. The renderer follows the supplied order and skips disabled and unity sections. Response indices still refer to the original slots. |

The normalized transfer function is `(b0 + b1 z⁻¹ + b2 z⁻²) / (1 + a1 z⁻¹ + a2 z⁻²)`. The cookbook designs use `A = 10^(gainDb/40)`, `ω = 2πf/fs`, and `α = sin(ω)/(2Q)`. For shelves, `1/Q² = (A + 1/A)(1/S − 1) + 2`. At the maximum shelf Q, S is 1. Smaller Q keeps the shelf monotonic at every supported gain. Coefficients are divided by a0 and checked for finiteness and strict second-order pole stability using `|a2| < 1` and `1 ± a1 + a2 > 0`. The transposed direct-form II recurrence subtracts the denominator feedback terms. [Robert Bristow-Johnson, *Audio EQ Cookbook*](https://www.w3.org/TR/audio-eq-cookbook/)

## Processing and integration

The audited path is `AudioFileReader → EqualizerSampleProvider → ToWaveProvider → WaveOut`. Decoder format determines the sample rate and interleaved channel count. On open, the service loads both mode preferences, bypass and output level before device initialization. Each session owns one provider and its banks. The existing session cleanup releases the driver and reader on retirement or failure. A new song gets fresh banks. Seeking resets both the channel cursor and effective processing history on the next nonempty read.

- **Modes.** `Mode` selects the graphic or parametric bank design. `SetEqualizerEnabled(false)` bypasses the selected mode. Re-enabling restores that selection. Graphic presets and sliders always edit graphic preferences. The programmatic `ResetEqualizer()` clears both sets of bands, trim and overload. The existing graphic window retains its graphic reset.
- **Publication.** The service uses its existing UI/control-thread convention. Settings copy the caller's bands. A provider setter validates and designs a complete curve under the settings-writer lock before publishing one reference. Invalid edits leave the previous preference and curve intact. With a file open, all submitted bands must fit its sample rate. With no file open, rate-independent validation applies. When opening a track, the service omits previously stored frequencies outside its range, giving those slots unity response and retaining their values for a compatible track.
- **Rendering.** One render thread owns two fixed banks, each with two double-precision delay values per channel per possible section. Float PCM enters and leaves the provider. A partial channel frame continues on the following read. The renderer acquires the existing decoder/seek lock around source reads. Coefficient design, graph calculations, allocations and settings locks stay outside the sample loop.
- **Live edits.** Each read samples the latest target. At a frame boundary, both stable cascades process the same input and their headroom-adjusted outputs crossfade linearly over approximately 20 ms. Identical upstream sections copy their channel histories. Changed sections and their downstream dependents start with cleared delays. An edit mid-fade replaces the pending target. The current fade finishes before another begins. There are at most two rendering banks and one latest target. Keeping the existing duration gives graphic, parametric and bypass changes the same bounded scheduling policy. Different filter phases can briefly cancel, and a newly introduced high-Q bass filter can take longer than the fade to settle.

### Headroom and meters

Graphic EQ keeps its original interval headroom calculation. For parametric EQ, a grid includes DC, Nyquist, semitone subdivisions and every active center. Within an interval each supported section is monotonic in magnitude. Multiplying the larger endpoint magnitude of each section bounds the cascade there, even when individual maxima occur at different frequencies. The reciprocal of the largest interval bound, capped at unity, supplies fixed headroom. A flat or all-cut curve reserves 0 dB.

The shared output stage then ramps trim, volume and mute. Bypass retains those controls. The bound describes steady-state sinusoidal gain. Transients can exceed full scale, and the existing warning latches actual post-level samples with magnitude above 1. Normal over-range samples pass through. A conversion beyond float's representable magnitude saturates at `float.MaxValue` with the original sign.

## Frequency-response API

```csharp
var settings = new ParametricEqualizerSettings(
    new(ParametricFilterType.LowShelf, 120, 4, Q: 0.65),
    new(ParametricFilterType.Peaking, 2300, -3, Q: 2),
    new(ParametricFilterType.HighShelf, 9000, 2));

player.ParametricSettings = settings;
player.Mode = EqualizerMode.Parametric;
if (player.GetEqualizerResponse() is { } response)
{
    var frequencies = new double[256];
    response.FillLogFrequencies(frequencies, 20, response.SampleRate * 0.5);
    double bandDb = response.GetBandDecibels(1, 2300);
    double filtersDb = response.GetDecibels(2300);
    double withHeadroomDb = response.GetDecibels(2300, EqualizerResponseStage.WithHeadroom);
    double deviceInputDb = response.GetDecibels(2300, EqualizerResponseStage.Output);
}
```

The snapshot evaluates the exact published coefficients at `z⁻¹ = exp(−j2πf/fs)` and multiplies the complex responses. It describes the steady-state target at capture time. `HeadroomDb` exposes automatic attenuation. `Output` includes the requested trim, volume and mute and returns negative infinity for silence. A bypassed or disabled filter contributes 0 dB. A closed service returns null. For offline use, `settings.CreateResponse(sampleRate)` performs strict format validation and assumes unity output level. Neither API requires WPF.

## Validation

Windows x64, .NET SDK 10.0.403, runtime 10.0.14. The starting revision was `031e791`, with a clean working tree. The default NuGet user configuration was unreadable in the execution environment. Both builds used the installed package versions from the local cache, a temporary offline NuGet profile and isolated restore metadata. The repository's Coverlet settings and ReportGenerator 5.6.1 gate supplied these counters.

| Release validation | Before | After |
| --- | ---: | ---: |
| WPF build warnings / errors | 0 / 0 | 0 / 0 |
| Tests passed / failed / skipped | 190 / 0 / 0 | 221 / 0 / 0 |
| Production lines | 1,386 / 1,563 (88.68%) | 1,548 / 1,727 (89.64%) |
| Production branches | 532 / 642 (82.87%) | 639 / 756 (84.52%) |

Both local CI thresholds passed (80% lines, 75% branches). All pre-existing test cases ran with their original assertions.

| Probe | Result |
| --- | --- |
| Independent bilinear-prewarped analog prototype | At 8, 44.1, 48, 96, 192 and 384 kHz, 324 designs covered gain and Q endpoints, unity, and centers near DC, midband and Nyquist. Largest absolute error across probes was 0.00000192 dB. All computed poles were inside the unit circle. Largest radius was 0.999999357803. |
| Measured impulse transform | A half-second rendered impulse, evaluated directly at DC and 65 log-spaced frequencies including Nyquist, agreed with the overall response to within 0.000000734 dB at 44.1, 96 and 192 kHz. A separate coherent three-tone measurement included a full second of settling. |
| Transitions and state | Exact two-bank reference mixes passed across split stereo frames in both mode directions and bypass. Repeated edits during a 0.5 DC fixture produced a largest adjacent step of 0.000791371. A 16-slot edit retained upstream bass decay. Mono, stereo and six-channel reads matched independent mono renders. |
| Gain and lifecycle | Narrow overlapping bells plus shelves required more than 39 dB of headroom. A bounded transient triggered the overload latch. Real WAV decoding into an in-memory output exercised pre-device gain, seek during a fade, format changes, stored frequencies becoming available again and reopening after a simulated driver failure. |

### Render benchmark

Intel Core i7-1065G7, Windows build 26200, Release, tiered compilation disabled, no coverage collector. Each case used seeded input, 128 warmup reads and 513 timed reads. Only the in-memory source copy and provider processing were inside the read timer. Input generation, assertions, sorting, settings design and allocation-counter calls were outside it. Each cell below is the median of three run medians, in microseconds per read. Baseline and final processes alternated with the same PowerShell processor-affinity mask of 4. The baseline used the original production sources and the same graphic/bypass measurement loop.

| Hz / channels / frames | Graphic 10 before | Graphic 10 after | Bypass after | 3 bells | 5 mixed | 5 mixed, live | Live throughput |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 44,100 / 1 / 64 | 5.4 | 2.0 | 0.5 | 1.0 | 1.4 | 2.4 | 605× |
| 44,100 / 2 / 256 | 37.3 | 14.6 | 3.1 | 6.6 | 9.3 | 19.0 | 306× |
| 48,000 / 1 / 256 | 22.0 | 8.0 | 1.6 | 3.4 | 4.3 | 9.8 | 544× |
| 48,000 / 2 / 64 | 10.6 | 3.7 | 0.9 | 1.6 | 2.1 | 4.6 | 290× |
| 48,000 / 2 / 256 | 42.2 | 14.6 | 3.3 | 6.5 | 8.4 | 18.8 | 284× |
| 96,000 / 2 / 256 | 41.2 | 15.5 | 3.8 | 6.9 | 8.4 | 19.2 | 139× |

All measured workloads allocated **0 managed bytes per read**. Mixed configurations used a low shelf, three bells and a high shelf. The live case alternated the first shelf at 60 updates per second of audio, quantized to block boundaries. Settings design took 80–318 µs/update across individual run means. Throughput is the audio duration of a block divided by its median processing time. Process scheduling and clock speed affected absolute results. The table measures processing capacity on this host. Driver latency, scheduling tails and underruns require endpoint testing.

To reproduce locally, run `dotnet build MusicPlayer.csproj -c Release`, `dotnet test tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj -c Release` and `.\launch_coverage_report.ps1 -NoOpen`. Run `tests/Measure-Coverage.ps1` on the new results with 80/75 minima. For detailed timings after the build, use PowerShell:

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet test tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj -c Release --no-build --filter FullyQualifiedName~EqualizerBenchmarkTests --logger 'console;verbosity=detailed'
```

## Changed files

| Files | Responsibility |
| --- | --- |
| `Services/ParametricEqualizerSettings.cs` | Mode and filter types, immutable configuration, validation, design and parametric interval headroom. |
| `Services/BiquadSection.cs`, `Services/EqualizerCurve.cs` | Shared normalized coefficients, transfer function, compact active cascade and per-channel filter banks. |
| `Services/GraphicEqualizerDesign.cs` | Render graphic curves through the shared biquad implementation while retaining graphic center, Q and headroom rules. |
| `Services/EqualizerSampleProvider.cs`, `Services/AudioPlaybackService.cs` | Publish mode-specific settings, expose target responses and restore preferences for each decoded format. |
| `Services/EqualizerResponse.cs` | Individual and combined dB responses, selectable gain stages and log spacing. |
| `tests/MusicPlayer.Tests/ParametricResponseTests.cs`, `ParametricRenderTests.cs`, `ParametricPlaybackTests.cs` (same directory) | Independent response and stability oracles, state and transition tests, gain monitoring and decoded-stream integration. |
| `tests/MusicPlayer.Tests/EqualizerBenchmarkTests.cs` | Repeatable rendering and control-update measurements. |
| `README.md`, `ROADMAP.md`, `PARAMETRIC_EQ_REPORT.md` | Current validation, completed DSP foundation, remaining milestones and engineering evidence. |

## Physical verification and deferred work

Physical playback, listening and remote GitHub Actions were unverified in this run. The following checks require a Windows endpoint.

- [ ] Play mono and stereo WAV and MP3 in the Release app. Compare flat graphic EQ and bypass on headphones, then exercise presets, trim, mute and seek.
- [ ] Through a development caller using the API above, switch between graphic, parametric and bypass during sustained bass and vocals. Sweep gain, Q and frequency and listen for clicks, phase changes and dropouts.
- [ ] Change tracks across sample rates, seek into silence and reopen after disconnecting an output. Check channel orientation, recovery, overload indication and audible latency on each target endpoint.

Phase 2B covers the advanced interface and response visualization. Phase 2C covers custom preset persistence and management. Phase 2D covers playback output settings and endpoint validation.

**Suggested commit** `feat(audio): add parametric EQ with shared transitions and frequency response`
