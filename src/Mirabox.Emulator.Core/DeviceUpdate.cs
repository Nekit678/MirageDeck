namespace Mirabox.Emulator.Core;

public abstract record DeviceUpdate;
public sealed record ButtonImageUpdate(byte Button, byte[] EncodedImage) : DeviceUpdate;
public sealed record FullScreenImageUpdate(byte[] EncodedImage) : DeviceUpdate;
public sealed record WindowImageUpdate(byte[] EncodedImage) : DeviceUpdate;
public sealed record PartialWindowImageUpdate(ushort X, ushort Y, ushort Width, ushort Height, byte[] EncodedImage) : DeviceUpdate;
public sealed record BrightnessUpdate(byte Value) : DeviceUpdate;
public sealed record ShowLogoUpdate : DeviceUpdate;
public sealed record FillLcdColorUpdate(byte Red, byte Green, byte Blue) : DeviceUpdate;
public sealed record FillButtonColorUpdate(byte Button, byte Red, byte Green, byte Blue) : DeviceUpdate;
public sealed record SleepDurationUpdate(int Seconds) : DeviceUpdate;
public sealed record UnknownCommandUpdate(byte[] Report) : DeviceUpdate;
