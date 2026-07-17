using System.Buffers.Binary;

namespace Mirabox.Emulator.Core;

/// <summary>
/// Framing used by the panel-only HID feature report. The fixed 32-byte report
/// keeps the public Stream Deck + report sizes unchanged while allowing the
/// panel and HID minidriver to exchange larger reports in ordered chunks.
/// </summary>
public static class PanelTransportProtocol
{
    public const byte ReportId = 0x0B;
    public const int ReportLength = StreamDeckPlusProfile.FeatureReportLength;
    public const int HeaderLength = 13;
    public const int PayloadLength = ReportLength - HeaderLength;

    private const uint Magic = 0x3150444D; // "MDP1" in little-endian order.

    public static byte[] CreateResetReport()
    {
        var report = CreateHeader(PanelTransportCommand.Reset);
        return report;
    }

    public static IReadOnlyList<byte[]> CreateInjectionReports(ReadOnlySpan<byte> inputReport, byte transaction)
    {
        if (inputReport.Length != StreamDeckPlusProfile.InputReportLength)
            throw new ArgumentException($"Expected {StreamDeckPlusProfile.InputReportLength} input bytes", nameof(inputReport));
        if (inputReport[0] != StreamDeckPlusProfile.InputReportId)
            throw new ArgumentException($"Expected input report ID {StreamDeckPlusProfile.InputReportId}", nameof(inputReport));

        return CreateChunks(PanelTransportCommand.InjectChunk, PanelCaptureKind.None, inputReport, transaction);
    }

    public static IReadOnlyList<byte[]> CreateCaptureReports(
        PanelCaptureKind kind,
        ReadOnlySpan<byte> capturedReport,
        byte transaction)
    {
        var expectedLength = kind switch
        {
            PanelCaptureKind.Output => StreamDeckPlusProfile.OutputReportLength,
            PanelCaptureKind.Feature => StreamDeckPlusProfile.FeatureReportLength,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (capturedReport.Length != expectedLength)
            throw new ArgumentException($"Expected {expectedLength} captured bytes", nameof(capturedReport));

        return CreateChunks(PanelTransportCommand.CaptureChunk, kind, capturedReport, transaction);
    }

    public static PanelTransportChunk ParseResponse(ReadOnlySpan<byte> report)
    {
        ValidateHeader(report);
        var command = (PanelTransportCommand)report[5];
        if (command == PanelTransportCommand.None)
            return new(command, PanelCaptureKind.None, 0, 0, 0, 0, []);
        if (command != PanelTransportCommand.CaptureChunk)
            throw new InvalidDataException($"Unexpected panel transport command {(byte)command}");

        var kind = (PanelCaptureKind)report[6];
        var expectedTotal = kind switch
        {
            PanelCaptureKind.Output => StreamDeckPlusProfile.OutputReportLength,
            PanelCaptureKind.Feature => StreamDeckPlusProfile.FeatureReportLength,
            _ => throw new InvalidDataException($"Unknown captured report kind {(byte)kind}"),
        };
        var transaction = report[7];
        var index = report[8];
        var count = report[9];
        var payloadLength = report[10];
        var totalLength = BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(11, 2));
        var expectedCount = GetChunkCount(totalLength);
        if (totalLength != expectedTotal || count != expectedCount || index >= count)
            throw new InvalidDataException("Invalid panel transport capture geometry");
        var expectedPayload = Math.Min(PayloadLength, totalLength - index * PayloadLength);
        if (payloadLength != expectedPayload)
            throw new InvalidDataException($"Capture chunk {index} has {payloadLength} bytes; expected {expectedPayload}");

        return new(command, kind, transaction, index, count, totalLength,
            report.Slice(HeaderLength, payloadLength).ToArray());
    }

    private static IReadOnlyList<byte[]> CreateChunks(
        PanelTransportCommand command,
        PanelCaptureKind kind,
        ReadOnlySpan<byte> contents,
        byte transaction)
    {
        var count = GetChunkCount(contents.Length);
        var reports = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            var offset = index * PayloadLength;
            var length = Math.Min(PayloadLength, contents.Length - offset);
            var report = CreateHeader(command);
            report[6] = (byte)kind;
            report[7] = transaction;
            report[8] = checked((byte)index);
            report[9] = checked((byte)count);
            report[10] = checked((byte)length);
            BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(11, 2), checked((ushort)contents.Length));
            contents.Slice(offset, length).CopyTo(report.AsSpan(HeaderLength));
            reports[index] = report;
        }
        return reports;
    }

    private static byte[] CreateHeader(PanelTransportCommand command)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(1, 4), Magic);
        report[5] = (byte)command;
        return report;
    }

    private static void ValidateHeader(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportLength)
            throw new InvalidDataException($"Expected {ReportLength} panel transport bytes");
        if (report[0] != ReportId || BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(1, 4)) != Magic)
            throw new InvalidDataException("Panel transport report has an invalid ID or magic");
    }

    private static int GetChunkCount(int length) => checked((length + PayloadLength - 1) / PayloadLength);
}

public enum PanelTransportCommand : byte
{
    None = 0,
    InjectChunk = 1,
    Reset = 2,
    CaptureChunk = 3,
}

public enum PanelCaptureKind : byte
{
    None = 0,
    Output = 1,
    Feature = 2,
}

public readonly record struct PanelTransportChunk(
    PanelTransportCommand Command,
    PanelCaptureKind Kind,
    byte Transaction,
    byte Index,
    byte Count,
    ushort TotalLength,
    byte[] Data);
