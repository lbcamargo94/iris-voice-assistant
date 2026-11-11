namespace IrisVoiceAssistant.Audio.Conversion;

/// <summary>
/// Streaming band-limited resampler (Kaiser-windowed sinc) for an arbitrary rational ratio.
/// Output sample <c>n</c> is centred exactly at input time <c>n * inputRate / outputRate</c>; the step is kept as an
/// integer plus a remainder, so long sessions never drift. Input before the first sample is treated as silence.
/// </summary>
public sealed class StreamingResampler
{
    private const int TableResolution = 512;
    private readonly int _step;
    private readonly int _remainderStep;
    private readonly int _denominator;
    private readonly double _halfWidth;
    private readonly float[] _table;
    private readonly bool _passthrough;

    private float[] _buffer = new float[4096];
    private int _count;
    private long _bufferStart;
    private long _position;
    private int _remainder;

    public StreamingResampler(int inputRate, int outputRate, int zeroCrossings = 16, double rolloff = 0.92, double kaiserBeta = 8.6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(zeroCrossings, 2);
        InputRate = inputRate;
        OutputRate = outputRate;
        _passthrough = inputRate == outputRate;

        int divisor = Gcd(inputRate, outputRate);
        int numerator = inputRate / divisor;
        _denominator = outputRate / divisor;
        _step = numerator / _denominator;
        _remainderStep = numerator % _denominator;

        // Cut-off in cycles per input sample: below the lower Nyquist, with a little roll-off margin.
        double cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate) * rolloff;
        _halfWidth = zeroCrossings / (2 * cutoff);
        int tableLength = (int)Math.Ceiling(_halfWidth * TableResolution) + 2;
        _table = new float[tableLength];
        double i0Beta = BesselI0(kaiserBeta);
        for (int i = 0; i < tableLength; i++)
        {
            double x = (double)i / TableResolution;
            if (x > _halfWidth)
            {
                break;
            }

            double u = 2 * cutoff * x;
            double sinc = u == 0 ? 1.0 : Math.Sin(Math.PI * u) / (Math.PI * u);
            double r = x / _halfWidth;
            double window = BesselI0(kaiserBeta * Math.Sqrt(1 - (r * r))) / i0Beta;
            _table[i] = (float)(2 * cutoff * sinc * window);
        }
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Input samples the output lags behind (the kernel's half width), for latency accounting.</summary>
    public int LatencyInputSamples => _passthrough ? 0 : (int)Math.Ceiling(_halfWidth);

    /// <summary>Feeds input samples and appends every output sample that can now be computed.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_passthrough)
        {
            foreach (float sample in input)
            {
                output.Add(sample);
            }

            return;
        }

        Append(input);
        long available = _bufferStart + _count;
        while (true)
        {
            double t = _position + ((double)_remainder / _denominator);
            if (Math.Floor(t + _halfWidth) >= available)
            {
                break;
            }

            output.Add(Evaluate(t));
            _position += _step;
            _remainder += _remainderStep;
            if (_remainder >= _denominator)
            {
                _remainder -= _denominator;
                _position++;
            }
        }

        Discard((long)Math.Ceiling(_position - _halfWidth) - 1);
    }

    private float Evaluate(double t)
    {
        long first = (long)Math.Ceiling(t - _halfWidth);
        long last = (long)Math.Floor(t + _halfWidth);
        double sum = 0;
        for (long index = first; index <= last; index++)
        {
            long local = index - _bufferStart;
            if (local < 0)
            {
                continue; // before the stream started: silence
            }

            double x = Math.Abs(t - index) * TableResolution;
            int i = (int)x;
            double frac = x - i;
            double weight = _table[i] + ((_table[i + 1] - _table[i]) * frac);
            sum += _buffer[local] * weight;
        }

        return (float)sum;
    }

    private void Append(ReadOnlySpan<float> input)
    {
        if (_count + input.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + input.Length));
        }

        input.CopyTo(_buffer.AsSpan(_count));
        _count += input.Length;
    }

    private void Discard(long keepFrom)
    {
        long drop = keepFrom - _bufferStart;
        if (drop <= 0)
        {
            return;
        }

        int n = (int)Math.Min(drop, _count);
        _buffer.AsSpan(n, _count - n).CopyTo(_buffer);
        _count -= n;
        _bufferStart += n;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 50; k++)
        {
            term *= half / k * (half / k);
            sum += term;
            if (term < 1e-12 * sum)
            {
                break;
            }
        }

        return sum;
    }
}
