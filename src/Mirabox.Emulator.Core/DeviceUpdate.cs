namespace Mirabox.Emulator.Core;

public abstract record DeviceUpdate;
public sealed record BrightnessUpdate(byte Value) : DeviceUpdate;
public sealed record ClearKeyUpdate(byte Slot) : DeviceUpdate;
public sealed record ClearAllUpdate : DeviceUpdate;
public sealed record RefreshUpdate : DeviceUpdate;
public sealed record WakeUpdate : DeviceUpdate;
public sealed record ImageUpdate(byte Slot, byte[] EncodedImage) : DeviceUpdate;
public sealed record BackgroundUpdate(byte[] EncodedImage) : DeviceUpdate;
public sealed record UnknownCommandUpdate(byte[] Packet) : DeviceUpdate;

