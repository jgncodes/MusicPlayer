using System;
using NAudio.Wave;

namespace MusicPlayer.Tests;

internal sealed class ArraySampleProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public ArraySampleProvider(
        float[] samples,
        int sampleRate,
        int channels)
    {
        _samples = samples;

        WaveFormat =
            WaveFormat.CreateIeeeFloatWaveFormat(
                sampleRate, channels);
    }

    public int Read(Span<float> buffer)
    {
        int available = _samples.Length - _position;
        int count = Math.Min(buffer.Length, available);

        _samples.AsSpan(_position, count).CopyTo(buffer);

        _position += count;

        return count;
    }
}
