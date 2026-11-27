# Phase 2B — Interactive parametric equalizer

MusicPlayer’s modeless **Sound → Equalizer** window now edits the same graphic or parametric bank that feeds playback. The response graph, including its individual-band and gain-stage overlays, uses the Phase 2A filter coefficients. Graphic EQ remains the startup mode. The implementation passed its Release build, 248 tests and both local CI coverage gates. Physical playback and Windows interaction sign-off are pending.

## Controls and limits

| Control | Behavior |
| --- | --- |
| Mode and bypass | Choose Graphic EQ or Parametric EQ. Each bank keeps its own settings across mode and track changes for the session. Bypass operates independently. Reopening the menu activates the existing editor. |
| Graphic EQ | Ten ±12 dB sliders, the original four built-in presets and Custom. Manual adjustment selects Custom. Reset clears graphic gains, output trim and the latched overload warning. Presets never change mode. |
| Add and remove | Add inserts an enabled 0 dB bell with Q 0.7. The first band starts at 1 kHz, clamped to the format. Later bands occupy the geometric midpoint of the largest available logarithmic gap. Remove selects the adjacent surviving slot. Zero bands gives a flat filter response. All slots, including disabled ones, count toward the 16-band limit. |
| Select and drag | Choose a band in the selector or click its marker. Bells use circles. Low and high shelves use left- and right-facing triangles. A ring and band number mark the selection. Drag horizontally in log frequency and vertically in dB. The marker represents the parameter gain. For a shelf, the individual response at its transition frequency is half that gain in dB. |
| Precise editing | Frequency is in Hz, gain is −24 to +24 dB, and Q is dimensionless. Higher Q narrows a bell. Bell Q is 0.25–16. Shelf Q is 0.25–√0.5 and controls steepness. Switching to a shelf caps a larger Q at the exact limit and shows a message. |
| Keyboard | Use Tab and Shift+Tab, arrow keys in selectors and sliders, Space for checkboxes, and Enter for numeric edits. Leaving a numeric field also commits it. Escape restores an accepted number or the drag’s starting position. Parsing follows the current culture and rejects nonfinite or out-of-range values. Validation restores the accepted snapshot and displays the reason. |
| Format changes | The editing range is inclusive from `max(5, 0.00001 × sample rate)` to `0.495 × sample rate`. A 16 kHz track supports up to 7,920 Hz. Unavailable bands stay in the selector, retain their preferences and contribute unity until a compatible track opens. Users can toggle or remove them, or enter a supported frequency. Other bands remain editable. |
| No open track | The editor uses a labeled 48 kHz preview from the same design code. It includes the current mode, bypass, trim, volume and mute. Opening a file replaces that preview with the decoded format and published response. |

## Architecture and threading

The previous window built ten sliders in code-behind and applied presets directly to the shared playback service. The Sound menu previously created another modeless editor on every invocation. It now owns one editor at a time and releases the reference on closure. The main window continues to own transport and output-level controls.

- **State ownership.** `AudioPlaybackService` owns both banks, mode, bypass, level and session. Control-thread notifications cover open, close, failure, level, bypass and configuration updates. `EqualizerEditor` reads accepted bands from that service. It submits whole immutable snapshots and updates selection after acceptance. Editor IDs survive ordinary edits and index changes. Whole-bank replacements match equal surviving bands first, then reuse positional slots when possible.
- **Validation.** UI and DSP share sample-rate bounds and Q limits. The editor can retain a previously accepted, out-of-format slot or change its enabled preference on a lower-rate track. A new out-of-range slot is rejected. The existing public settings setter remains strict for an open track. The provider finishes validation and coefficient design before it publishes a target.
- **View.** The window owns controls, numeric drafts, feedback and subscriptions. Level changes update the output display while preserving uncommitted numeric text. Standard controls provide a complete keyboard path. Disabled and overlapping markers remain selectable by band number. Closing the window detaches service notifications and stops the drag timer and existing 250 ms overload-indicator timer.
- **Rendering.** The UI reads an immutable response snapshot on the control thread. The provider takes its settings-writer lock only to capture the target, and the snapshot can then be sampled independently. A frozen WPF drawing group caches the grid and curves. Raw pointer movement invalidates marker drawing. Resize, DPI and response changes invalidate cached graph geometry. The audio callback has no UI subscription or Dispatcher dependency.
- **Dragging.** A 25 ms Dispatcher timer coalesces moves, with at most one pending coordinate pair and one scheduled tick. The point follows raw input. Mouse-up processes the exact final coordinates synchronously. Escape restores the original band. Capture loss, closure, resize, selection, format and mode changes discard uncommitted motion. The audio provider retains its existing approximately 20 ms crossfade and latest-pending-target behavior.

## Response accuracy and gain staging

The solid curve samples `GetDecibels` on the response returned by `GetEqualizerResponse()` during playback. `FillLogFrequencies` supplies 256–512 base samples according to graph width. Parametric band centers add exact samples, and narrow bells receive four extra nearby probes adjusted for their bandwidth near Nyquist. The range covers the entire editable frequency interval. The vertical scale starts at ±24 dB and expands in 12 dB increments to fit the sampled curves. Grid label density also adapts to window height.

| Overlay | Source and interpretation |
| --- | --- |
| Filters only | Combined response of the active, enabled filter sections. Bypassing the EQ gives 0 dB. |
| With headroom | The filter response plus the engine’s automatic attenuation, also shown numerically. A single frequency-independent offset applies to all samples. |
| Final output | Includes automatic headroom, trim, master volume and mute. The graph derives the aggregate offset from the Output-stage API. Zero volume or mute gives an explicit silence label and suppresses the infinite-dB overlay. |
| Selected band | Uses `GetBandDecibels` at the original slot index, before headroom and output level. A disabled band gives 0 dB and remains accessible from the selector. |

The graph describes the requested steady-state response. Crossfade progress, queued device buffers, source spectrum and transient peaks are separate quantities. Automatic headroom is part of the existing engine. The filter-only and compensated overlays let users compare the effect without changing audio processing. The latched overload warning continues to report actual post-volume samples over full scale.

## Release validation

The starting revision was `3cdee83`. The default restore could not read the user NuGet configuration. Baseline and final runs used an isolated copy of the source, a temporary offline NuGet profile and the installed package cache. All 56 source, project and validation inputs checked against the final workspace matched by hash. The Windows .NET SDK was 10.0.403 and the test runtime was 10.0.14.

| Local measurement | Fresh baseline | Final |
| --- | ---: | ---: |
| Release build warnings / errors | 0 / 0 | 0 / 0 |
| Tests passed / failed / skipped | 221 / 0 / 0 | 248 / 0 / 0 |
| Production line coverage | 1,548 / 1,727 (89.64%) | 2,022 / 2,259 (89.51%) |
| Production branch coverage | 640 / 756 (84.66%) | 1,021 / 1,186 (86.09%) |
| CI thresholds, 80% lines and 75% branches | Above both | Both gates passed locally |

The final suite ran both with and without Coverlet. Existing assertions and coverage configuration were preserved. The non-interactive workflow generated the local HTML report and the TRX/Cobertura files. ReportGenerator evaluated the final gate using integer counts. This run did not trigger remote GitHub Actions.

- **Signal and graph.** Tests checked unity, bell boosts and cuts, both shelves, high-Q centers, a +48 dB stack, headroom, output attenuation and silence. Display samples were compared against the DSP API at each plotted frequency. A separate test drove the WPF controls while rendering a decoded 1 kHz WAV through an in-memory output. The settled PCM followed the requested −9 dB cut across seek, bypass and both mode switches.
- **Editor and window.** Tests covered local-culture and malformed input, exact Q limits, rollback, band limit, immutable settings, stable selection, external replacements, two decoded formats, keyboard commit/cancel, disabled slots and close-time detachment. A 300-move burst produced one timed publication and a second exact mouse-up publication. Canceled, mode-switched and closed gestures discarded pending work.
- **Rendering and layout.** STA tests rendered native WPF bitmaps and checked both curve colors, selection, shelves, silence and small dimensions. An additional offscreen window review checked both modes at nominal and reduced sizes. Vertical tick spacing was adjusted after inspecting the reduced graphic layout. The offscreen session could not acquire native mouse capture. Physical pointer delivery, screen-reader announcements and per-monitor scaling remain manual checks.

To reproduce on Windows with the .NET 10 SDK and ReportGenerator available:

```powershell
dotnet build MusicPlayer.csproj --configuration Release
dotnet test tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj --configuration Release
.\launch_coverage_report.ps1 -NoOpen
$run = Get-ChildItem TestResults/local -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
.\tests\Measure-Coverage.ps1 -ResultsDirectory $run.FullName -MinimumLinePercent 80 -MinimumBranchPercent 75
```

## Performance

Windows x64, Intel Core i7-1065G7, Release, tiered compilation disabled, no coverage collector. The sampling loop used Q=16 bells with alternating boost/cut, a selected-band overlay, 12 warmups and 101 timed calls. Frequencies, samples and overlay arrays are allocated on the UI thread when the curve changes. These measurements exclude audio-device I/O.

| Active bands | Base samples | Sampling median | Sampling p95 | Managed bytes per sampling call |
| ---: | ---: | ---: | ---: | ---: |
| 3 | 256 | 0.137 ms | 0.236 ms | 10,968 |
| 5 | 512 | 0.329 ms | 0.504 ms | 21,528 |
| 16 | 512 | 0.739 ms | 0.993 ms | 23,288 |

A separate STA probe changed a gain on a 48 kHz decoded session with 16 active narrow bells, alternated the graph width between 748 and 760 DIPs, and synchronously rendered a 760 × 270 software bitmap. Across 51 measured edits after five warmups, median time was **15.34 ms** and p95 was **45.71 ms**. Some software-rendered frames exceeded the 25 ms drag publication interval. A burst of 100 mode switches took **29.58 ms** including synchronous control updates, with painting deferred. Display frame rate and endpoint underruns require a physical Windows session.

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet test tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~MeasureWindowEditsResizeAndModeSwitches|FullyQualifiedName~MeasureResponseSampling' --logger 'console;verbosity=detailed'
```

## Changed files

| File | Responsibility |
| --- | --- |
| `EqualizerWindow.xaml` | Both mode panels, response-stage selector, accessible numeric fields, band management, trim and overload. |
| `EqualizerWindow.xaml.cs` | Window synchronization, graphic presets, numeric commits, rollback, stage display and cleanup. |
| `MainWindow.xaml.cs` | Own and reactivate a single modeless EQ window. |
| `Equalizer/EqualizerEditor.cs` | Stable band selection, validated edits, add/remove defaults and format-aware service integration. |
| `Equalizer/ResponsePlot.cs` | Coordinate transforms, precise response samples, overlays and adaptive gain bounds. |
| `Equalizer/ResponseGraph.cs` | Native WPF drawing, marker shapes, hit testing, capture and bounded drag scheduling. |
| `Services/AudioPlaybackService.cs` | Control-thread change notifications, decoded format, preview responses and retained-slot edits. |
| `Services/ParametricEqualizerSettings.cs` | Shared DSP frequency and Q bounds used by the editor. |
| `tests/MusicPlayer.Tests/EqualizerEditorTests.cs` | Selection, validation, independent modes, rate changes and strict publication. |
| `tests/MusicPlayer.Tests/ResponsePlotTests.cs` | Log and dB mapping, per-sample DSP agreement, axis bounds and sampling timings. |
| `tests/MusicPlayer.Tests/EqualizerWindowEditorTests.cs` | STA control and drag interactions, decoded PCM checks, software rendering and UI timings. |
| `README.md`, `ROADMAP.md`, `PARAMETRIC_EQ_UI_REPORT.md` | User controls, milestone status, engineering measurements and verification checklist. |

## Manual Windows sign-off

Use a Windows output device with the Release app. Start at a comfortable listening level. Use a familiar stereo track and two WAVs with different decoded sample rates.

1. Open **Sound → Equalizer** twice. Check that one window stays open, responds to keyboard focus and allows playback from the main window. Try each graphic preset, move a slider, then reset.
2. Choose Parametric EQ. Add a band and use Tab, arrows, Space and Enter to set a 1 kHz bell to −9 dB with Q 2. Try an invalid value and cancel an edit with Escape. Confirm the displayed and audible band agree.
3. Add both shelf types. Drag the markers slowly, rapidly and past the plot edges. Release the mouse outside the plot, cancel a second drag with Escape, and close the window mid-drag. Reopen it and check selection, capture release and responsiveness.
4. Toggle a band, remove a middle slot, fill all 16 slots and remove them all. Check the selection, flat empty curve and disabled Add button at capacity.
5. Boost two nearby bands. Compare **Filters only**, **With headroom** and **Final output**. Adjust trim, master volume and mute. Bypass and re-enable during playback. Check that the headroom display and silence indication follow the controls, and listen for transitions.
6. Keep the editor open and switch to a lower-rate track with a retained high-frequency band. Edit an available band, seek, then return to the original track. Check the retained band’s frequency and listen for stale audio or dropouts.
7. Drag at maximum band count while resizing. Check the minimum window size and 100%, 150% and 200% Windows scaling, including moving the window between displays. Verify axis labels, numeric fields, focus indicators, Narrator control names and the validation message.

## Phase 2C readiness

The playback service already accepts immutable ordered band snapshots and independently stores the ten graphic gains and selected mode. A versioned preset can store the mode, numeric filter parameters and enabled flags, then submit one validated settings replacement on load. Editor selection IDs are session-local and need no serialization. Naming, saving, loading and deleting custom presets belong to Phase 2C. Device selection and endpoint settings belong to Phase 2D.

**Suggested commit** `feat(eq): add interactive parametric editor and DSP response graph`
