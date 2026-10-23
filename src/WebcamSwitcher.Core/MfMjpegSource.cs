using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace WebcamSwitcher.Core;

/// <summary>
/// MJPG capture through a Media Foundation source reader. The WinRT
/// <c>MediaFrameReader</c> path cannot decode MJPG at all (it yields no frames),
/// while the source reader inserts the MJPEG decoder MFT when the output type is
/// NV12. The reader is pinned to the camera's native MJPG type first (so the
/// device itself runs at the requested rate) and then asked for NV12 at the
/// output size; the decoder does the rest.
///
/// Every failure is logged and reported as <c>false</c> so callers can fall back
/// to the WinRT path. <see cref="Start"/> never throws.
/// </summary>
public sealed class MfMjpegSource : IDisposable, IAsyncDisposable
{
    private static readonly object StartupLock = new();
    private static int _startupRefs;

    private readonly string _deviceId;
    private readonly string _name;
    private readonly int _outWidth;
    private readonly int _outHeight;
    private readonly int _fpsNum;
    private readonly int _fpsDen;

    private IMFSourceReader? _reader;
    private IMFMediaSource? _source;
    private Thread? _thread;
    private volatile bool _stop;
    private int _disposed;
    private int _copyErrors;
    private long _framesRead;
    private int _stride;
    private int _lastSampleBytes;
    private string _negotiationPath = "none";
    private byte[] _frame = Array.Empty<byte>();

    /// <summary>
    /// When true (default) the reader is first opened with
    /// MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING so MF decodes, scales and
    /// converts to NV12; when that fails the reader is reopened without it. Set to
    /// false to force the decoder-only path (diagnostics only; the frame contract
    /// is identical).
    /// </summary>
    public bool PreferAdvancedVideoProcessing { get; set; } = true;

    public MfMjpegSource(string deviceId, string name, int width, int height, int fps = Protocol.DefaultFps)
    {
        _deviceId = deviceId;
        _name = name;
        _outWidth = width;
        _outHeight = height;
        _fpsNum = fps > 0 ? Math.Clamp(fps, 1, 120) : Protocol.DefaultFps;
        _fpsDen = 1;
    }

    public int OutputWidth => _outWidth;
    public int OutputHeight => _outHeight;

    /// <summary>
    /// How the NV12 output type was negotiated: <c>"advanced"</c> (source reader
    /// with MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, i.e. Media
    /// Foundation decodes, scales and converts in the video processor),
    /// <c>"legacy"</c> (decoder only, the pre-existing path) or <c>"none"</c>
    /// (no MF path; the caller uses WinRT capture and software conversion).
    /// Diagnostics only - the frame contract is the same for both.
    /// </summary>
    public string NegotiationPath => _negotiationPath;

    /// <summary>True when MF's video processor produces the output frames, so no software color conversion runs.</summary>
    public bool UsesAdvancedVideoProcessing => _negotiationPath == "advanced";

    /// <summary>MF_MT_DEFAULT_STRIDE of the negotiated output type (negative = bottom-up).</summary>
    public int NegotiatedStride => _stride;

    /// <summary>Byte count of the last MF sample buffer, for verifying the negotiated size.</summary>
    public int LastSampleBytes => Volatile.Read(ref _lastSampleBytes);

    /// <summary>Raised with a tight NV12 buffer (stride == width) at the output size; the buffer is reused.</summary>
    public event Action<byte[]>? FrameReady;

    public long FramesRead => Interlocked.Read(ref _framesRead);

    /// <summary>
    /// Opens the device, pins MJPG, negotiates NV12 output and starts the read
    /// thread. Returns false (after logging) on any failure.
    /// </summary>
    public bool Start()
    {
        if (_disposed != 0)
            return false;
        if (_thread != null)
            return true;
        if (!AcquireMf())
            return false;

        bool ok;
        try
        {
            ok = StartCore();
        }
        catch (Exception ex)
        {
            AppLog.Write($"MF-MJPEG({_name}): start FAILED {Hr(ex)}");
            ok = false;
        }

        if (!ok)
        {
            _thread = null;
            TeardownObjects();
            ReleaseMf();
        }
        return ok;
    }

    private bool StartCore()
    {
        string symbolicLink = ResolveSymbolicLink();
        AppLog.Write($"MF-MJPEG({_name}): opening '{symbolicLink}' target {_outWidth}x{_outHeight} @ {_fpsNum}/{_fpsDen}");

        // Nothing must survive from a previous attempt: both attempts open their
        // own device source and reader.
        TeardownObjects();

        // Attempt 1 asks for NV12 at the destination size with advanced video
        // processing enabled, so Media Foundation's video processor MFT performs
        // decode + scale + color conversion and the frame handler never touches
        // ColorConverter. Attempt 2 is the pre-existing decoder-only path. Only
        // when both fail does the caller fall back to WinRT + software conversion.
        if (TryOpen(symbolicLink, advanced: PreferAdvancedVideoProcessing, out string reason))
        {
            _negotiationPath = PreferAdvancedVideoProcessing ? "advanced" : "legacy";
        }
        else if (PreferAdvancedVideoProcessing)
        {
            AppLog.Write($"MF-MJPEG({_name}): advanced NV12 {_outWidth}x{_outHeight} unavailable ({reason}); retrying without advanced video processing");
            if (!TryOpen(symbolicLink, advanced: false, out reason))
            {
                _negotiationPath = "none";
                AppLog.Write($"MF-MJPEG({_name}): NV12 negotiation failed on both paths ({reason}); caller falls back to WinRT + software conversion");
                return false;
            }
            _negotiationPath = "legacy";
        }
        else
        {
            _negotiationPath = "none";
            AppLog.Write($"MF-MJPEG({_name}): NV12 negotiation failed ({reason}); caller falls back to WinRT + software conversion");
            return false;
        }

        _frame = new byte[Protocol.Nv12Size(_outWidth, _outHeight)];
        AppLog.Write($"MF-MJPEG({_name}): output NV12 {_outWidth}x{_outHeight} @ {_fpsNum}/{_fpsDen} stride={_stride} path={_negotiationPath}");

        _stop = false;
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"MF-MJPEG {_name}"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        return true;
    }

    /// <summary>
    /// Opens the device plus a source reader and negotiates NV12 output at the
    /// configured size/rate. Always leaves <c>_reader</c>/<c>_source</c> null on
    /// failure so the next attempt starts clean. Never throws.
    /// </summary>
    private bool TryOpen(string symbolicLink, bool advanced, out string reason)
    {
        reason = "";
        IMFAttributes? deviceAttributes = null;
        IMFAttributes? readerAttributes = null;
        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        IMFMediaType? nativeType = null;
        bool adopted = false;

        try
        {
            deviceAttributes = MediaFactory.MFCreateAttributes(4);
            Check(deviceAttributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap), "Set SourceType");
            Check(deviceAttributes.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, symbolicLink), "Set SourceTypeVidcapSymbolicLink");

            source = MediaFactory.MFCreateDeviceSource(deviceAttributes);

            // Empty attributes are fine here; passing null to the COM call is not
            // guaranteed to be accepted by the wrapper.
            readerAttributes = MediaFactory.MFCreateAttributes(advanced ? 1u : 0u);
            if (advanced)
            {
                var avf = readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
                if (avf.Failure)
                    AppLog.Write($"MF-MJPEG({_name}): MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING refused 0x{avf.Code:X8}");
            }

            reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, readerAttributes);

            nativeType = FindNativeMjpgType(reader);
            if (nativeType == null)
            {
                reason = $"no native MJPG type at {_outWidth}x{_outHeight}";
                return false;
            }

            using (var readerEx = reader.QueryInterface<IMFSourceReaderEx>())
            {
                var nativeResult = readerEx.SetNativeMediaType(0, nativeType, out int nativeFlags);
                if (nativeResult.Failure)
                {
                    reason = $"SetNativeMediaType 0x{nativeResult.Code:X8} {nativeResult.Description}";
                    return false;
                }
            AppLog.Write($"MF-MJPEG({_name}): native type pinned, flags=0x{nativeFlags:X} (advanced={advanced})");
            }
            // nativeType stays alive until the finally block: its frame rate is used
            // below if the configured rate is refused.

            // Output type: NV12 at the exact output size, the configured rate, and
            // square pixels. Negotiating this is where Media Foundation inserts the
            // MJPEG decoder and - with advanced video processing - the video
            // processor that does the scaling and color conversion.
            int nativeNum = 0, nativeDen = 0;
            if (TryUInt64(nativeType, MediaTypeAttributeKeys.FrameRate, out ulong nativeRate))
            {
                MediaFactory.UnpackRatio(nativeRate, out uint nn, out uint nd);
                nativeNum = (int)nn;
                nativeDen = (int)nd;
            }

            using var outputType = MediaFactory.MFCreateMediaType();
            Check(outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video), "Set MajorType");
            Check(outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12), "Set Subtype");
            Check(MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)_outWidth, (uint)_outHeight), "Set FrameSize");
            Check(MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)_fpsNum, (uint)_fpsDen), "Set FrameRate");
            Check(MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1), "Set PixelAspectRatio");
            Check(outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive), "Set InterlaceMode");
            Check(outputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u), "Set AllSamplesIndependent");

            try
            {
                reader.SetCurrentMediaType(0, outputType);
            }
            catch (Exception ex)
            {
                // Some devices refuse a rate they do not offer. Fall back once to the
                // rate of the type that was actually pinned; the pipe is drop-latest,
                // so the rate here only describes the stream.
                bool retried = false;
                if (nativeNum > 0 && nativeDen > 0 && (nativeNum != _fpsNum || nativeDen != _fpsDen))
                {
                    try
                    {
                        Check(MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)nativeNum, (uint)nativeDen), "Set FrameRate(native)");
                        reader.SetCurrentMediaType(0, outputType);
                        retried = true;
                        AppLog.Write($"MF-MJPEG({_name}): configured {_fpsNum}/{_fpsDen} refused, using native {nativeNum}/{nativeDen}");
                    }
                    catch (Exception)
                    {
                        retried = false;
                    }
                }

                if (!retried)
                {
                    reason = $"SetCurrentMediaType(NV12 {_outWidth}x{_outHeight}) {Hr(ex)}";
                    return false;
                }
            }

            if (!ReadNegotiatedType(reader, out int width, out int height, out int num, out int den, out int stride))
            {
                reason = "GetCurrentMediaType failed";
                return false;
            }

            if (width != _outWidth || height != _outHeight)
            {
                reason = $"negotiated {width}x{height} != requested {_outWidth}x{_outHeight}";
                return false;
            }

            if (stride == 0)
                stride = width;
            if (Math.Abs(stride) < width)
            {
                reason = $"negotiated stride {stride} is narrower than {width}";
                return false;
            }

            AppLog.Write($"MF-MJPEG({_name}): negotiated NV12 {width}x{height} @ {num}/{den} stride={stride} (advanced={advanced})");

            _reader = reader;
            _source = source;
            _stride = stride;
            adopted = true;
            return true;
        }
        catch (Exception ex)
        {
            reason = Hr(ex);
            return false;
        }
        finally
        {
            nativeType?.Dispose();
            readerAttributes?.Dispose();
            deviceAttributes?.Dispose();

            if (!adopted)
            {
                try { reader?.Dispose(); } catch { }
                if (source != null)
                {
                    try { source.Shutdown(); } catch { }
                    try { source.Dispose(); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Looks for the camera's native MJPG type at the requested size, preferring
    /// an exact 30/1 frame rate and otherwise the highest rate at that size.
    /// </summary>
    private IMFMediaType? FindNativeMjpgType(IMFSourceReader reader)
    {
        IMFMediaType? best = null;
        int bestScore = -1;
        string bestDescription = "";
        int enumerated = 0;

        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                type = reader.GetNativeMediaType(0, i);
            }
            catch (Exception)
            {
                // MF_E_NO_MORE_TYPES (0xC00D36B3) ends the enumeration.
                break;
            }

            bool keep = false;
            try
            {
                enumerated++;

                // Check the subtype first: most native types are H264/NV12 and
                // skipping them after one attribute read keeps startup fast.
                Guid? subtype = TryGuid(type, MediaTypeAttributeKeys.Subtype);
                if (subtype == null || subtype.Value != VideoFormatGuids.Mjpg)
                    continue;

                int width = 0, height = 0;
                if (TryUInt64(type, MediaTypeAttributeKeys.FrameSize, out ulong packedSize))
                {
                    MediaFactory.UnpackSize(packedSize, out uint w, out uint h);
                    width = (int)w;
                    height = (int)h;
                }

                int num = 0, den = 0;
                if (TryUInt64(type, MediaTypeAttributeKeys.FrameRate, out ulong packedRate))
                {
                    MediaFactory.UnpackRatio(packedRate, out uint n, out uint d);
                    num = (int)n;
                    den = (int)d;
                }

                double fps = den > 0 ? (double)num / den : 0;
                if (width != _outWidth || height != _outHeight)
                    continue;

                AppLog.Write($"MF-MJPEG({_name}): candidate MJPG {width}x{height} {num}/{den} ({fps:0.##} fps)");

                int score = num == 30 && den == 1 ? 1_000_000 : (int)Math.Min(999_999, fps * 1000);
                if (score > bestScore)
                {
                    best?.Dispose();
                    best = type;
                    bestScore = score;
                    bestDescription = $"{width}x{height} {num}/{den} ({fps:0.##} fps)";
                    keep = true;

                    // 30/1 is the best possible match; stop scanning.
                    if (score == 1_000_000)
                        break;
                }
            }
            finally
            {
                if (!keep)
                    type.Dispose();
            }
        }

        if (best != null)
            AppLog.Write($"MF-MJPEG({_name}): native type chosen MJPG {bestDescription} ({enumerated} types enumerated)");
        else
            AppLog.Write($"MF-MJPEG({_name}): no native MJPG {_outWidth}x{_outHeight} ({enumerated} types enumerated)");

        return best;
    }

    /// <summary>
    /// Reads back the negotiated output type. Pure: it reports what it found and
    /// leaves <c>_stride</c> alone so a failed attempt cannot leak state.
    /// </summary>
    private bool ReadNegotiatedType(
        IMFSourceReader reader, out int width, out int height, out int num, out int den, out int stride)
    {
        width = _outWidth;
        height = _outHeight;
        num = _fpsNum;
        den = _fpsDen;
        stride = 0;

        IMFMediaType current;
        try
        {
            current = reader.GetCurrentMediaType(0);
        }
        catch (Exception ex)
        {
            AppLog.Write($"MF-MJPEG({_name}): GetCurrentMediaType failed {Hr(ex)}");
            return false;
        }

        using (current)
        {
            Guid? subtype = TryGuid(current, MediaTypeAttributeKeys.Subtype);
            if (subtype == null || subtype.Value != VideoFormatGuids.NV12)
                AppLog.Write($"MF-MJPEG({_name}): WARNING negotiated subtype is {SubtypeName(subtype ?? Guid.Empty)}");

            if (TryUInt64(current, MediaTypeAttributeKeys.FrameSize, out ulong packedSize))
            {
                MediaFactory.UnpackSize(packedSize, out uint w, out uint h);
                width = (int)w;
                height = (int)h;
            }

            if (TryUInt64(current, MediaTypeAttributeKeys.FrameRate, out ulong packedRate))
            {
                MediaFactory.UnpackRatio(packedRate, out uint n, out uint d);
                num = (int)n;
                den = (int)d;
            }

            // The default stride is only known after the media type is negotiated,
            // and it is NOT necessarily equal to the width. A negative value means
            // the buffer is bottom-up.
            if (TryUInt32(current, MediaTypeAttributeKeys.DefaultStride, out uint defaultStride) && defaultStride != 0)
                stride = (int)defaultStride;
        }

        if (stride == 0)
            stride = width;

        return true;
    }

    private unsafe void ReadLoop()
    {
        var clock = Stopwatch.StartNew();
        long windowStartMs = 0;
        long windowFrames = 0;
        bool firstLogged = false;

        AppLog.Write($"MF-MJPEG({_name}): read loop started ({Thread.CurrentThread.GetApartmentState()})");
        try
        {
            while (!_stop)
            {
                IMFSample? sample;
                try
                {
                    sample = _reader!.ReadSample(
                        SourceReaderIndex.FirstVideoStream,
                        SourceReaderControlFlag.None,
                        out _,
                        out SourceReaderFlag flags,
                        out _);

                    if (sample == null)
                    {
                        if ((flags & SourceReaderFlag.EndOfStream) != 0)
                        {
                            AppLog.Write($"MF-MJPEG({_name}): end of stream");
                            break;
                        }
                        if ((flags & SourceReaderFlag.Error) != 0)
                        {
                            AppLog.Write($"MF-MJPEG({_name}): ReadSample reported an error");
                            break;
                        }
                        // Neither a sample nor a terminal flag: wait briefly so a
                        // reader that keeps returning null cannot spin a core.
                        Thread.Sleep(1);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    if (!_stop)
                        AppLog.Write($"MF-MJPEG({_name}): ReadSample failed {Hr(ex)}");
                    break;
                }

                using (sample)
                {
                    if (!CopySample(sample))
                        continue;
                }

                Interlocked.Increment(ref _framesRead);
                if (!firstLogged)
                {
                    firstLogged = true;
                    AppLog.Write($"MF-MJPEG: first frame {_outWidth}x{_outHeight} stride={_stride}");
                }

                RaiseFrame();

                long now = clock.ElapsedMilliseconds;
                windowFrames++;
                if (now - windowStartMs >= 5000)
                {
                    double seconds = (now - windowStartMs) / 1000.0;
                    AppLog.Write($"MF-MJPEG({_name}): {windowFrames} frames in {seconds:0.0}s = {windowFrames / seconds:0.0} fps");
                    windowStartMs = now;
                    windowFrames = 0;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"MF-MJPEG({_name}): read loop CRASHED {Hr(ex)}");
        }

        AppLog.Write($"MF-MJPEG({_name}): read loop exited after {FramesRead} frames");
    }

    private unsafe bool CopySample(IMFSample sample)
    {
        IMFMediaBuffer? buffer = null;
        try
        {
            buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr data, out _, out int currentLength);
            try
            {
                Volatile.Write(ref _lastSampleBytes, currentLength);

                int absStride = Math.Abs(_stride);
                int needed = absStride * _outHeight + absStride * (_outHeight / 2);
                if (currentLength < needed)
                {
                    if (Interlocked.Increment(ref _copyErrors) <= 5)
                        AppLog.Write($"MF-MJPEG({_name}): short buffer {currentLength} < {needed} (stride={_stride})");
                    return false;
                }

                fixed (byte* dst = _frame)
                    CopyNv12Tight((byte*)data, dst, _frame.Length, _outWidth, _outHeight, _stride);
                return true;
            }
            finally
            {
                buffer.Unlock();
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _copyErrors) <= 5)
                AppLog.Write($"MF-MJPEG({_name}): copy failed {Hr(ex)}");
            return false;
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    /// <summary>
    /// Copies the sample's NV12 planes into the tight destination buffer. When the
    /// buffer is top-down and its stride equals the width, Y and UV are already
    /// contiguous, so one copy covers the whole frame instead of one per row.
    /// </summary>
    private static unsafe void CopyNv12Tight(byte* src, byte* dst, int dstLength, int w, int h, int stride)
    {
        int abs = Math.Abs(stride);
        bool bottomUp = stride < 0;
        int uvHeight = h / 2;
        int tight = w * h + w * uvHeight;

        if (!bottomUp && abs == w && tight <= dstLength)
        {
            Buffer.MemoryCopy(src, dst, dstLength, tight);
            return;
        }

        for (int y = 0; y < h; y++)
        {
            byte* row = src + (bottomUp ? h - 1 - y : y) * abs;
            Buffer.MemoryCopy(row, dst + y * w, w, w);
        }

        byte* uv = src + abs * h;
        byte* duv = dst + w * h;
        for (int y = 0; y < uvHeight; y++)
        {
            byte* row = uv + (bottomUp ? uvHeight - 1 - y : y) * abs;
            Buffer.MemoryCopy(row, duv + y * w, w, w);
        }
    }

    private void RaiseFrame()
    {
        try
        {
            FrameReady?.Invoke(_frame);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _copyErrors) <= 5)
                AppLog.Write($"MF-MJPEG({_name}): frame handler threw {Hr(ex)}");
        }
    }

    private static string NormalizeDeviceId(string deviceId) => deviceId.Trim();

    /// <summary>
    /// Resolves the camera's exact Media Foundation symbolic link. The WinRT
    /// device id looks similar but Media Foundation only accepts the string its
    /// own enumeration returns (which carries a trailing <c>\global</c>); passing
    /// the WinRT id with <c>\GLOBAL</c> stripped fails with
    /// ERROR_FILE_NOT_FOUND (0x80070002). Matching ignores the suffix's case and
    /// presence, and the raw id is used if the device cannot be enumerated.
    /// </summary>
    private string ResolveSymbolicLink()
    {
        string raw = NormalizeDeviceId(_deviceId);
        string wanted = TrimGlobal(raw);

        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(1);
            Check(attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap), "Set SourceType");
            using var devices = MediaFactory.MFEnumDeviceSources(attributes);

            foreach (var activate in devices)
            {
                using (activate)
                {
                    string? link = null;
                    try { link = activate.GetString(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink); }
                    catch { /* attribute is optional on some devices */ }

                    if (string.IsNullOrEmpty(link))
                        continue;

                    if (string.Equals(TrimGlobal(link), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Write($"MF-MJPEG({_name}): device link resolved by enumeration");
                        return link;
                    }
                }
            }

            AppLog.Write($"MF-MJPEG({_name}): device not found in MF enumeration; trying the configured id as-is");
        }
        catch (Exception ex)
        {
            AppLog.Write($"MF-MJPEG({_name}): device enumeration failed {Hr(ex)}");
        }

        return raw;
    }

    private static string TrimGlobal(string id)
    {
        const string suffix = @"\global";
        return id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? id[..^suffix.Length]
            : id;
    }

    private static void Check(Result result, string what)
    {
        if (result.Failure)
            throw new InvalidOperationException($"{what} failed 0x{result.Code:X8} {result.Description}");
    }

    private static Guid? TryGuid(IMFAttributes attributes, Guid key)
    {
        try { return attributes.GetGUID(key); }
        catch { return null; }
    }

    private static bool TryUInt32(IMFAttributes attributes, Guid key, out uint value)
    {
        try { value = attributes.GetUInt32(key); return true; }
        catch { value = 0; return false; }
    }

    private static bool TryUInt64(IMFAttributes attributes, Guid key, out ulong value)
    {
        try { value = attributes.GetUInt64(key); return true; }
        catch { value = 0; return false; }
    }

    private static string SubtypeName(Guid subtype)
    {
        if (subtype == VideoFormatGuids.Mjpg) return "MJPG";
        if (subtype == VideoFormatGuids.NV12) return "NV12";
        if (subtype == VideoFormatGuids.YUY2) return "YUY2";
        if (subtype == VideoFormatGuids.H264) return "H264";
        return subtype == Guid.Empty ? "(none)" : subtype.ToString();
    }

    private static string Hr(Exception ex) =>
        $"0x{ex.HResult:X8} ({ex.GetType().Name}: {ex.Message})";

    private bool AcquireMf()
    {
        lock (StartupLock)
        {
            if (_startupRefs++ > 0)
                return true;

            var result = MediaFactory.MFStartup(false);
            if (result.Failure)
            {
                _startupRefs = 0;
                AppLog.Write($"MF-MJPEG({_name}): MFStartup failed 0x{result.Code:X8} {result.Description}");
                return false;
            }
            AppLog.Write($"MF-MJPEG({_name}): MFStartup ok");
            return true;
        }
    }

    private void ReleaseMf()
    {
        lock (StartupLock)
        {
            if (_startupRefs == 0 || --_startupRefs > 0)
                return;

            try
            {
                MediaFactory.MFShutdown().CheckError();
                AppLog.Write($"MF-MJPEG({_name}): MFShutdown ok");
            }
            catch (Exception ex)
            {
                AppLog.Write($"MF-MJPEG({_name}): MFShutdown failed {Hr(ex)}");
            }
        }
    }

    /// <summary>Disposes the COM objects without waiting for the read thread.</summary>
    private void TeardownObjects()
    {
        try { _reader?.Dispose(); } catch { }
        _reader = null;

        if (_source != null)
        {
            try { _source.Shutdown(); } catch { }
            try { _source.Dispose(); } catch { }
            _source = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop = true;

        // ReadSample blocks; Flush makes it return null so the thread can exit.
        try { _reader?.Flush(SourceReaderIndex.AllStreams); }
        catch (Exception ex) { AppLog.Write($"MF-MJPEG({_name}): Flush failed {Hr(ex)}"); }

        var thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive && !thread.Join(2000))
        {
            // Tearing down COM objects still in use would crash the process, so
            // leave them to process teardown instead.
            AppLog.Write($"MF-MJPEG({_name}): read thread did not stop; leaving MF objects alive");
            return;
        }

        TeardownObjects();
        ReleaseMf();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
