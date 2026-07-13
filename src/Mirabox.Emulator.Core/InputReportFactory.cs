using System.Buffers.Binary;

namespace Mirabox.Emulator.Core;

public enum TouchPhase : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}

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
    /// N4 Pro touch contacts use a TP packet. Stream Dock uses the phase and
    /// sequence fields to separate contacts; omitting them makes a new contact
    /// look like a continuation from the previous coordinate.
    /// </summary>
    public static byte[] Touch(ushort x, ushort y, TouchPhase phase, uint timestamp, ushort sequence)
    {
        if ((byte)phase > (byte)TouchPhase.Cancel) throw new ArgumentOutOfRangeException(nameof(phase));
        var report = new byte[N4ProProfile.InputReportLength];
        report[0] = (byte)'T';
        report[1] = (byte)'P';
        report[2] = (byte)phase;
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(4, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(6, 2), y);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(8, 4), timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(12, 2), sequence);
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
