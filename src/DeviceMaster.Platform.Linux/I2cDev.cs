using System.Runtime.InteropServices;

namespace DeviceMaster.Platform.Linux;

/// <summary>
/// Raw i2c-dev access to a /dev/i2c-N node. The nvidia driver's I2C adapters reject the SMBus
/// ioctls (EINVAL) — only I2C_RDWR works — so every transaction is a sequence of raw messages
/// in ONE ioctl call, which the adapter executes with repeated starts between same-direction
/// messages. This mirrors OpenRGB's i2c_smbus_linux fallback, which the ENE register logic in
/// EneRgbDevice was ported from.
/// </summary>
public static class I2cDev
{
    private const ushort I2cMWrite = 0x00;
    private const ushort I2cMRead = 0x01;
    private const uint I2cRdwrIoctl = 0x0706;
    private const int O_RDWR = 0x0002;

    // mirrors struct i2c_msg (include/uapi/linux/i2c.h): __u16 addr, __u16 flags, __u16 len, __u8 *buf.
    // flags/len MUST be u16: packing them as bytes shifts len into padding and the kernel
    // sees garbage flags and len=0 — every transaction fails.
    [StructLayout(LayoutKind.Sequential)]
    private struct I2cMsg
    {
        public ushort Addr;
        public ushort Flags;
        public ushort Len;
        public IntPtr Buf;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct I2cRdwr
    {
        public uint Msgs;
        public IntPtr Msg;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, uint request, IntPtr arg);

    /// <summary>One raw message: the 7-bit address, direction, and payload.</summary>
    public readonly record struct Message(ushort Address, bool Write, byte[] Data);

    /// <summary>Opens the i2c-dev node. Returns the fd, or -1 on failure.</summary>
    public static int OpenDevice(string path)
    {
        var fd = Open(path, O_RDWR);
        return fd < 0 ? -1 : fd;
    }

    public static void CloseDevice(int fd)
    {
        if (fd >= 0)
        {
            Close(fd);
        }
    }

    /// <summary>
    /// Runs the messages as ONE I2C_RDWR transaction (repeated starts where the adapter
    /// supports them). Returns 0 on success, -1 otherwise.
    /// </summary>
    public static int Run(int fd, IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0)
        {
            return -1;
        }

        var msgSize = Marshal.SizeOf<I2cMsg>();
        var msgArray = Marshal.AllocHGlobal(messages.Count * msgSize);
        var rdwrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<I2cRdwr>());
        var pointers = new IntPtr[messages.Count];
        try
        {
            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                pointers[i] = IntPtr.Zero;
                if (message.Data.Length > 0)
                {
                    pointers[i] = Marshal.AllocHGlobal(message.Data.Length);
                    Marshal.Copy(message.Data, 0, pointers[i], message.Data.Length);
                }

                var msg = new I2cMsg
                {
                    Addr = message.Address,
                    Flags = (ushort)(message.Write ? I2cMWrite : I2cMRead),
                    Len = (ushort)message.Data.Length,
                    Buf = pointers[i],
                };

                Marshal.StructureToPtr(msg, msgArray + i * msgSize, false);
            }

            var rdwr = new I2cRdwr { Msgs = (uint)messages.Count, Msg = msgArray };
            Marshal.StructureToPtr(rdwr, rdwrPtr, false);

            return Ioctl(fd, I2cRdwrIoctl, rdwrPtr) == 0 ? 0 : -1;
        }
        catch
        {
            return -1;
        }
        finally
        {
            for (var i = 0; i < pointers.Length; i++)
            {
                if (pointers[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pointers[i]);
                }
            }

            Marshal.FreeHGlobal(msgArray);
            Marshal.FreeHGlobal(rdwrPtr);
        }
    }
}
