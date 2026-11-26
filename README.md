# MusicPlayer

MusicPlayer is a Windows desktop application for playing local MP3 and WAV files. It combines a searchable, folder-based music library with an interactive waveform, playback-synchronized signal analysis, automatic tempo estimation, and a ten-band graphic equalizer.

## Requirements and installation

Use a Windows desktop with the .NET 10 SDK and Git available in PowerShell. The application and test project target `net10.0-windows`. From PowerShell:

```powershell
git clone https://github.com/Jn527fd/MusicPlayer.git
Set-Location MusicPlayer
dotnet restore MusicPlayer.csproj
dotnet build MusicPlayer.csproj --no-restore
dotnet run --project MusicPlayer.csproj --no-build
```

In MusicPlayer, choose **File → Open Folder** to browse a music folder. Select a track to load it, then press **Play** or double-click the track. **File → Open File** loads the file's containing folder and selects the chosen track.

## Features

### Playback

- Play or pause local MP3 and WAV files. Seek with the timeline or by clicking a position on the waveform. The timeline shows elapsed time and track duration.
- Previous, Next, and automatic advancement use the visible track list, including active search results. With shuffle off, manual navigation wraps around and automatic playback stops after the last visible track.
- Shuffle picks a random visible track and excludes the current track when at least two tracks are visible. Repeat replays the current track at the end and takes precedence over automatic advancement.
- Set volume with the slider or toggle mute while retaining the selected volume.

### Music library

- Browse supported files directly inside one folder, sorted alphabetically by filename. Search matches filename substrings without regard to case and trims surrounding whitespace. The song count shows the folder total.
- View the filename, duration, and BPM in the track list. Now Playing also shows format, sample rate, channel layout, and bitrate when available.
- Assign artwork to an individual track from a local PNG, JPEG, or BMP file or a direct HTTPS image URL. Imports are limited to 5 MB and saved as app-local PNGs associated with the track's path.
- Use **File → Set Default Folder** to reopen a folder at startup. **Clear Default Folder** removes the saved startup choice. The default folder, artwork, and waveform and tempo caches are stored under `%LOCALAPPDATA%\MusicPlayer`.

### Audio analysis

- **Waveform** summarizes the full track into 400 peak bins and redraws a moving playhead as playback or seeking changes position. The waveform is cached on disk for reuse.
- **Frequency and levels** use 4,096-point FFTs with Hann windowing for relative bass, midrange, and treble energy, alongside time-domain RMS and sample peak in dBFS. The three bands cover 20–250 Hz, 250 Hz–4 kHz, and 4–16 kHz, with upper bounds excluded. The app selects the analysis frame for the current playback position. Values below full scale are negative in dBFS.
- **Tempo** prefers embedded BPM metadata. When a track without a BPM tag is opened, the detector uses spectral flux and normalized autocorrelation to estimate a tempo in the 60–200 BPM range. It requires at least eight seconds of onset data and sufficient rhythmic repetition. Now Playing identifies estimated BPM and shows a rhythm-strength score. BPM stays unavailable if the detector cannot make an estimate. Successful estimates are cached using the path, file size, modification time, and detector version.
- Analysis runs in the background. Changing tracks cancels work for the previous track.

### Audio equalizer

Open **Sound → Equalizer** for ten bands centered at 31.25, 62.5, 125, 250, 500, 1,000, 2,000, 4,000, 8,000, and 16,000 Hz. Each band supports ±12 dB of gain. Built-in presets are **Flat**, **Bass Boost**, **Vocal**, and **V-Shape**. Editing a band marks the curve as **Custom**, and **Reset** restores all bands to 0 dB.

The EQ starts flat and enabled. **Bypass EQ** preserves the curve for comparison. Each channel has independent peaking filters with Q = 1.4. A band at or above 45% of the decoded sample rate has no effect on that track. Active band changes, preset changes and bypass transitions crossfade over 20 ms of audio. Device buffering adds to the time before a change reaches the speakers.

Positive bands automatically reserve fixed headroom for the combined boost. Overlapping boosts can therefore play quieter than in earlier versions. A flat curve and cuts alone reserve 0 dB. **Output trim** adds 0 to −24 dB of attenuation and applies in bypass too. **Reset** clears the bands, trim and peak warning. Volume and mute act after the EQ and ramp over 20 ms. These preferences carry across tracks in the current session.

A transient can still exceed full scale. **Output over 0 dBFS · Clear** appears when a sample sent to the device exceeds ±1. Lower the trim, clear the warning and replay the passage to check it. The player uses static gain and preserves the floating-point waveform through this stage. The main window's RMS, peak and spectrum continue to describe the source file.

## Architecture

WPF starts the application through `App.xaml` and opens `MainWindow`. The window coordinates application behavior in its code-behind and delegates playback, signal analysis, library discovery, and navigation to the classes below.

| Component | Responsibility |
| --- | --- |
| [MainWindow](MainWindow.xaml.cs) | Handles track selection, playback controls, default-folder settings, artwork import, and visualization updates. Starts and cancels background analysis and uses a dispatcher timer to update the timeline and meters. |
| [SongFile](SongFile.cs) | Stores the file path, display metadata, artwork, and tempo estimate. Notifies WPF bindings when artwork or tempo information changes. |
| [AudioPlaybackService](Services/AudioPlaybackService.cs) | Owns the decoder, EQ provider, and output device. Exposes transport, position, duration, volume, and EQ controls and raises events for media open, end, and failure. |
| [EqualizerSampleProvider](Services/EqualizerSampleProvider.cs) | Implements NAudio's `ISampleProvider` to apply per-channel filters and headroom attenuation to decoded samples. |
| [GraphicEqualizerDesign](Services/GraphicEqualizerDesign.cs) | Calculates sample-rate-specific peaking coefficients and a bound on combined boost. The render thread owns the filter delay lines. |
| [AudioAnalysisService](Services/AudioAnalysisService.cs) | Reads metadata with TagLibSharp and decodes source audio independently for waveform, frequency, level, and tempo analysis. Manages the waveform and tempo caches. |
| [MusicLibraryLogic](Services/MusicLibraryLogic.cs) and [PlaybackQueue](Services/PlaybackQueue.cs) | Implement folder discovery, filename matching, and visible-list navigation independently of WPF controls and audio hardware. |
| [EqualizerWindow](EqualizerWindow.xaml.cs) | Uses the same playback service as the main window to edit band gains and apply presets. |

```mermaid
flowchart TD
    File["Local MP3 or WAV"] --> Reader["AudioFileReader at unity gain"]
    File --> Analysis["Independent source analysis"]
    Reader --> EQ["Per-channel EQ and headroom"]
    EQ --> Level["Output trim, volume and mute"]
    Level --> Output["Float PCM to WaveOut"]
    Analysis --> UI["Waveform, BPM, frequency and levels"]
```

`AudioFileReader` uses Windows Media Foundation for MP3. For WAV it opens a `WaveFileReader` and uses an ACM conversion for supported compressed WAV codecs. The DSP receives interleaved 32-bit IEEE float PCM at the decoder's sample rate and channel count. Tests verify 8-, 16-, 24- and 32-bit integer WAV and 32-bit float WAV. Each non-flat filter uses double-precision coefficients and state. At unity output level, settled flat EQ and bypass pass finite float samples exactly.

`ToWaveProvider()` exposes the float samples as bytes for event-driven WinMM `WaveOut` on the default Windows output. NAudio 3.3.0 defaults to two 100 ms buffers. MusicPlayer requests the decoder format without an application resampler or channel mixer. Device acceptance, Windows mixing and the physical endpoint determine the final output. Seeking resets filter history at the next decoded block. Audio already queued at the device can still be heard.

## Technologies

| Area | Frameworks and tools |
| --- | --- |
| Application | C#, .NET 10, WPF, and XAML |
| Runtime packages | NAudio 3.3.0 for decoding, playback, and DSP. TagLibSharp 2.3.0 for embedded BPM and technical metadata. |
| Testing | Microsoft.NET.Test.Sdk 17.14.1, xUnit 2.9.3, and the xUnit Visual Studio runner 3.1.4 |
| Reporting and automation | Coverlet collector 6.0.4, ReportGenerator as a global .NET tool for HTML reports, and GitHub Actions |

## Automated testing

Run the Release suite from the repository root on Windows with the .NET 10 SDK. Filter by xUnit class name to run one group.

```powershell
$project = 'tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj'
dotnet test $project -c Release
dotnet test $project -c Release --filter FullyQualifiedName~PlaybackLifecycleTests
dotnet test $project -c Release --filter FullyQualifiedName~WindowIntegrationTests
```

The tests use deterministic WAVs, in-memory sample providers, temporary music folders and private cache directories. Playback lifecycle tests use the real decoder, equalizer and an in-memory NAudio output device. WPF tests instantiate the XAML windows on STA dispatcher threads and await analysis completion. The suite runs without speakers, a visible window, network access during tests, or a personal music folder.

## Code coverage

```powershell
.\launch_coverage_report.ps1
.\launch_coverage_report.ps1 -NoOpen
```

The launcher collects Release coverage into a fresh directory under `TestResults/local/`, keeps the TRX and Cobertura XML, generates `coverage-report/index.html` and opens it in the default browser. Use `-NoOpen` on a headless runner. ReportGenerator must be on PATH or installable as a global .NET tool. Initial dependency and tool installation requires access to a package source. Both results directories are ignored by Git.

The Phase 2A Release run passed **221 tests**. ReportGenerator measured **89.64% production line coverage** (1,548/1,727) and **84.52% branch coverage** (639/756), including WPF. Tests cover decoded audio, filter response, live edits, overloads and seek resets. The equalizer window provides the ten-band graphic EQ. Physical listening and endpoint compatibility remain manual checks. [Parametric DSP engineering report](PARAMETRIC_EQ_REPORT.md) · [Audio engine report](AUDIO_ENGINE_REPORT.md) · [Earlier coverage audit](tests/COVERAGE.md)

## Continuous integration

The Windows workflow builds the application and runs the full Release xUnit suite with the same collector settings as the local launcher. CI requires **at least 80% line coverage and 75% branch coverage**. The gate uses ReportGenerator 5.6.1's integer covered and total counts from `Summary.json`. The HTML report displays rounded percentages. A failed test, missing coverage report, or either fraction falling below its minimum fails the job. The `musicplayer-test-results` artifact retains available TRX, Cobertura and gate summary files for 14 days, including on failure. Both minimums are configurable in the [workflow](.github/workflows/tests.yaml).

To apply the CI gate locally after running the launcher, use PowerShell from the repository root with ReportGenerator on PATH.

```powershell
$run = Get-ChildItem TestResults/local -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
.\tests\Measure-Coverage.ps1 -ResultsDirectory $run.FullName -MinimumLinePercent 80 -MinimumBranchPercent 75
```

## Project structure

```text
MusicPlayer/
├── .github/workflows/tests.yaml
├── App.xaml
├── App.xaml.cs
├── AssemblyInfo.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
├── EqualizerWindow.xaml
├── EqualizerWindow.xaml.cs
├── SongFile.cs
├── Services/
│   ├── AudioAnalysisService.cs
│   ├── AudioPlaybackService.cs
│   ├── EqualizerSampleProvider.cs
│   ├── MusicLibraryLogic.cs
│   └── PlaybackQueue.cs
├── tests/MusicPlayer.Tests/
│   ├── MusicPlayer.Tests.csproj
│   ├── AudioAnalysisTests.cs
│   ├── AudioPlaybackServiceTests.cs
│   ├── EqualizerTests.cs
│   ├── MusicLibraryTests.cs
│   ├── PlaybackQueueTests.cs
│   ├── TempoDetectionTests.cs
│   ├── TestAudioFactoryTests.cs
│   ├── WaveformTests.cs
│   ├── TemporaryWavFile.cs
│   └── TestAudioFactory.cs
├── .gitignore
├── MusicPlayer.csproj
├── launch_coverage_report.ps1
├── README.md
└── ROADMAP.md
```

## Limitations

- MusicPlayer is a Windows WPF application. The library supports local MP3 and WAV files from one folder at a time, with nonrecursive scanning and filename-based search.
- Frequency and level displays measure source audio. They are independent of volume, mute, and EQ settings.
- BPM is a heuristic estimate and can resolve to a half-time or double-time interpretation. Short tracks or weakly repeating signals may yield no estimate.
- The app persists the startup folder, custom artwork, and analysis caches. Playback sessions, EQ settings across restarts, and user-defined presets are future work.

## Development roadmap

Planned work includes persistent playback sessions, saved EQ presets and response visualization, custom playlists, Windows media controls, post-EQ analysis, and a SQLite-backed library. Longer-term work includes gapless playback, crossfade, smart shuffle, and audio-similarity recommendations. See the [Development Roadmap](ROADMAP.md).
