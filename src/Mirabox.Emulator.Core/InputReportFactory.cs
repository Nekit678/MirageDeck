namespace Mirabox.Emulator.Core;

public static class InputReportFactory
{
    public static byte[] Key(byte hardwareCode, bool pressed) => Event(hardwareCode, pressed ? (byte)1 : (byte)0);

    /// <summary>
    /// N4 Pro encoders report a single press event. Unlike the main keys, the
    /// firmware does not produce a matching release packet.
    /// </summary>
    public static byte[] KnobPress(int knob)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(knob);
        if (knob >= N4ProProfile.KnobPressCodes.Length) throw new ArgumentOutOfRangeException(nameof(knob));
        return Event(N4ProProfile.KnobPressCodes[knob], 1);
    }

    public static byte[] KnobRotate(int knob, int direction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(knob);
        if (knob >= N4ProProfile.KnobLeftCodes.Length) throw new ArgumentOutOfRangeException(nameof(knob));
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        return Event(direction < 0 ? N4ProProfile.KnobLeftCodes[knob] : N4ProProfile.KnobRightCodes[knob], 0);
    }

    public static byte[] Swipe(bool left) => Event(left ? (byte)0x38 : (byte)0x39, 0);

    /// <summary>
    /// In Knob Mode the four touchscreen slots report one release-style event
    /// (0x40..0x43, state=0) when tapped.
    /// </summary>
    public static byte[] SecondaryTap(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= N4ProProfile.SecondaryKeyCodes.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return Event(N4ProProfile.SecondaryKeyCodes[index], 0);
    }

    /// <summary>
    /// N4 Pro touch points use the ACK ARX packet documented by the official
    /// Device SDK. The protocol carries coordinates only and has no contact
    /// phase or sequence field.
    /// </summary>
    public static byte[] Touch(ushort x, ushort y)
    {
        var report = new byte[N4ProProfile.InputReportLength];
        "ACK"u8.CopyTo(report);
        "ARX"u8.CopyTo(report.AsSpan(4));
        report[10] = (byte)(x >> 8);
        report[11] = (byte)x;
        report[12] = (byte)(y >> 8);
        report[13] = (byte)y;
        return report;
    }

    /// <summary>
    /// A stationary N4 Pro touch uses the ordinary ACK/OK header, followed by
    /// a contact phase and the big-endian horizontal coordinate. Stream Dock
    /// turns a matching down/up pair into a touchbar item click.
    /// </summary>
    public static byte[] TouchContact(ushort x, bool pressed)
    {
        var report = Header();
        report[10] = pressed ? (byte)1 : (byte)0;
        report[11] = (byte)(x >> 8);
        report[12] = (byte)x;
        return report;
    }

    private static byte[] Event(byte hardwareCode, byte state)
    {
        var report = Header();
        report[9] = hardwareCode;
        report[10] = state;
        return report;
    }

    private static byte[] Header()
    {
        var report = new byte[N4ProProfile.InputReportLength];
        "ACK"u8.CopyTo(report);
        report[5] = (byte)'O';
        report[6] = (byte)'K';
        return report;
    }
}
