namespace WebcamSwitcher.Core;

/// <summary>
/// A fixed-capacity FIFO of NV12 frame copies, used to give every virtual camera
/// the same, operator-controlled latency so switching sources never shifts A/V
/// sync.
///
/// <para>
/// The contract is shaped so a delay can never stall a virtual camera:
/// </para>
/// <list type="bullet">
/// <item>Depth 0 is a complete bypass — <see cref="TryPush"/> returns false and
/// allocates, copies and locks nothing. That is the default and costs nothing.</item>
/// <item>While the ring is filling (fewer than <see cref="Depth"/> frames held)
/// <see cref="TryPush"/> also returns false, meaning "publish the frame that just
/// arrived": the newest frame is always available, never "no frame".</item>
/// <item>Once full it returns true with the frame from <see cref="Depth"/> frames
/// ago, strictly in order, one buffer in and one out, with no per-frame
/// allocation.</item>
/// </list>
///
/// <para>
/// <b>Threading.</b> Pushing and taking are not separate calls: <see cref="TryPush"/>
/// does both, so the capture callback that pushes is also the one that publishes
/// and no hand-off between threads is needed — the same single-writer assumption
/// <see cref="FramePublisher"/> makes about its frame source. One internal lock
/// guards every state change (<see cref="Configure"/>, <see cref="Resize"/>,
/// <see cref="Clear"/>) against a frame in flight, and it covers the copy, so a
/// settings change from the UI thread can never tear a frame or reorder the
/// queue; the disabled fast path below takes no lock at all. A frame handed back
/// by <see cref="TryPush"/> stays valid until the caller pushes again, exactly
/// like the sibling buffers in <see cref="FramePublisher"/>.
/// </para>
///
/// <para>
/// <b>Resizing mid-stream.</b> Growing a depth is treated exactly like an
/// unfilled ring: the newest frames go out immediately until enough are held
/// (so raising the delay takes at most <c>depth / fps</c> seconds to settle and
/// cannot stall). Shrinking keeps the oldest frames and drops the newest ones,
/// so output order stays strictly FIFO and nothing is duplicated.
/// </para>
/// </summary>
public sealed class FrameDelayBuffer
{
    /// <summary>Largest accepted depth, in frames (2 s at 30 fps).</summary>
    public const int MaxDepth = 60;

    private readonly object _sync = new();

    // _depth frames are held plus one exchange slot, so the frame being handed
    // out is never overwritten by the frame arriving behind it; a full ring
    // therefore costs (depth + 1) frame buffers and no extra copy.
    private byte[][] _slots = Array.Empty<byte[]>();
    private int _slotBytes;
    private int _head;
    private int _count;
    private int _depth;
    private int _resets;

    /// <summary>
    /// Creates a bypassed buffer (depth 0): no memory, no copies, no lock on the
    /// frame path. Call <see cref="Configure"/> to turn a delay on.
    /// </summary>
    public FrameDelayBuffer()
    {
    }

    /// <summary>Configured depth in frames; 0 means "disabled / pure bypass".</summary>
    public int Depth
    {
        get
        {
            lock (_sync)
                return _depth;
        }
    }

    /// <summary>Frames currently held in the ring (always ≤ <see cref="Depth"/>).</summary>
    public int Buffered
    {
        get
        {
            lock (_sync)
                return _count;
        }
    }

    /// <summary>
    /// Bytes of frame copies currently held (0 when disabled). The ring also
    /// keeps one spare exchange buffer for the frame being handed out, so a full
    /// ring of depth N occupies (N + 1) frame buffers in total.
    /// </summary>
    public long BytesHeld
    {
        get
        {
            lock (_sync)
                return (long)_count * _slotBytes;
        }
    }

    /// <summary>
    /// Number of times the ring was emptied by a frame-size change since it was
    /// configured; diagnostic only (a healthy stream keeps it stable).
    /// </summary>
    public int Resets
    {
        get
        {
            lock (_sync)
                return _resets;
        }
    }

    /// <summary>
    /// Sets the depth in frames (clamped to <c>0..<see cref="MaxDepth"/>) and
    /// sizes the ring once. Depth 0 releases every buffer immediately, so turning
    /// a delay off returns all of its memory. Same-depth calls are no-ops, which
    /// is what makes it safe to call from a live settings change.
    /// </summary>
    public void Configure(int depthFrames) => Resize(depthFrames);

    /// <summary>
    /// Runtime depth change, safe mid-stream. Growing keeps the frames already
    /// held and acts as a filling ring again; shrinking keeps the oldest frames
    /// and drops the newest, preserving FIFO order without stalling. Frame buffers
    /// are carried over by reference in both directions, so no frame is copied.
    /// </summary>
    public void Resize(int depthFrames)
    {
        depthFrames = Math.Clamp(depthFrames, 0, MaxDepth);

        lock (_sync)
        {
            if (depthFrames == _depth)
                return;

            if (depthFrames == 0)
            {
                // Disabled: drop the copies, keep nothing that could leak memory.
                _depth = 0;
                _slots = Array.Empty<byte[]>();
                _slotBytes = 0;
                _head = 0;
                _count = 0;
                return;
            }

            // Re-lay the held frames out oldest-first into a fresh array, so the
            // queue order can never drift across a resize. Shrinking keeps the
            // oldest frames and drops the newest queued ones: the next output is
            // still the oldest frame held, so order is preserved and the shorter
            // latency takes effect at once.
            int keep = Math.Min(_count, depthFrames);
            int oldLength = _slots.Length;
            var slots = new byte[depthFrames + 1][];
            for (int i = 0; i < keep; i++)
                slots[i] = _slots[(_head + i) % oldLength];

            _slots = slots;
            _head = 0;
            _count = keep;
            _depth = depthFrames;
        }
    }

    /// <summary>
    /// Offers one frame and, when the ring is full, returns the frame to publish
    /// instead. Returns false to publish <paramref name="frame"/> itself (delay
    /// disabled, or the ring is still filling) — never "no frame".
    /// </summary>
    /// <param name="frame">The frame that just arrived; copied only when the
    /// delay is enabled.</param>
    /// <param name="delayed">The frame to publish instead when this returns true;
    /// owned by the buffer until the next call, so publish it before pushing
    /// again.</param>
    public bool TryPush(ReadOnlySpan<byte> frame, out byte[]? delayed)
    {
        delayed = null;

        // Fast path: a disabled buffer must cost nothing at all. _depth is an
        // int, so this read is atomic; Configure(0) also takes the lock.
        if (Volatile.Read(ref _depth) == 0)
            return false;

        lock (_sync)
        {
            if (_depth == 0 || frame.Length == 0)
                return false;

            // A frame-size change (resolution switch) cannot be mixed with the
            // held copies: start the ring over rather than emit garbage. The
            // slots are reallocated so no buffer of the old size is reused, which
            // keeps the memory bound at (depth + 1) × frameBytes.
            if (frame.Length != _slotBytes)
            {
                // The first frame after Configure only sizes the ring; it is not
                // a reset of a running stream.
                bool firstFrame = _slotBytes == 0;
                _slots = new byte[_depth + 1][];
                _slotBytes = frame.Length;
                _head = 0;
                _count = 0;
                if (!firstFrame)
                    _resets++;
            }

            // Write behind the held frames: the exchange slot at head + count is
            // always free, so it never clobbers the frame being handed out.
            int write = (_head + _count) % _slots.Length;
            var slot = _slots[write] ??= new byte[_slotBytes];
            frame.CopyTo(slot);

            if (_count < _depth)
            {
                // Filling: the newest frame goes out now; the copy waits.
                _count++;
                return false;
            }

            // Full: hand out the oldest frame and advance past it. Order holds
            // because the write slot is the one just vacated by this pop.
            delayed = _slots[_head];
            _head = (_head + 1) % _slots.Length;
            return true;
        }
    }

    /// <summary>
    /// Drops every held frame but keeps the configured depth and the allocated
    /// buffers (e.g. after a capture restart). Never stalls: the next pushes
    /// behave like a filling ring again.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Releases all frame memory and disables the delay (equivalent to
    /// <c>Configure(0)</c>), for shutdown or when the user turns the delay off.
    /// </summary>
    public void Release() => Configure(0);
}
