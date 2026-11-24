using System;
using System.IO;
using NAudio.Wave;

namespace MusicPlayer.Tests;

/// <summary>
/// Creates a temporary WAV file for automated audio tests.
/// The file is automatically deleted when disposed.
/// </summary>
public sealed class TemporaryWavFile : IDisposable
{
    public string FilePath { get; }

    public TemporaryWavFile(
        float[] samples,
        int sampleRate = 44100)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        FilePath = Path.Combine(
            Path.GetTempPath(),
            $"MusicPlayerTest_{Guid.NewGuid():N}.wav");

        // Use mono, 32-bit floating-point audio.
        WaveFormat format =
            WaveFormat.CreateIeeeFloatWaveFormat(
                sampleRate, 1);

        using var writer =
            new WaveFileWriter(FilePath, format);

        writer.WriteSamples(
            samples, 0, samples.Length);
    }

    public void Dispose()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}
