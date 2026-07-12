using System.Buffers.Binary;

namespace Mirabox.Emulator.Core;

/// <summary>
/// Reassembles the packet stream used by the original Stream Dock hardware.
/// Commands are 1024-byte HID output packets. BAT/BGPIC announce a following
/// image byte stream, which can span an arbitrary number of packets.
/// </summary>
public sealed class MiraboxProtocolDecoder
{
    private PendingTransfer? _pending;

    public IReadOnlyList<DeviceUpdate> Push(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == N4ProProfile.OutputReportLength + 1 && packet[0] == 0)
            packet = packet[1..];
        if (packet.Length != N4ProProfile.OutputReportLength)
            throw new ArgumentException($"Expected {N4ProProfile.OutputReportLength} output bytes", nameof(packet));

        if (_pending is not null)
            return ConsumeTransfer(packet);

        if (!packet[..5].SequenceEqual("CRT\0\0"u8))
            return [new UnknownCommandUpdate(packet.ToArray())];

        var command = packet.Slice(5, 3);
        if (command.SequenceEqual("LIG"u8)) return [new BrightnessUpdate(packet[10])];
        if (command.SequenceEqual("DIS"u8)) return [new WakeUpdate()];
        if (command.SequenceEqual("STP"u8)) return [new RefreshUpdate()];
        if (command.SequenceEqual("CLE"u8))
            return packet[11] == 0xFF ? [new ClearAllUpdate()] : [new ClearKeyUpdate(packet[11])];

        if (command.SequenceEqual("BAT"u8))
        {
            StartTransfer(BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4)), packet[12], false);
            return [];
        }

        if (command.SequenceEqual("LOG"u8))
        {
            StartTransfer(BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4)), 0, true);
            return [];
        }

        // Newer transports call the full-screen operation BGPIC. The length is
        // stored big-endian directly after the five-byte command name.
        if (packet.Slice(5, 5).SequenceEqual("BGPIC"u8))
        {
            StartTransfer(BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(10, 4)), 0, true);
            return [];
        }

        return [new UnknownCommandUpdate(packet.ToArray())];
    }

    private void StartTransfer(uint length, byte slot, bool background)
    {
        if (length is 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException($"Invalid image transfer length: {length}");
        _pending = new PendingTransfer(checked((int)length), slot, background);
    }

    private IReadOnlyList<DeviceUpdate> ConsumeTransfer(ReadOnlySpan<byte> packet)
    {
        var transfer = _pending!;
        var take = Math.Min(packet.Length, transfer.Buffer.Length - transfer.Offset);
        packet[..take].CopyTo(transfer.Buffer.AsSpan(transfer.Offset));
        transfer.Offset += take;
        if (transfer.Offset != transfer.Buffer.Length) return [];

        _pending = null;
        return transfer.Background
            ? [new BackgroundUpdate(transfer.Buffer)]
            : [new ImageUpdate(transfer.Slot, transfer.Buffer)];
    }

    private sealed class PendingTransfer(int length, byte slot, bool background)
    {
        public byte[] Buffer { get; } = new byte[length];
        public byte Slot { get; } = slot;
        public bool Background { get; } = background;
        public int Offset { get; set; }
    }
}
