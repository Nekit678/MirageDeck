using System.Buffers.Binary;

namespace Mirabox.Emulator.Core;

/// <summary>Builds the numbered input reports emitted by a Stream Deck +.</summary>
public static class InputReportFactory
{
    public static byte[] Buttons(ReadOnlySpan<bool> pressed)
    {
        if (pressed.Length != StreamDeckPlusProfile.KeyCount)
            throw new ArgumentException($"Expected {StreamDeckPlusProfile.KeyCount} button states", nameof(pressed));

        var report = Header(command: 0x00, payloadLength: StreamDeckPlusProfile.KeyCount);
        for (var i = 0; i < pressed.Length; i++) report[4 + i] = pressed[i] ? (byte)1 : (byte)0;
        return report;
    }

    public static byte[] EncoderButtons(ReadOnlySpan<bool> pressed)
    {
        if (pressed.Length != StreamDeckPlusProfile.EncoderCount)
            throw new ArgumentException($"Expected {StreamDeckPlusProfile.EncoderCount} encoder states", nameof(pressed));

        var report = Header(command: 0x03, payloadLength: StreamDeckPlusProfile.EncoderCount + 1);
        report[4] = 0x00;
        for (var i = 0; i < pressed.Length; i++) report[5 + i] = pressed[i] ? (byte)1 : (byte)0;
        return report;
    }

    public static byte[] EncoderRotate(int encoder, int ticks)
    {
        if ((uint)encoder >= StreamDeckPlusProfile.EncoderCount)
            throw new ArgumentOutOfRangeException(nameof(encoder));
        if (ticks is < sbyte.MinValue or > sbyte.MaxValue || ticks == 0)
            throw new ArgumentOutOfRangeException(nameof(ticks));

        var report = Header(command: 0x03, payloadLength: StreamDeckPlusProfile.EncoderCount + 1);
        report[4] = 0x01;
        report[5 + encoder] = unchecked((byte)(sbyte)ticks);
        return report;
    }

    public static byte[] TouchTap(ushort x, ushort y) => TouchPoint(contentsType: 0x01, x, y);

    public static byte[] TouchPress(ushort x, ushort y) => TouchPoint(contentsType: 0x02, x, y);

    public static byte[] TouchFlick(ushort startX, ushort startY, ushort endX, ushort endY)
    {
        ValidateTouch(startX, startY);
        ValidateTouch(endX, endY);
        var report = Header(command: 0x02, payloadLength: 0x0E);
        report[4] = 0x03;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(6, 2), startX);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(8, 2), startY);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(10, 2), endX);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(12, 2), endY);
        return report;
    }

    private static byte[] TouchPoint(byte contentsType, ushort x, ushort y)
    {
        ValidateTouch(x, y);
        var report = Header(command: 0x02, payloadLength: 0x0A);
        report[4] = contentsType;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(6, 2), x);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(8, 2), y);
        return report;
    }

    private static void ValidateTouch(ushort x, ushort y)
    {
        if (x >= StreamDeckPlusProfile.TouchWidth) throw new ArgumentOutOfRangeException(nameof(x));
        if (y >= StreamDeckPlusProfile.TouchHeight) throw new ArgumentOutOfRangeException(nameof(y));
    }

    private static byte[] Header(byte command, ushort payloadLength)
    {
        var report = new byte[StreamDeckPlusProfile.InputReportLength];
        report[0] = StreamDeckPlusProfile.InputReportId;
        report[1] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2, 2), payloadLength);
        return report;
    }
}
