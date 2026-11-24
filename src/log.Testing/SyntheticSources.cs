using Log.Core.Capture;
using System.Buffers.Binary;

namespace Log.Testing;

public sealed class SyntheticFrameSource(IEnumerable<FrameSample> frames) : IFrameSource, IFrameSourceLifecycle
{
    private readonly IReadOnlyList<FrameSample> frames = frames.ToArray();

    public FrameSourceCompletion Completion { get; private set; }

    public static SyntheticFrameSource Create(
        int frameCount,
        long startTicks,
        long intervalTicks,
        int width = 2,
        int height = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalTicks);

        return new SyntheticFrameSource(Enumerable.Range(0, frameCount).Select(index =>
        {
            var sourceTicks = checked(startTicks + index * intervalTicks);
            return new FrameSample(
                sourceTicks,
                SyntheticFramePayload.Encode(index, sourceTicks),
                width,
                height);
        }));
    }

    public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
            await Task.Yield();
        }

        Completion = FrameSourceCompletion.Completed;
    }
}

public sealed class VirtualClock(long initialTicks = 0) : IClock
{
    public long NowTicks { get; private set; } = initialTicks;

    public async ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetTicks > NowTicks)
        {
            NowTicks = targetTicks;
        }

        await Task.Yield();
    }

    public void AdvanceBy(long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        NowTicks = checked(NowTicks + ticks);
    }
}

public sealed class RecordingEncoder : IEncoder
{
    private readonly List<EncodedFrame> frames = [];

    public IReadOnlyList<EncodedFrame> Frames => frames;
    public bool IsFinalized { get; private set; }

    public ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsFinalized)
        {
            throw new InvalidOperationException("The encoder has already been finalized.");
        }

        frames.Add(frame);
        return ValueTask.CompletedTask;
    }

    public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsFinalized = true;
        return ValueTask.CompletedTask;
    }
}

public readonly record struct SyntheticFrameMarker(int FrameIndex, long SourceTicks);

public static class SyntheticFramePayload
{
    private const uint Magic = 0x474F4C46;
    private const int PayloadLength = sizeof(uint) + sizeof(int) + sizeof(long);

    public static byte[] Encode(int frameIndex, long sourceTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceTicks);
        var payload = new byte[PayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(uint)), frameIndex);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(uint) + sizeof(int)), sourceTicks);
        return payload;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out SyntheticFrameMarker marker)
    {
        marker = default;
        if (payload.Length < PayloadLength
            || BinaryPrimitives.ReadUInt32LittleEndian(payload) != Magic)
        {
            return false;
        }

        var frameIndex = BinaryPrimitives.ReadInt32LittleEndian(payload[sizeof(uint)..]);
        var sourceTicks = BinaryPrimitives.ReadInt64LittleEndian(payload[(sizeof(uint) + sizeof(int))..]);
        if (frameIndex < 0 || sourceTicks < 0)
        {
            return false;
        }

        marker = new SyntheticFrameMarker(frameIndex, sourceTicks);
        return true;
    }

    public static IReadOnlyList<SyntheticFrameMarker> DecodeEncodedFrames(
        IReadOnlyList<EncodedFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var markers = new SyntheticFrameMarker[frames.Count];
        for (var index = 0; index < frames.Count; index++)
        {
            var sample = frames[index].Sample
                ?? throw new InvalidDataException($"Encoded frame {index} was a gap.");
            if (!TryDecode(sample.Payload.Span, out var marker))
            {
                throw new InvalidDataException($"Encoded frame {index} did not contain a synthetic marker.");
            }

            markers[index] = marker;
        }

        return markers;
    }
}

public static class CaptureTestSupport
{
    public static CaptureSafetyGate StartedSafetyGate()
    {
        var safetyGate = new CaptureSafetyGate();
        safetyGate.Start(indicatorsVerified: true);
        return safetyGate;
    }
}
