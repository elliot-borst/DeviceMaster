using System.Security.Cryptography;

namespace DeviceMaster.Devices.LianLi;

/// <summary>
/// Pure packet builders for the SL V3 per-fan LCD nodes (VID 0x1CBE), ported from
/// sgtaziz/lian-li-linux (crypto.rs + slv3_lcd.rs, MIT). Commands travel as 512-byte
/// DES-CBC-encrypted headers (key = IV = "slv3tuzx", PKCS7), optionally followed by a raw
/// JPEG payload; the whole frame transfer is padded to exactly 102,400 bytes.
/// No I/O here — fully unit-testable.
/// </summary>
public static class Slv3LcdProtocol
{
    public const byte CmdGetVersion = 0x0A;
    public const byte CmdRotate = 0x0D;
    public const byte CmdBrightness = 0x0E;
    public const byte CmdPushJpeg = 0x65;

    /// <summary>The wireless LCD fans carry a 400×400 screen.</summary>
    public const int ScreenWidth = 400;
    public const int ScreenHeight = 400;

    /// <summary>Frame transfers are one fixed-size bulk write: 512-byte header + padded JPEG.</summary>
    public const int FrameTransferSize = 102_400;
    public const int HeaderSize = 512;
    public const int MaxJpegPayload = FrameTransferSize - HeaderSize;

    private static readonly byte[] DesKey = "slv3tuzx"u8.ToArray();

    /// <summary>
    /// Builds the encrypted 512-byte command header. Plaintext layout (504 bytes):
    /// [0]=command, [2]=0x1A, [3]=0x6D, [4..8]=millisecond timestamp LE (must be strictly
    /// increasing per session), [8..]=parameters. DES-CBC/PKCS7 turns 504 bytes into 512.
    /// </summary>
    public static byte[] BuildHeader(byte command, ReadOnlySpan<byte> parameters, uint timestampMs)
    {
        var plaintext = new byte[504];
        plaintext[0] = command;
        plaintext[2] = 0x1A;
        plaintext[3] = 0x6D;
        plaintext[4] = (byte)timestampMs;
        plaintext[5] = (byte)(timestampMs >> 8);
        plaintext[6] = (byte)(timestampMs >> 16);
        plaintext[7] = (byte)(timestampMs >> 24);
        parameters[..Math.Min(parameters.Length, 496)].CopyTo(plaintext.AsSpan(8));

#pragma warning disable CA5351 // DES is the device firmware's protocol, not our choice
        using var des = DES.Create();
#pragma warning restore CA5351
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        des.Key = DesKey;
        des.IV = DesKey;
        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    /// <summary>Header announcing a JPEG push (payload size big-endian at parameter offset 0).</summary>
    public static byte[] BuildJpegHeader(int jpegLength, uint timestampMs) =>
        BuildHeader(CmdPushJpeg, [(byte)(jpegLength >> 24), (byte)(jpegLength >> 16), (byte)(jpegLength >> 8), (byte)jpegLength], timestampMs);

    /// <summary>Backlight brightness header, 0 (off) to 100.</summary>
    public static byte[] BuildBrightnessHeader(int percent, uint timestampMs) =>
        BuildHeader(CmdBrightness, [(byte)Math.Clamp(percent, 0, 100)], timestampMs);

    /// <summary>The reference's per-connection init: a bare rotate command with no parameters.</summary>
    public static byte[] BuildInitHeader(uint timestampMs) =>
        BuildHeader(CmdRotate, [], timestampMs);

    /// <summary>The fixed-size frame transfer: header, JPEG, zero padding to 102,400 bytes.</summary>
    public static byte[] BuildFrameTransfer(ReadOnlySpan<byte> header, ReadOnlySpan<byte> jpeg)
    {
        if (header.Length != HeaderSize)
        {
            throw new ArgumentException($"Header must be {HeaderSize} bytes.");
        }

        if (jpeg.Length > MaxJpegPayload)
        {
            throw new ArgumentException($"JPEG payload {jpeg.Length} exceeds the {MaxJpegPayload}-byte limit.");
        }

        var transfer = new byte[FrameTransferSize];
        header.CopyTo(transfer);
        jpeg.CopyTo(transfer.AsSpan(HeaderSize));
        return transfer;
    }
}
