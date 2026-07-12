namespace Mirabox.Emulator.Core;

public static class InputReportFactory
{
    public static byte[] Key(byte hardwareCode, bool pressed) => Event(hardwareCode, pressed ? (byte)1 : (byte)0);

    public static byte[] KnobPress(int knob, bool pressed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(knob);
        if (knob >= N4ProProfile.KnobPressCodes.Length) throw new ArgumentOutOfRangeException(nameof(knob));
        return Event(N4ProProfile.KnobPressCodes[knob], pressed ? (byte)1 : (byte)0);
    }

    public static byte[] KnobRotate(int knob, int direction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(knob);
        if (knob >= N4ProProfile.KnobLeftCodes.Length) throw new ArgumentOutOfRangeException(nameof(knob));
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        return Event(direction < 0 ? N4ProProfile.KnobLeftCodes[knob] : N4ProProfile.KnobRightCodes[knob], 0);
    }

    public static byte[] Swipe(bool left) => Event(left ? (byte)0x38 : (byte)0x39, 0);

    public static byte[] Touch(ushort x, ushort y)
    {
        var report = Header("ARX");
        report[10] = (byte)(x >> 8);
        report[11] = (byte)x;
        report[12] = (byte)(y >> 8);
        report[13] = (byte)y;
        return report;
    }

    private static byte[] Event(byte hardwareCode, byte state)
    {
        var report = Header("OK");
        report[9] = hardwareCode;
        report[10] = state;
        return report;
    }

    private static byte[] Header(string response)
    {
        var report = new byte[N4ProProfile.InputReportLength];
        "ACK"u8.CopyTo(report);
        if (response == "OK")
        {
            report[5] = (byte)'O';
            report[6] = (byte)'K';
        }
        else
        {
            report[4] = (byte)'A';
            report[5] = (byte)'R';
            report[6] = (byte)'X';
        }
        return report;
    }
}

