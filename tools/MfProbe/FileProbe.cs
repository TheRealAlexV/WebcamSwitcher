using System.Diagnostics;
using Vortice.MediaFoundation;

namespace WebcamSwitcher.MfProbe;

/// <summary>
/// Camera-free end-to-end check of the exact negotiation the production code
/// performs (advanced video processing + NV12 output type at the destination
/// size/fps/progressive/1:1 PAR) against a file source instead of a camera.
/// It proves Media Foundation's video processor really decodes, scales and
/// converts to NV12 through that type, and reports the resulting stride,
/// sample size, mean luma and pipeline rate. It says nothing about a specific
/// camera's capabilities - that is what the per-device run is for.
/// </summary>
internal static class FileProbe
{
    public static int Run(string path, int wantW, int wantH, int wantFps, int frames, bool advanced = true)
    {
        Console.WriteLine($"FileProbe: {path} -> NV12 {wantW}x{wantH}@{wantFps}, {frames} frames, advanced={advanced}");
        if (!File.Exists(path))
        {
            Console.WriteLine($"  file not found: {path}");
            return 2;
        }

        MediaFactory.MFStartup(false);
        try
        {
            using var attrs = MediaFactory.MFCreateAttributes(advanced ? 1u : 0u);
            if (advanced)
                attrs.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);

            using var outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            MediaFactory.MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, (uint)wantW, (uint)wantH);
            MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.FrameRate, (uint)wantFps, 1);
            MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            outType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);

            try
            {
                reader.SetCurrentMediaType(0, outType);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  SetCurrentMediaType NV12 {wantW}x{wantH}: FAILED {ex.Message}");
                return 1;
            }

            using var current = reader.GetCurrentMediaType(0);
            int stride = (int)current.GetUInt32(MediaTypeAttributeKeys.DefaultStride);
            current.GetUInt64(MediaTypeAttributeKeys.FrameSize, out ulong packed);
            MediaFactory.UnpackSize(packed, out uint w, out uint h);
            current.GetUInt64(MediaTypeAttributeKeys.FrameRate, out ulong rate);
            MediaFactory.UnpackRatio(rate, out uint num, out uint den);
            Console.WriteLine($"  SetCurrentMediaType NV12 {wantW}x{wantH}: SUCCEEDED");
            Console.WriteLine($"  negotiated:             {w}x{h} @ {num}/{den} stride={stride}");

            var clock = Stopwatch.StartNew();
            long read = 0, lumaFrames = 0, lumaSum = 0;
            int expected = wantW * wantH * 3 / 2;
            int lastBytes = 0;

            while (read < frames)
            {
                using var sample = reader.ReadSample(
                    SourceReaderIndex.FirstVideoStream,
                    SourceReaderControlFlag.None,
                    out _,
                    out SourceReaderFlag flags,
                    out _);
                if (sample == null)
                {
                    if ((flags & SourceReaderFlag.EndOfStream) != 0)
                    {
                        Console.WriteLine($"  end of stream after {read} frames");
                        break;
                    }
                    if ((flags & SourceReaderFlag.Error) != 0)
                    {
                        Console.WriteLine("  ReadSample reported an error");
                        return 1;
                    }
                    Thread.Sleep(1);
                    continue;
                }

                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out IntPtr data, out _, out int len);
                try
                {
                    lastBytes = len;
                    read++;
                    if (lumaFrames < 60 && len >= wantW * wantH)
                    {
                        long sum = 0;
                        unsafe
                        {
                            byte* p = (byte*)data;
                            for (int i = 0; i < wantW * wantH; i++)
                                sum += p[i];
                        }
                        lumaSum += sum / (wantW * wantH);
                        lumaFrames++;
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
            }

            clock.Stop();
            double fps = clock.Elapsed.TotalSeconds > 0 ? read / clock.Elapsed.TotalSeconds : 0;
            double mean = lumaFrames > 0 ? lumaSum / (double)lumaFrames : double.NaN;
            Console.WriteLine($"  frames:                 {read} in {clock.Elapsed.TotalSeconds:F2}s = {fps:F1} fps");
            Console.WriteLine($"  sample bytes (last):    {lastBytes} (expected {expected}, delta {lastBytes - expected})");
            Console.WriteLine($"  mean Y luma:            {mean:F1}");
            return read > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FileProbe failed: {ex.Message}");
            return 1;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }
}
