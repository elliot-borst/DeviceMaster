using DeviceMaster.Devices.Turzx;

namespace DeviceMaster.Core.Tests;

public class TurzxProtocolTests
{
    [Fact]
    public void BuildCommand_PadsUpToBlockMultipleWithNull()
    {
        var message = TurzxProtocol.BuildCommand(TurzxProtocol.Hello);

        Assert.Equal(TurzxProtocol.Block, message.Length); // 12-byte command → one 250-byte block
        Assert.Equal(TurzxProtocol.Hello, message[..TurzxProtocol.Hello.Length]);
        Assert.All(message[TurzxProtocol.Hello.Length..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildCommand_ExactMultipleIsNotPaddedFurther()
    {
        var payload = new byte[TurzxProtocol.Block - 4]; // 4-byte command + 246 payload = 250
        var command = new byte[] { 0xcc, 0xef, 0x69, 0x00 };

        var message = TurzxProtocol.BuildCommand(command, payload);

        Assert.Equal(TurzxProtocol.Block, message.Length);
    }

    [Fact]
    public void BuildBrightness_ScalesPercentToByteRange()
    {
        // 10-byte command + 1 level byte, padded to 250
        Assert.Equal(255, TurzxProtocol.BuildBrightness(100)[TurzxProtocol.SetBrightnessCmd.Length]);
        Assert.Equal(0, TurzxProtocol.BuildBrightness(0)[TurzxProtocol.SetBrightnessCmd.Length]);
        Assert.Equal(127, TurzxProtocol.BuildBrightness(50)[TurzxProtocol.SetBrightnessCmd.Length]);
        Assert.Equal(255, TurzxProtocol.BuildBrightness(150)[TurzxProtocol.SetBrightnessCmd.Length]); // clamped
    }

    [Fact]
    public void BuildStartDisplayBitmap_Is250BytesOfMarker()
    {
        var message = TurzxProtocol.BuildStartDisplayBitmap();

        Assert.Equal(TurzxProtocol.Block, message.Length);
        Assert.All(message, b => Assert.Equal(TurzxProtocol.StartDisplayBitmapByte, b));
    }

    [Fact]
    public void BuildDisplayBitmap8Inch_CarriesWidthSquaredOver64()
    {
        var message = TurzxProtocol.BuildDisplayBitmap8Inch();

        Assert.Equal(TurzxProtocol.DisplayBitmap8Inch, message[..TurzxProtocol.DisplayBitmap8Inch.Length]);
        var size = (message[TurzxProtocol.DisplayBitmap8Inch.Length] << 8)
                   | message[TurzxProtocol.DisplayBitmap8Inch.Length + 1];
        Assert.Equal(TurzxProtocol.ScreenWidth * TurzxProtocol.ScreenWidth / 64, size); // 3600
    }

    [Fact]
    public void EncodeFullFrameBody_InsertsNullAfterEvery249Bytes()
    {
        var bgra = new byte[249 * 2 + 10]; // three chunks: 249, 249, 10
        for (var i = 0; i < bgra.Length; i++)
        {
            bgra[i] = 0xAB;
        }

        var body = TurzxProtocol.EncodeFullFrameBody(bgra);

        Assert.Equal(bgra.Length + 2, body.Length); // two separators for three chunks
        Assert.Equal(0x00, body[249]);              // separator after the first run
        Assert.Equal(0x00, body[249 + 1 + 249]);    // separator after the second run
        Assert.Equal(0xAB, body[0]);
        Assert.Equal(0xAB, body[^1]);
    }

    [Fact]
    public void EncodeFullFrameBody_ShortPayloadIsUnchanged()
    {
        var bgra = new byte[] { 1, 2, 3 };

        Assert.Equal(bgra, TurzxProtocol.EncodeFullFrameBody(bgra));
    }

    [Fact]
    public void BuildSendPayload_PadsBodyToBlockMultiple()
    {
        var body = TurzxProtocol.EncodeFullFrameBody(new byte[500]);

        var message = TurzxProtocol.BuildSendPayload(body);

        Assert.Equal(0, message.Length % TurzxProtocol.Block);
        Assert.True(message.Length >= body.Length);
    }
}
