# MusicPlayer audio engine engineering report

The largest measured defect was output overload under overlapping EQ boosts. At 48 kHz, a 0.99-amplitude 1 kHz sine with all ten bands at +12 dB reached **+6.33 dBFS** at the old device input. The same fixture now reaches **−0.64 dBFS**. A deliberately constructed transient still exceeds full scale and exercises the new overload warning. The ten band centers, Q, slider range and preset values are preserved.

## Original audio architecture

The baseline was the clean `ac5f756` revision on Windows x64. Before changing production code, the Release build completed with zero warnings and errors. All 152 tests passed. Fresh ReportGenerator counters matched the historical counts exactly.

| Stage | Observed behavior at entry |
| --- | --- |
| File selection and decoding | The library selects local MP3 and WAV. In the installed NAudio 3.3.0 filename constructor, MP3 opens a Windows Media Foundation reader. WAV opens a WaveFileReader. Compressed WAV can require an installed ACM codec. TagLib metadata describes the file and does not configure playback. |
| Internal PCM | AudioFileReader uses a SampleChannel with mono-to-stereo conversion disabled. It presents interleaved 32-bit IEEE floats. The decoder supplies the rate and channel count. MusicPlayer had no fixed-rate conversion or remix. |
| Level and filters | Reader.Volume applied master volume or zero for mute before the EQ. For each channel, ten independent NAudio peaking biquads ran in ascending frequency order. The centers in Hz were 31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000 and 16000. Q was 1.4 and the range was ±12 dB. Bands at or above 0.45 times the sample rate were skipped. Active flat sections still ran. NAudio's biquad stored coefficients as doubles and its four sample-history values as floats. |
| Settings and headroom | A lock covered the source read and entire DSP block. A setter retuned each channel immediately under that same lock. Presets called ten setters. Bypass returned source samples and froze filter histories. The output multiplier was 10 raised to minus the largest positive slider gain divided by 20, including inactive bands. NaN gain was rejected, infinities saturated, and master volume accepted NaN. |
| Output | ToWaveProvider exposed the float samples to IWavePlayer as bytes. The app initialized event-driven WinMM WaveOut with that format. Inspecting a new 3.3.0 instance without opening a device gave device −1, two buffers and 100 ms per buffer. There was no app conversion to 16-bit after EQ. |
| Lifecycle and analysis | Open closed the preceding output, allocated a new reader and filters, initialized the device, then raised MediaOpened. Active output disposal waited for PlaybackStopped. Events returned through the WPF dispatcher. Seeking changed the reader position with the old filter histories intact. Independent readers supplied waveform, source levels, frequency analysis and BPM. MainWindow canceled prior jobs on track changes and window close, and checked identity before publishing results. |

## Findings and resolutions

| Component | Problem | Severity | Evidence | Resolution |
| --- | --- | --- | --- | --- |
| Gain staging | Largest-slider attenuation underestimated overlapping peaks. | High | Ten +12 dB bands put a settled sine 6.33 dB over full scale. | Reserve static headroom using an upper bound on the combined steady-state frequency response. |
| Live controls | Immediate gain, headroom and bypass changes could create discontinuities. Presets exposed intermediate settings. | High | On 0.5 DC, a 1 kHz slider change produced a 0.365858 sample step. The UI published ten separate band updates for a preset. | Publish a whole curve and blend two stable cascades over 20 ms. Ramp the final gain per frame. |
| Seeking | A new position could inherit ringing from the previous passage. End-time rounding could also leave the decoder one frame short. | High | An impulse followed by a seek into silence and an 80-frame, 44.1 kHz EOF fixture reproduced the two cases. | Serialize reads with repositioning, invalidate histories, align the next frame, and use the decoder's exact byte length for an end seek. |
| Device lifecycle | Synchronous transport exceptions escaped to WPF. Delayed stops could operate on retired resources or be mistaken for EOF near the end. | High | The old handler disposed stale resources directly. A fake driver reproduced command failures, read-ahead at manual stop, duplicate stops and queued completion across a seek or replacement. | Retire a per-track session, release once, report failures through MediaFailed, and distinguish manual stops and stale dispatch from current EOF. |
| Invalid floats | A malformed float WAV sample could poison recursive filter history. Non-finite controls had inconsistent behavior. | High | The old filter consumed input directly, and the volume clamp admitted NaN. | Replace malformed source samples with zero at the DSP boundary. Reject non-finite settings before modifying the last valid state. |
| Inactive bands | A boost beyond the allowed band range attenuated audible frequencies. | Medium | The 16 kHz slider reduced DC by 12 dB in a 16 kHz file. | Exclude inactive sections from filter design and headroom. An inactive edit also preserves the audible sections' live histories. |
| Filter precision | The combined response deviated from the analytical curve most at high sample rates. | Medium | The tested 192 kHz mixed curve had a worst sampled shape error of 0.120405 dB. | Accumulate and retain filter state in double precision. Skip flat sections. |
| Callback contention | The callback shared the settings lock across decoding and processing. | Medium | Source.Read was inside the UI setter's critical section. | Only positioning synchronizes with decoding. Curve calculation and publication complete while a test decoder is held at a barrier. |
| User feedback | The source meters could not show an overload produced by the EQ. Opening the EQ window also forced processing on. | Medium | A bounded transient through a cut reached 1.883485 at the output. The window constructor called SetEqualizerEnabled(true). | Latch actual post-volume sample overloads, expose a clear action and output trim, and preserve the bypass preference when opening the window. |

The original implementation already isolated channels and applied accurate single-band cuts at ordinary playback rates. The measured faults justified keeping the graphic EQ and revising its implementation.

## Final signal path and DSP design

1. AudioFileReader decodes with its volume at unity. A new session applies the saved settings before device initialization, including mute for devices that prefill during Init.
2. EqualizerSampleProvider receives native-rate interleaved floats. A finite input at unity level passes exactly through a settled flat or bypassed curve. Each active channel and band has its own two double-precision delay values.
3. GraphicEqualizerDesign computes normalized Audio EQ Cookbook peaking coefficients. It uses $A=10^{g/40}$, $w=2\pi f_0/F_s$ and $\alpha=\sin(w)/(2Q)$. The transposed direct-form II implementation takes advantage of the equal normalized $b_1$ and $a_1$ of this specific filter. For valid band centers and gains, $|a_2|<1$ and $1\pm a_1+a_2>0$, the real second-order stability conditions. The response oracle evaluates a separate complex transfer function from the specification. [Audio EQ Cookbook](https://www.w3.org/TR/audio-eq-cookbook/)
4. Each curve has one fixed headroom gain. On each frequency interval, a boosted section reaches its maximum at the point closest to its center because the magnitude increases up to the center and decreases above it. The product of these individual maxima bounds the cascade over the whole interval. Intervals include DC and Nyquist with twelve subdivisions per octave between active centers. Cuts are conservatively bounded by unity. The reciprocal of the largest amplitude bound is applied to the curve. This accounts for peaks between sample points and can reserve more headroom than a particular program requires.
5. The control thread designs and publishes whole curve snapshots. The single render thread owns two preallocated filter banks. During a change it blends their outputs linearly for 20 ms, using one weight for every channel in a frame. Identical leading sections transfer their histories to preserve bass decay during a treble edit. Changed and downstream sections start with cleared delays. Rapid edits coalesce to the latest target after the current blend. The extra bank runs only during a transition. A seek discards both the old transition and its history on the first subsequent nonempty read.
6. Output trim (−24 to 0 dB), the master volume (0 to 1), and mute determine a final scalar. Live changes ramp in linear amplitude for 20 ms, including in bypass. Static settings apply immediately when a stream opens or seeks. A sample whose absolute value exceeds 1 latches a per-track warning. The DSP passes the floating-point result through, so the user can lower trim without also applying compression, hard clipping or automatic program normalization.
7. ToWaveProvider presents float PCM to WaveOut. Windows and the endpoint can mix, convert or reject a format. Already queued buffers may play after a seek or a control change. File I/O remains synchronous inside the decoder read. Source analysis continues on separate readers and cancellation tokens.

WASAPI shared mode offers endpoint format negotiation and, on supported drivers, a shorter shared-engine period. It also changes device selection, buffering and conversion behavior. The measurements isolated defects upstream of the device. A switch requires native endpoint measurements for initialization, resampling, unplug/reconnect and underruns. WaveOut remains the default with its existing buffer configuration. Exclusive output, oversampling, shelves, FIR filtering and a default limiter have no demonstrated requirement for these ten user controls. [NAudio WaveOut](https://naudio.github.io/NAudio/api/NAudio.Wave.WaveOut.html) · [NAudio WasapiPlayer](https://naudio.github.io/NAudio/docs/WasapiPlayer.html) · [Installed NAudio package source revision](https://github.com/naudio/NAudio/tree/4f8889e2bd34c3978a25652c6c172fa1502ccba3)

## Objective measurements

Measurements end at the float sample provider or the simulated device input. The 0.5 DC fixture isolates control-induced discontinuities from the slope of a musical waveform. For the center, inter-band, low-frequency and near-Nyquist response measurements, a one-second unit impulse drives the real provider. Its discrete-time Fourier transform is compared to the analytical cascade after factoring out the measured DC trim. This measures curve shape separately from the static headroom gain.

| Measurement | Original | Final |
| --- | ---: | ---: |
| Flat float bits differing from input, fixed seed plus full-scale, signed-zero and quiet probes, 8 / 44.1 / 192 kHz | 2 per fixture, max absolute error 2.00 × 10⁻¹⁶ | 0 in each of 800 / 4,410 / 19,200 samples, flat and bypass |
| Ten +12 dB bands, 0.99-amplitude 1 kHz sine, 48 kHz, settled sample peak | 2.073071 (+6.3323 dBFS) | 0.928763 (−0.6419 dBFS) |
| Inactive 16 kHz band at +12 dB, 16 kHz source, DC gain | −12.0000 dB | 0.0000 dB |
| 1 kHz band, 0 to +12 dB on 0.5 DC, first sample step | 0.365858 | 0.000381 |
| Largest step anywhere in that transition | 0.365858 | 0.000654 |
| 0.5-amplitude 1 kHz sine, −6 dB band cut, settled fundamental RMS | 0.17719646 | 0.17719645 |
| Residual relative to fitted fundamental, same sine | −144.51 dBc | −146.40 dBc |
| Mean output in the same measurement | −1.49 × 10⁻⁸ | 4.59 × 10⁻¹⁷ |

The residual is the RMS of the error after fitting sine, cosine and DC over 500 complete cycles after 500 settling cycles. It includes numerical noise and harmonic components. It cannot establish hardware THD or audibility.

| Sample rate | Original worst sampled shape error | Final worst sampled shape error | Final mixed-curve DC trim |
| ---: | ---: | ---: | ---: |
| 16 kHz | 0.00226559 dB | 3.67 × 10⁻⁷ dB | −8.25970 dB |
| 44.1 kHz | 0.00326940 dB | 7.41 × 10⁻⁷ dB | −9.41077 dB |
| 48 kHz | 0.01529673 dB | 6.65 × 10⁻⁷ dB | −9.51388 dB |
| 96 kHz | 0.03221273 dB | 2.30 × 10⁻⁷ dB | −10.01059 dB |
| 192 kHz | 0.12040504 dB | 3.77 × 10⁻⁷ dB | −10.15014 dB |

The mixed gains in ascending order were −8, +4, −5, +3, +6, −4, +8, −6, +5 and +9 dB. Measurements included eligible centers, 12, 88, 740 and 11,300 Hz where representable, and 0.49 times the sample rate. Separate tests cover all ten −6 dB centers, bitwise bypass after settling, exact zero on the silent stereo channel through 601 rounds of edits, independent stereo signals and odd read lengths. A two-tone signal scaled by 2⁻²⁰ tracked the same scaling through alternating ±12 dB bands. An impulse tail decayed below 10⁻¹² in the tested final window.

**Peak protection has a measurable limit.** A full-scale bounded sequence constructed from the reversed signs of a cut filter's impulse response reached 1.883485 with the 1 kHz band at −12 dB. The warning latched. Replaying with −12 dB output trim gave 0.473110 and cleared the overload condition. This is sample-peak monitoring. Inter-sample reconstruction peaks and subsequent Windows processing are outside its scope.

## Render performance

Release x64, .NET 10.0.14, Intel Core i7-1065G7, tiered compilation disabled, without coverage instrumentation. Each case reads seeded in-memory audio with ten non-flat bands, warms up for 128 calls and times 512 further reads. The table gives medians of three alternating original/final runs. Timings exclude signal generation, assertions and settings publication. These are elapsed provider times, so disk I/O, process CPU utilization, driver scheduling and speaker latency require separate measurements.

| Source and frames per read | Original µs/read | Final µs/read | Original / final Mframes/s |
| --- | ---: | ---: | ---: |
| 44.1 kHz mono, 64 | 5.47 | 1.77 | 11.69 / 36.16 |
| 48 kHz stereo, 256 | 44.19 | 11.41 | 5.79 / 22.43 |
| 96 kHz stereo, 1,024 | 175.48 | 50.72 | 5.84 / 20.19 |
| 48 kHz stereo, 256, retune every read | 44.28 | 26.26 | 5.78 / 9.75 |

Every final case measured **0 managed bytes inside the 512 Read calls** in all three runs. The original cases usually measured zero, with 7,600 bytes in one 96 kHz run. The benchmark does not identify the source of that outlier. Settings publication builds a small snapshot on the calling thread. In the rapid-edit case it took a median 2.03 µs per edit versus 0.46 µs originally. Callback allocation results apply only to the measured in-memory DSP path.

## Build, test and CI validation

| Release result | Fresh baseline | Final checkout | CI minimum |
| --- | ---: | ---: | ---: |
| Tests total / passed / failed / skipped | 152 / 152 / 0 / 0 | 190 / 190 / 0 / 0 | All tests pass |
| Production lines | 1,228 / 1,403 (87.53%) | 1,386 / 1,563 (88.68%) | 80% |
| Production branches | 459 / 568 (80.81%) | 531 / 642 (82.71%) | 75% |
| Service lines, informational | 564 / 576 (97.92%) | 704 / 718 (98.05%) | — |
| Compiler warnings / errors | 0 / 0 | 0 / 0 | — |

An adjacent full-suite run covered one fewer line in MainWindow's asynchronous UI path (1,385/1,563). Its service and branch counters matched the final run, and both coverage gates passed.

The test project and its application reference restored using the pinned packages in the local NuGet cache. A temporary NuGet configuration and redirected output paths allowed the final Release app and test builds to run in the checkout without changing its tracked legacy Debug outputs. The full suite used the existing Coverlet settings and ReportGenerator 5.6.1 gate. No coverage exclusions or workflow changes were made. The Windows workflow's TRX, Cobertura and summary artifact upload remains configured for 14 days. Remote GitHub Actions was not run.

The standard suite decodes actual integer WAV fixtures at 8, 16, 24 and 32 bits, plus 32-bit float WAV, and inspects PCM delivered to an in-memory output. A separate local Windows codec smoke test encoded a synthetic mono 1 kHz sine as a 128 kb/s MP3, then played it through AudioPlaybackService to an in-memory device. It yielded 45,552 mono float samples at 44.1 kHz with RMS 0.20863690. This codec-dependent check is outside the 190-test count.

To reproduce the suite, gate and render measurements from a Windows checkout with the SDK, packages and ReportGenerator available, use a fresh results directory each time.

```powershell
$project = 'tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj'
$results = Join-Path 'TestResults' ([guid]::NewGuid().ToString('N'))
dotnet restore $project
dotnet build MusicPlayer.csproj -c Release --no-restore
dotnet build $project -c Release --no-restore
dotnet test $project -c Release --no-build --collect 'XPlat Code Coverage' --settings tests/coverage.runsettings --logger 'trx;LogFileName=MusicPlayer.Tests.trx' --results-directory $results
./tests/Measure-Coverage.ps1 -ResultsDirectory $results -MinimumLinePercent 80 -MinimumBranchPercent 75
$env:DOTNET_TieredCompilation = '0'
dotnet test $project -c Release --no-build --filter FullyQualifiedName~MeasureRenderCost --logger 'console;verbosity=detailed'
```

For a historical comparison, export the original revision to a separate directory and run its unmodified suite first. Then copy only `ArraySampleProvider.cs`, `DspMeasurementTests.cs` and `FilterResponseTests.cs` from the current test project into its test directory and run those two test classes in Release with detailed console logging. Both probes use the original public provider interface. The tighter response, overload and transition assertions are expected to fail on the original implementation. Their output includes the measurements before those assertions.

## Changed files and compatibility

| Files | Change |
| --- | --- |
| `Services/GraphicEqualizerDesign.cs` | New filter design, interval headroom bound and per-channel double-precision banks. |
| `Services/EqualizerSampleProvider.cs` | Curve publication, crossfades, seek reset, post-EQ level ramp and overload latch. The public frequency list is read-only to protect the fixed design centers. |
| `Services/AudioPlaybackService.cs` | Decoder/device session ownership, trim, atomic presets, seek and failure handling. |
| `EqualizerWindow.xaml`, `EqualizerWindow.xaml.cs` | Bypass, output trim, clearable overload warning, and whole-preset updates. Reset also clears trim and warning. |
| `MainWindow.xaml.cs` | Set play intent before the driver call so a synchronous device error leaves the UI stopped. |
| `tests/MusicPlayer.Tests/ArraySampleProvider.cs`, `EqualizerTests.cs` in the same directory | Share the existing array fixture. The inactive-band regression now uses a legal −12 dB cut. Separate tests explicitly reject both infinities and NaN. |
| `tests/MusicPlayer.Tests/DspMeasurementTests.cs`, `FilterResponseTests.cs`, `RenderTransitionTests.cs`, `OutputPathTests.cs` | Four new xUnit classes covering response, distortion residual, performance, transitions, level control, formats, seek and device failures. |
| `tests/MusicPlayer.Tests/PlaybackLifecycleTests.cs`, `RecordingOutput.cs`, `WindowIntegrationTests.cs` | Simulate prefill, driver command failures and disposal. The driver-failure regression now verifies release and retries EOF on a fresh device. WPF tests exercise the new controls and warning. |
| `README.md`, `ROADMAP.md`, `AUDIO_ENGINE_REPORT.md` | User controls, signal path, completed and pending roadmap items, measurements and validation record. |

## Remaining limitations and manual listening checklist

Driver acceptance of a given rate or channel layout, actual buffer latency, underruns, endpoint changes, reconnection and acoustic output depend on Windows and the device. Source decoding can still block on slow storage. Closing an active output relies on the driver delivering its stop notification. Device errors are reported and the track can be reopened. A post-volume warning is available in the EQ window. Continuous peak and true-peak meters, endpoint selection, and optional peak limiting remain future work. A crossfade can briefly blend different filter phases. Bypass, volume and mute changes reach the listener after queued audio and the transition time.

The following checks require a person and physical endpoints. They remain open.

- [ ] On headphones and speakers, compare flat EQ and bypass on a familiar track at the same volume and 0 dB trim. Check quiet passages and the stereo center.
- [ ] Drag bass and treble sliders through their ranges and alternate presets and bypass during sustained vocals and bass notes. Listen for clicks, pumping and dropouts.
- [ ] Boost neighboring bass bands on a dense recording at a comfortable listening volume. Check the overload warning, reduce trim, clear it and replay the same passage.
- [ ] Play a left/right identification track and a centered vocal. Check channel orientation, image stability and any change during a preset fade.
- [ ] Switch repeatedly between mono/stereo MP3 and WAV tracks at different rates. Exercise Pause, Play, Next, Previous, Shuffle and Repeat at natural EOF.
- [ ] Seek during a ringing bass note into a quiet section, to the end, then back to the start. Check for stale audio beyond the queued device buffers.
- [ ] Move master volume and trim, mute and unmute, and open a new track while muted. Verify the chosen volume and trim survive a track change.
- [ ] Repeat on built-in, USB and Bluetooth outputs where available. Change the Windows default and disconnect the active device. Record recovery, format incompatibilities, dropouts and measured latency before selecting another backend.

**Suggested commit** `feat(audio): stabilize EQ transitions, headroom and playback lifecycle`
