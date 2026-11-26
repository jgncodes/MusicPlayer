# MusicPlayer coverage audit

Measured on Windows with .NET SDK 10.0.403 and runtime 10.0.14 on November 25, 2026. The baseline was built from `a73ca17`. The final measurement used the working tree and the CI collector settings.

## Release results

| Measurement | Original | Final |
| --- | ---: | ---: |
| Discovered / passed / failed / skipped | 102 / 102 / 0 / 0 | 152 / 152 / 0 / 0 |
| Production lines | 415/1,390 (29.86%) | 1,228/1,403 (87.53%) |
| Production branches | 159/564 (28.19%) | 461/570 (80.88%) |
| Service lines | 415/564 (73.58%) | 564/576 (97.92%) |
| Service branches | 159/250 (63.60%) | 238/254 (93.70%) |

The table above records the historical Coverlet root counters. The original collector output contained one assembly, `MusicPlayer`. The checked-in settings select that same assembly and all its types. This excludes the test assembly, third-party libraries and tooling. There are no application file, namespace or attribute exclusions configured here. WPF-generated connector sequence points mapped to authored XAML stay in the denominator. Compiler infrastructure without reported source sequence points, declarations, and XAML markup without executable mappings contribute no lines. The core subtotal covers every source file under `Services/`, including the playback service's dispatch adapter. The WPF bitmap model `SongFile` is included in the overall metric and shown separately. The HTML report's class-level aggregation displays 459/568 branches. CI now compares both ReportGenerator fractions directly from its JSON summary. The minimums are 80% of production lines and 75% of production branches.

## Coverage by source file

Counts show covered/coverable sequence-point lines or branches. The audit used a 70% project line gate and a 90% service line goal. Individual WPF files have no quota.

| File | Baseline lines | Final lines | Baseline branches | Final branches |
| --- | ---: | ---: | ---: | ---: |
| App.xaml | 0/1 | 0/1 | — | — |
| EqualizerWindow.xaml | 0/3 | 3/3 | 0/6 | 5/6 |
| EqualizerWindow.xaml.cs | 0/131 | 131/131 | 0/22 | 22/22 |
| MainWindow.xaml | 0/21 | 20/21 | 0/44 | 41/44 |
| MainWindow.xaml.cs | 0/612 | 452/613 | 0/228 | 140/228 |
| Services/AudioAnalysisService.cs | 266/347 | 336/348 | 93/146 | 137/146 |
| Services/AudioPlaybackService.cs | 41/109 | 115/115 | 17/54 | 50/56 |
| Services/EqualizerSampleProvider.cs | 70/70 | 75/75 | 20/20 | 22/22 |
| Services/MusicLibraryLogic.cs | 20/20 | 20/20 | 3/4 | 3/4 |
| Services/PlaybackQueue.cs | 18/18 | 18/18 | 26/26 | 26/26 |
| SongFile.cs | 0/58 | 58/58 | 0/14 | 13/14 |

ReportGenerator groups nested async methods and XAML mappings into the owning class. Its class line percentages are shown at its displayed precision.

| Class | Baseline | Final |
| --- | ---: | ---: |
| App | 0% | 0% |
| MainWindow | 0% | 74.4% |
| EqualizerWindow | 0% | 100% |
| AudioAnalysisService | 76.5% | 96.3% |
| AudioMetadata | 0% | 100% |
| FrequencyFrame, FrequencyAnalysis, TempoEstimate (each) | 100% | 100% |
| AudioPlaybackService | 39% | 100% |
| PlaybackErrorEventArgs | 0% | 100% |
| EqualizerSampleProvider, PlaybackQueue, MusicLibraryLogic (each) | 100% | 100% |
| SongFile | 0% | 100% |

## Tests and production changes

| Component | Regression coverage and implementation |
| --- | --- |
| Equalizer | 13 additional cases in `EqualizerTests` check all ten band centers against a measured 6 dB cut, gain changes on a live stream, low sample rates, NaN handling, and equivalence across stereo read sizes. An odd-sized stereo read formerly restarted at channel zero and produced a 0.0308 sample error against the single-read reference. `EqualizerSampleProvider.cs` now carries the channel offset across reads, including bypass. A shared gain clamp rejects NaN before mutating filter or playback settings. Existing clamping of out-of-range gains is retained. |
| Queue and library | One large-queue regression returned −2 where Next should wrap to 0. `PlaybackQueue.cs` now widens the intermediate sum. The existing seeded test feeds the selected index into the next navigation step and checks that consecutive tracks differ. Two `LibraryBoundaryTests` cover discovery failure and sorted Unicode, punctuation and similar names in a 257-file directory. Deleted-after-discovery metadata fallback is covered with the song model. |
| Playback | Six new `PlaybackLifecycleTests` cover decoder output, applying saved and live gains, mute, seek, duration, state transitions, EOF, driver errors, stale callbacks before and after dispatch, cleanup, malformed/missing files and recovery. `AudioPlaybackService.cs` accepts an internal NAudio output factory and dispatch delegate. The public constructor uses WaveOut and the WPF dispatcher. A failing factory previously leaked the reader. The fix disposes partially initialized resources. The regression requires an exclusive file reopen and a successful second playback attempt. The idle test now checks events and state. |
| Analysis | Nineteen `AnalysisRecoveryTests` use locked inputs to prove cache hits, modify file contents at the same length, corrupt waveform and tempo entries, simulate unavailable and locked caches, and verify recovery and temporary-file cleanup. DSP cases cover partial stereo frames, nonfinite samples, quiet audio, band boundaries, and seeded noise over rhythmic pulses. A canceled frequency analysis previously opened a missing file and raised FileNotFoundException. `AudioAnalysisService.cs` checks cancellation before I/O and separates the sample loop so a source can cancel during a partial read. An internal data-directory constructor isolates caches, including those in the existing waveform and tempo suites. |
| Song metadata and artwork | Three `SongFileTests` cover identity, real TagLib BPM precedence, rounded estimates, property notifications, normalized artwork keys, corrupt images and frozen decoded images after file removal. `SongFile.cs` uses the analysis profile to resolve its artwork location. The public default path and cache-key algorithm are preserved. A draft test expected a hyphen for missing metadata. The product's existing em dash is the documented test expectation. |
| WPF | Four `WindowIntegrationTests` exercise the actual controls, hidden on STA dispatchers. Scenarios include changing songs while async analysis is pending, search filtering and selection restoration, play/pause, seek and thumb release, waveform position, frequency updates, repeat, shuffle, EOF, volume/mute, decoder failure and recovery, presets/manual edits/reset, and closing during analysis. `MainWindow.xaml.cs` accepts a player, analysis profile and error presenter through an internal constructor and groups the current track's analysis tasks for deterministic completion. The normal constructor still loads the user profile and displays WPF warnings. |
| Project | `MusicPlayer.csproj` grants internal access to the test assembly so the test dependencies stay out of public APIs. Both projects build in Release with zero warnings. No package versions changed. |

The original cache-repeatability assertions compared two outputs that could both have been recomputed. Reading from a valid cache while the source WAV is exclusively locked distinguishes a cache hit. Original flat-EQ and shuffle tests also had full line coverage while missing split-frame processing and integer overflow. The existing fixture-only tests still run and contribute zero production lines on their own.

## Validation log

Each checkpoint came from a fresh Release TRX and Cobertura attachment. All completed checkpoint runs had zero failures and skips.

The final suite also passed three consecutive Release runs without instrumentation. A further Release build-and-test using the repository's original restored package paths passed all 150 cases. The temporary WAVs and private test profiles were absent after these runs.

| Checkpoint | Passed | Lines | Branches |
| --- | ---: | ---: | ---: |
| audit-baseline | 102 | 415/1390 | 159/564 |
| audit-dsp | 115 | 420/1395 | 161/566 |
| audit-playback | 121 | 494/1401 | 194/568 |
| audit-analysis | 139 | 612/1400 | 244/568 |
| audit-wpf | 142 | 1212/1401 | 449/570 |
| audit-boundaries | 148 | 1220/1403 | 451/570 |
| audit-final | 150 | 1224/1403 | 457/570 |
| recheck-final | 152 | 1228/1403 | 461/570 |

From the repository root, the following commands reproduce the validation stages. Use a fresh results directory for every measured run.

```powershell
$project = 'tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj'
# Command used at the original baseline and each pre-CI checkpoint.
$checkpoint = 'audit-baseline'
dotnet test $project -c Release --no-restore '--collect:XPlat Code Coverage' --logger 'trx;LogFileName=MusicPlayer.Tests.trx' --results-directory "TestResults/$checkpoint"
$baseline = (Get-ChildItem TestResults/audit-baseline/*/coverage.cobertura.xml).FullName
reportgenerator "-reports:$baseline" '-targetdir:TestResults/audit-baseline/html' '-reporttypes:Html;TextSummary'

dotnet build MusicPlayer.csproj -c Release --no-restore
dotnet build $project --configuration Release --no-restore
dotnet test $project --configuration Release --no-build '--collect:XPlat Code Coverage' --settings tests/coverage.runsettings --logger 'trx;LogFileName=MusicPlayer.Tests.trx' --results-directory TestResults/audit-final
./tests/Measure-Coverage.ps1 -ResultsDirectory TestResults/audit-final -MinimumLinePercent 70
dotnet test $project -c Release --no-build
git diff --check
```

Focused runs used `dotnet test $project -c Release --no-restore --filter '<expression>'` with these expressions as the batches evolved.

| Filter expression | Outcome |
| --- | --- |
| `FullyQualifiedName~EqualizerTests\|FullyQualifiedName~PlaybackQueueTests` | Three regressions reproduced, then 45 passed. |
| `FullyQualifiedName~PlaybackLifecycleTests` | One reproducible reader leak, then six passed. |
| `FullyQualifiedName~AnalysisRecovery\|FullyQualifiedName~SongFileTests\|FullyQualifiedName~TempoDetection\|FullyQualifiedName~WaveformTests` | Corrected the fixture's placeholder expectation, then 33 passed. |
| `FullyQualifiedName~WindowIntegration` | An initial fake driver emitted stop notifications while already stopped and aborted the test host. Correcting the test double produced a passing run. The final group passed all four tests. |
| `FullyQualifiedName~AnalysisRecovery\|FullyQualifiedName~LibraryBoundary\|FullyQualifiedName~PlaybackQueue` | A pre-canceled operation reproduced the wrong exception. |
| `FullyQualifiedName~AnalysisRecovery\|FullyQualifiedName~LibraryBoundary\|FullyQualifiedName~PlaybackQueue\|FullyQualifiedName~EqualizerTests\|FullyQualifiedName~WindowIntegration` | 69 passed after the cancellation fix and boundary additions. |
| `FullyQualifiedName~AnalysisRecovery\|FullyQualifiedName~WindowIntegration\|FullyQualifiedName~Playback` | 75 passed including cache-lock and close-during-analysis checks. |

The CI command also ran with `--results-directory TestResults/ci`. The gate accepted that report and rejected the untouched original report at 70%. The CI workflow preserves the TRX and Cobertura artifacts on failure. The hosted GitHub runner was not invoked.

Ordinary `dotnet restore $project` encountered a local access denial on the user's NuGet.Config. Validation used a temporary config, a local feed of the exact cached package versions, and a fresh package directory. The successful restore command was `dotnet restore $project --configfile $env:RestoreConfigFile --source $feed.FullName`. Both projects restored. These environment overrides are specific to this validation host.

The unsigned launcher ran under a process-scoped execution policy. `pwsh -ExecutionPolicy Bypass -File ./launch_coverage_report.ps1` produced the report, then Windows denied the default-browser launch. `pwsh -ExecutionPolicy Bypass -File ./launch_coverage_report.ps1 -NoOpen` completed successfully. No machine policy was changed.

## Remaining gaps

- Artwork import has an uncovered error path worth testing. The local size check and the download's redirect, media type and size checks throw `InvalidDataException`. That exception derives from `SystemException` and escapes both handlers' catch filters, so a rejected image can bypass the intended error dialog. This finding came from reviewing the uncovered source. The gate change does not exercise the dialog.
- The main window accounts for 161 uncovered code-behind lines. Native file/folder selection, setting or clearing the default folder through dialogs, URL entry, HTTP redirect/content/size checks, local artwork import/save, mouse hit testing and visible child-window behavior remain outside the automated paths. Actual image decoding and cached artwork loading are covered independently.
- The suite substitutes audio output. Driver initialization, device removal, native callback scheduling, sound at the speakers and end-to-end MP3 codec behavior require separate Windows integration coverage. Real WAV decoding and the samples sent to the device are exercised.
- Folder ACL denial, some cache-permission and cleanup failures, invalid channel/rate headers rejected by decoders, and a few estimator rejection branches remain uncovered. Tests use sharing violations and a file in place of a cache directory for repeatable I/O failures. The tempo fixtures establish behavior on synthetic audio and do not measure accuracy on commercial recordings.
- App startup through the generated entry point and OS-driven gestures remain manual. Hidden-window tests instantiate the compiled XAML and use routed controls directly. CI enforces production line and branch floors. The service subtotal is informational.

## Working-tree revalidation

The incoming working tree already contained the 150-case expansion. Its fresh Release run passed all 150. Two additional SongFile theory cases decode real stereo and six-channel WAV metadata and check duration, channel layout, sample rate and missing BPM. No further production edits were necessary. Five focused SongFile cases passed, followed by all 152 in Release. The local HTML launcher also completed with -NoOpen. Browser handoff and hosted Actions were not exercised in this recheck.

```powershell
dotnet test tests/MusicPlayer.Tests -c Release --no-restore --collect 'XPlat Code Coverage' --settings tests/coverage.runsettings --results-directory TestResults/recheck-entry --logger trx
dotnet test tests/MusicPlayer.Tests -c Release --no-restore --filter 'FullyQualifiedName~SongFileTests'
dotnet test tests/MusicPlayer.Tests -c Release --no-build --collect 'XPlat Code Coverage' --settings tests/coverage.runsettings --results-directory TestResults/recheck-final --logger trx
pwsh -ExecutionPolicy Bypass -File tests/Measure-Coverage.ps1 -ResultsDirectory TestResults/recheck-final -MinimumLinePercent 70
dotnet build MusicPlayer.csproj -c Release --no-restore
pwsh -ExecutionPolicy Bypass -File ./launch_coverage_report.ps1 -NoOpen
```
