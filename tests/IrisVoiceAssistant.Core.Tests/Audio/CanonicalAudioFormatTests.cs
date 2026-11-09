using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Core.Tests.Audio;

public sealed class CanonicalAudioFormatTests
{
    [Fact]
    public void Frame_is_20ms_of_16kHz_mono_float32()
    {
        Assert.Equal(320, CanonicalAudioFormat.SamplesPerFrame);
        Assert.Equal(1280, CanonicalAudioFormat.BytesPerFrame);
        Assert.Equal(
            TimeSpan.FromMilliseconds(CanonicalAudioFormat.FrameDurationMilliseconds),
            CanonicalAudioFormat.ToTimeSpan(CanonicalAudioFormat.SamplesPerFrame));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(16_000L)]
    [InlineData(57_600_000L)] // one hour of audio
    public void Sample_position_round_trips_through_TimeSpan_exactly(long samples)
    {
        var elapsed = CanonicalAudioFormat.ToTimeSpan(samples);

        Assert.Equal(samples, CanonicalAudioFormat.ToSamples(elapsed));
    }

    [Fact]
    public void One_hour_session_maps_to_exact_duration()
    {
        Assert.Equal(TimeSpan.FromHours(1), CanonicalAudioFormat.ToTimeSpan(57_600_000L));
    }

    [Fact]
    public void ToSamples_rounds_down_partial_samples()
    {
        // One sample lasts 625 ticks; 624 ticks is still inside sample 0.
        Assert.Equal(0, CanonicalAudioFormat.ToSamples(TimeSpan.FromTicks(624)));
        Assert.Equal(1, CanonicalAudioFormat.ToSamples(TimeSpan.FromTicks(625)));
    }

    [Fact]
    public void Negative_inputs_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalAudioFormat.ToTimeSpan(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalAudioFormat.ToSamples(TimeSpan.FromTicks(-1)));
    }
}
