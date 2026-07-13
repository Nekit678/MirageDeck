namespace Mirabox.Emulator.Core;

public static class N4ProProfile
{
    public const ushort VendorId = 0x5548;
    // 0x1021 is the official global/English N4ProE variant. 0x1008 is the
    // mainland-China model and is intentionally rejected by global Stream Dock.
    public const ushort ProductId = 0x1021;
    public const ushort UsagePage = 0xFFA0;
    public const ushort Usage = 0x0001;
    public const int InputReportLength = 512;
    public const int OutputReportLength = 1024;
    public const int TouchWidth = 800;
    public const int TouchHeight = 480;

    // UI key 0..9 is top-left to bottom-right. The firmware numbers its
    // two rows as 0x01..0x05 (top) and 0x06..0x0A (bottom).
    public static readonly byte[] KeyCodes =
    [
        0x01, 0x02, 0x03, 0x04, 0x05,
        0x06, 0x07, 0x08, 0x09, 0x0A,
    ];

    public static readonly byte[] KnobPressCodes = [0x37, 0x35, 0x33, 0x36];
    public static readonly byte[] KnobLeftCodes = [0xA0, 0x50, 0x90, 0x70];
    public static readonly byte[] KnobRightCodes = [0xA1, 0x51, 0x91, 0x71];
    public static readonly byte[] SecondaryKeyCodes = [0x40, 0x41, 0x42, 0x43];
    public static readonly byte[] SecondaryImageSlots = [1, 2, 3, 4];

    // Image slots used by the N4 Pro firmware. Main keys are 11..15, 6..10.
    public static readonly byte[] ImageSlots =
    [
        11, 12, 13, 14, 15,
        6, 7, 8, 9, 10,
    ];

    public static int UiKeyForImageSlot(byte slot) => Array.IndexOf(ImageSlots, slot);
    public static int SecondaryKeyForImageSlot(byte slot) => Array.IndexOf(SecondaryImageSlots, slot);
}
