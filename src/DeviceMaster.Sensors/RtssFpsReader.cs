using System.IO;
using System.IO.MemoryMappedFiles;

namespace DeviceMaster.Sensors;

/// <summary>
/// Reads the current framerate from RivaTuner Statistics Server's shared memory
/// (<c>RTSSSharedMemoryV2</c>, created by RTSS / MSI Afterburner while it is running). Returns
/// null when RTSS isn't running or nothing is being rendered, so callers can show a placeholder.
///
/// Layout (RTSS_SHARED_MEMORY + RTSS_SHARED_MEMORY_APP_ENTRY, documented in the RTSS SDK):
/// header holds the app-array offset/size/stride; each app entry carries a frame count over a
/// [dwTime0, dwTime1] window in ms. The "current" app is the entry updated most recently
/// (largest dwTime1). FPS = dwFrames * 1000 / (dwTime1 - dwTime0).
/// </summary>
public static class RtssFpsReader
{
    private const string MapName = "RTSSSharedMemoryV2";

    // header field offsets
    private const int OffAppEntrySize = 8;
    private const int OffAppArrOffset = 12;
    private const int OffAppArrSize = 16;

    // app-entry field offsets (within one entry)
    private const int EntProcessId = 0;
    private const int EntTime0 = 268;
    private const int EntTime1 = 272;
    private const int EntFrames = 276;

    /// <summary>Current framerate of the most-recently-updated app, or null if RTSS has nothing.</summary>
    public static int? ReadCurrentFps()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            var entrySize = view.ReadUInt32(OffAppEntrySize);
            var arrOffset = view.ReadUInt32(OffAppArrOffset);
            var arrSize = view.ReadUInt32(OffAppArrSize);

            // sanity-check the header rather than hard-matching a signature (endianness-proof)
            if (arrOffset < 20 || entrySize is < 64 or > 1_000_000 || arrSize is 0 or > 100_000)
            {
                return null;
            }

            var bestFps = 0;
            var bestTime = 0u;
            for (uint i = 0; i < arrSize; i++)
            {
                var entry = arrOffset + (long)i * entrySize;
                if (view.ReadUInt32(entry + EntProcessId) == 0)
                {
                    continue; // empty slot
                }

                var t0 = view.ReadUInt32(entry + EntTime0);
                var t1 = view.ReadUInt32(entry + EntTime1);
                var frames = view.ReadUInt32(entry + EntFrames);
                if (t1 <= t0 || frames == 0)
                {
                    continue;
                }

                var fps = (int)(frames * 1000UL / (t1 - t0));
                if (fps > 0 && t1 >= bestTime)
                {
                    bestTime = t1;
                    bestFps = fps;
                }
            }

            return bestFps > 0 ? bestFps : null;
        }
        catch (FileNotFoundException)
        {
            return null; // RTSS not running — no shared memory
        }
        catch
        {
            return null; // any parse/access issue: treat as "no FPS"
        }
    }
}
