using IrisVoiceAssistant.Audio.Conversion;

namespace IrisVoiceAssistant.Audio.Tests.Conversion;

public sealed class StreamingResamplerTests
{
    private static float[] Sine(int rate, double frequency, double seconds, double amplitude = 0.5) =>
        Enumerable.Range(0, (int)(rate * seconds))
            .Select(n => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * n / rate)))
            .ToArray();

    private static List<float> Run(StreamingResampler resampler, float[] input, int chunk)
    {
        var output = new List<float>();
        for (int i = 0; i < input.Length; i += chunk)
        {
            resampler.Process(input.AsSpan(i, Math.Min(chunk, input.Length - i)), output);
        }

        return output;
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(32_000)]
    [InlineData(96_000)]
    [InlineData(8_000)]
    public void Keeps_an_in_band_tone_with_its_amplitude_and_phase(int inputRate)
    {
        var resampler = new StreamingResampler(inputRate, 16_000);
        List<float> output = Run(resampler, Sine(inputRate, 1_000, 1.0), chunk: 441);

        // Compare against the ideal 16 kHz sine sampled at the same instants, away from the start-up transient.
        double maxError = 0;
        for (int n = 400; n < output.Count; n++)
        {
            double ideal = 0.5 * Math.Sin(2 * Math.PI * 1_000 * n / 16_000);
            maxError = Math.Max(maxError, Math.Abs(output[n] - ideal));
        }

        Assert.True(maxError < 0.01, $"max error {maxError:F4} for {inputRate} Hz");
    }

    [Fact]
    public void Attenuates_tones_above_the_output_nyquist()
    {
        var resampler = new StreamingResampler(48_000, 16_000);
        List<float> output = Run(resampler, Sine(48_000, 12_000, 1.0, amplitude: 1.0), chunk: 480);
        double peak = output.Skip(400).Max(Math.Abs);
        Assert.True(peak < 0.001, $"aliased peak {peak:F5} (should be below -60 dBFS)");
    }

    [Fact]
    public void Output_does_not_depend_on_how_the_input_is_chunked()
    {
        float[] input = Sine(44_100, 440, 0.5);
        List<float> whole = Run(new StreamingResampler(44_100, 16_000), input, input.Length);
        List<float> small = Run(new StreamingResampler(44_100, 16_000), input, 7);
        Assert.Equal(whole.Count, small.Count);
        for (int i = 0; i < whole.Count; i++)
        {
            Assert.Equal(whole[i], small[i], 1e-6f);
        }
    }

    [Fact]
    public void Produces_the_exact_rational_count_without_drift()
    {
        var resampler = new StreamingResampler(44_100, 16_000);
        var output = new List<float>();
        float[] second = new float[44_100];
        for (int s = 0; s < 120; s++)
        {
            resampler.Process(second, output);
        }

        // 120 s in, 1 920 000 out, minus the samples still waiting for the kernel's right half.
        int pending = (int)Math.Ceiling(resampler.LatencyInputSamples * 16_000 / 44_100.0) + 1;
        Assert.InRange(output.Count, 1_920_000 - pending, 1_920_000);
    }

    [Fact]
    public void Same_rate_is_a_passthrough()
    {
        float[] input = Sine(16_000, 300, 0.1);
        List<float> output = Run(new StreamingResampler(16_000, 16_000), input, 33);
        Assert.Equal(input, output);
    }
}
