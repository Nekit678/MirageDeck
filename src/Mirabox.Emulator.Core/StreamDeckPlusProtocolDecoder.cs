using System.Buffers.Binary;

namespace Mirabox.Emulator.Core;

/// <summary>Reassembles Stream Deck + JPEG transfers and decodes feature commands.</summary>
public sealed class StreamDeckPlusProtocolDecoder
{
    private readonly Dictionary<TransferTarget, PendingTransfer> _pending = [];

    public IReadOnlyList<DeviceUpdate> PushOutput(ReadOnlySpan<byte> report)
    {
        if (report.Length != StreamDeckPlusProfile.OutputReportLength)
            throw new ArgumentException($"Expected {StreamDeckPlusProfile.OutputReportLength} output bytes", nameof(report));
        if (report[0] != StreamDeckPlusProfile.OutputReportId)
            return [new UnknownCommandUpdate(report.ToArray())];

        return report[1] switch
        {
            0x07 => PushStandardChunk(report, TransferTarget.ForButton(report[2]), doneOffset: 3, sizeOffset: 4, indexOffset: 6, dataOffset: 8),
            0x08 => PushStandardChunk(report, TransferTarget.FullScreen, doneOffset: 3, sizeOffset: 4, indexOffset: 6, dataOffset: 8),
            0x0B => PushStandardChunk(report, TransferTarget.Window, doneOffset: 3, sizeOffset: 4, indexOffset: 6, dataOffset: 8),
            0x0C => PushPartialWindowChunk(report),
            _ => [new UnknownCommandUpdate(report.ToArray())],
        };
    }

    public IReadOnlyList<DeviceUpdate> PushFeature(ReadOnlySpan<byte> report)
    {
        if (report.Length != StreamDeckPlusProfile.FeatureReportLength)
            throw new ArgumentException($"Expected {StreamDeckPlusProfile.FeatureReportLength} feature bytes", nameof(report));
        if (report[0] != StreamDeckPlusProfile.SetterFeatureReportId)
            return [new UnknownCommandUpdate(report.ToArray())];

        return report[1] switch
        {
            0x02 => [new ShowLogoUpdate()],
            0x05 => [new FillLcdColorUpdate(report[2], report[3], report[4])],
            0x06 when report[2] < StreamDeckPlusProfile.KeyCount =>
                [new FillButtonColorUpdate(report[2], report[3], report[4], report[5])],
            0x08 => [new BrightnessUpdate((byte)Math.Min((int)report[2], 100))],
            0x0D => [new SleepDurationUpdate(BinaryPrimitives.ReadInt32LittleEndian(report.Slice(2, 4)))],
            _ => [new UnknownCommandUpdate(report.ToArray())],
        };
    }

    private IReadOnlyList<DeviceUpdate> PushPartialWindowChunk(ReadOnlySpan<byte> report)
    {
        var target = TransferTarget.PartialWindow(
            BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(2, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(4, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(8, 2)));

        if (target.Width is 0 || target.Height is 0
            || target.X + target.Width > StreamDeckPlusProfile.TouchWidth
            || target.Y + target.Height > StreamDeckPlusProfile.TouchHeight)
            throw new InvalidDataException("Partial window rectangle is outside the 800x100 touch strip");

        return PushStandardChunk(report, target, doneOffset: 10, sizeOffset: 13, indexOffset: 11, dataOffset: 16);
    }

    private IReadOnlyList<DeviceUpdate> PushStandardChunk(
        ReadOnlySpan<byte> report,
        TransferTarget target,
        int doneOffset,
        int sizeOffset,
        int indexOffset,
        int dataOffset)
    {
        if (target.Kind == TransferKind.Button && target.Button >= StreamDeckPlusProfile.KeyCount)
            throw new InvalidDataException($"Invalid button index {target.Button}");

        var contentsSize = BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(sizeOffset, 2));
        var chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(indexOffset, 2));
        if (contentsSize > report.Length - dataOffset)
            throw new InvalidDataException($"Chunk declares {contentsSize} bytes but only {report.Length - dataOffset} fit");

        PendingTransfer transfer;
        if (chunkIndex == 0)
        {
            transfer = new PendingTransfer(target);
            _pending[target] = transfer;
        }
        else
        {
            if (!_pending.TryGetValue(target, out var existing) || existing is null)
                throw new InvalidDataException($"Chunk {chunkIndex} has no matching transfer");
            transfer = existing;
        }

        if (transfer.NextChunkIndex != chunkIndex)
            throw new InvalidDataException($"Expected chunk {transfer.NextChunkIndex}, received {chunkIndex}");

        transfer.Buffer.Write(report.Slice(dataOffset, contentsSize));
        transfer.NextChunkIndex++;
        if (report[doneOffset] == 0) return [];
        if (report[doneOffset] != 1) throw new InvalidDataException("Invalid transfer completion flag");

        _pending.Remove(target);
        var image = transfer.Buffer.ToArray();
        if (image.Length == 0) throw new InvalidDataException("Image transfer is empty");

        return transfer.Target.Kind switch
        {
            TransferKind.Button => [new ButtonImageUpdate(transfer.Target.Button, image)],
            TransferKind.FullScreen => [new FullScreenImageUpdate(image)],
            TransferKind.Window => [new WindowImageUpdate(image)],
            TransferKind.PartialWindow => [new PartialWindowImageUpdate(
                transfer.Target.X, transfer.Target.Y, transfer.Target.Width, transfer.Target.Height, image)],
            _ => throw new InvalidOperationException(),
        };
    }

    private enum TransferKind { Button, FullScreen, Window, PartialWindow }

    private readonly record struct TransferTarget(
        TransferKind Kind, byte Button, ushort X, ushort Y, ushort Width, ushort Height)
    {
        public static TransferTarget ForButton(byte button) => new(TransferKind.Button, button, 0, 0, 0, 0);
        public static TransferTarget FullScreen => new(TransferKind.FullScreen, 0, 0, 0, 0, 0);
        public static TransferTarget Window => new(TransferKind.Window, 0, 0, 0, 0, 0);
        public static TransferTarget PartialWindow(ushort x, ushort y, ushort width, ushort height) =>
            new(TransferKind.PartialWindow, 0, x, y, width, height);
    }

    private sealed class PendingTransfer(TransferTarget target)
    {
        public TransferTarget Target { get; } = target;
        public MemoryStream Buffer { get; } = new();
        public ushort NextChunkIndex { get; set; }
    }
}
