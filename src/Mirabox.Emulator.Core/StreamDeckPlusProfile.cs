namespace Mirabox.Emulator.Core;

/// <summary>
/// Public HID characteristics of the Elgato Stream Deck + (20GBD9901).
/// Report lengths include the numbered report ID byte.
/// </summary>
public static class StreamDeckPlusProfile
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x0084;
    public const ushort UsagePage = 0xFF00;
    public const ushort Usage = 0x0001;

    public const byte InputReportId = 0x01;
    public const byte OutputReportId = 0x02;
    public const byte SetterFeatureReportId = 0x03;

    public const int InputReportLength = 512;
    public const int OutputReportLength = 1024;
    public const int FeatureReportLength = 32;

    public const int KeyCount = 8;
    public const int KeyColumns = 4;
    public const int KeyRows = 2;
    public const int KeyImageWidth = 120;
    public const int KeyImageHeight = 120;
    public const int EncoderCount = 4;
    public const int TouchWidth = 800;
    public const int TouchHeight = 100;
    public const int LcdWidth = 800;
    public const int LcdHeight = 480;
}
