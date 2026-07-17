using System.Buffers.Binary;
using Mirabox.Emulator.Core;

var tests = new (string Name, Action Run)[]
{
    ("button state report", ButtonStateReport),
    ("encoder button state report", EncoderButtonStateReport),
    ("encoder rotation report", EncoderRotationReport),
    ("touch reports", TouchReports),
    ("button image chunks", ButtonImageChunks),
    ("interleaved image chunks", InterleavedImageChunks),
    ("window and full-screen chunks", WindowAndFullScreenChunks),
    ("partial window chunks", PartialWindowChunks),
    ("feature commands", FeatureCommands),
    ("invalid chunks rejected", InvalidChunksRejected),
    ("panel input transport", PanelInputTransport),
    ("panel capture transport", PanelCaptureTransport),
    ("invalid panel transport rejected", InvalidPanelTransportRejected),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
    }
}
return failed == 0 ? 0 : 1;

static void ButtonStateReport()
{
    var report = InputReportFactory.Buttons([true, false, true, false, false, false, false, true]);
    Equal(StreamDeckPlusProfile.InputReportLength, report.Length);
    Bytes([0x01, 0x00, 0x08, 0x00, 1, 0, 1, 0, 0, 0, 0, 1], report[..12]);
}

static void EncoderButtonStateReport()
{
    var report = InputReportFactory.EncoderButtons([false, true, false, true]);
    Bytes([0x01, 0x03, 0x05, 0x00, 0x00, 0, 1, 0, 1], report[..9]);
}

static void EncoderRotationReport()
{
    var left = InputReportFactory.EncoderRotate(2, -1);
    Bytes([0x01, 0x03, 0x05, 0x00, 0x01, 0, 0, 0xFF, 0], left[..9]);
    var right = InputReportFactory.EncoderRotate(0, 3);
    Bytes([0x01, 0x03, 0x05, 0x00, 0x01, 3, 0, 0, 0], right[..9]);
}

static void TouchReports()
{
    var tap = InputReportFactory.TouchTap(799, 99);
    Bytes([0x01, 0x02, 0x0A, 0x00, 0x01, 0, 0x1F, 0x03, 0x63, 0x00], tap[..10]);

    var press = InputReportFactory.TouchPress(400, 50);
    Equal((byte)0x02, press[4]);
    Equal((ushort)400, BinaryPrimitives.ReadUInt16LittleEndian(press.AsSpan(6, 2)));
    Equal((ushort)50, BinaryPrimitives.ReadUInt16LittleEndian(press.AsSpan(8, 2)));

    var flick = InputReportFactory.TouchFlick(10, 20, 700, 80);
    Bytes([0x01, 0x02, 0x0E, 0x00, 0x03, 0, 10, 0, 20, 0, 0xBC, 0x02, 80, 0], flick[..14]);
}

static void ButtonImageChunks()
{
    var decoder = new StreamDeckPlusProtocolDecoder();
    var first = OutputChunk(0x07, target: 5, chunk: 0, done: false, [1, 2, 3]);
    Equal(0, decoder.PushOutput(first).Count);
    var second = OutputChunk(0x07, target: 5, chunk: 1, done: true, [4, 5]);
    var update = Single<ButtonImageUpdate>(decoder.PushOutput(second));
    Equal((byte)5, update.Button);
    Bytes([1, 2, 3, 4, 5], update.EncodedImage);
}

static void InterleavedImageChunks()
{
    var decoder = new StreamDeckPlusProtocolDecoder();
    Equal(0, decoder.PushOutput(OutputChunk(0x07, target: 0, chunk: 0, done: false, [1])).Count);
    Equal(0, decoder.PushOutput(OutputChunk(0x07, target: 1, chunk: 0, done: false, [2])).Count);
    var first = Single<ButtonImageUpdate>(decoder.PushOutput(
        OutputChunk(0x07, target: 0, chunk: 1, done: true, [3])));
    var second = Single<ButtonImageUpdate>(decoder.PushOutput(
        OutputChunk(0x07, target: 1, chunk: 1, done: true, [4])));
    Bytes([1, 3], first.EncodedImage);
    Bytes([2, 4], second.EncodedImage);
}

static void WindowAndFullScreenChunks()
{
    var decoder = new StreamDeckPlusProtocolDecoder();
    var window = Single<WindowImageUpdate>(decoder.PushOutput(
        OutputChunk(0x0B, target: 0, chunk: 0, done: true, [0xFF, 0xD8, 0xFF, 0xD9])));
    Bytes([0xFF, 0xD8, 0xFF, 0xD9], window.EncodedImage);

    var full = Single<FullScreenImageUpdate>(decoder.PushOutput(
        OutputChunk(0x08, target: 0, chunk: 0, done: true, [9, 8, 7])));
    Bytes([9, 8, 7], full.EncodedImage);
}

static void PartialWindowChunks()
{
    var decoder = new StreamDeckPlusProtocolDecoder();
    var report = new byte[StreamDeckPlusProfile.OutputReportLength];
    report[0] = 0x02;
    report[1] = 0x0C;
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2, 2), 200);
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(6, 2), 200);
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(8, 2), 100);
    report[10] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(11, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(13, 2), 4);
    report[16] = 1; report[17] = 3; report[18] = 3; report[19] = 7;

    var update = Single<PartialWindowImageUpdate>(decoder.PushOutput(report));
    Equal((ushort)200, update.X);
    Equal((ushort)100, update.Height);
    Bytes([1, 3, 3, 7], update.EncodedImage);
}

static void FeatureCommands()
{
    var decoder = new StreamDeckPlusProtocolDecoder();
    var feature = new byte[StreamDeckPlusProfile.FeatureReportLength];
    feature[0] = 0x03;
    feature[1] = 0x08;
    feature[2] = 73;
    Equal((byte)73, Single<BrightnessUpdate>(decoder.PushFeature(feature)).Value);

    feature[1] = 0x06;
    feature[2] = 7;
    feature[3] = 10; feature[4] = 20; feature[5] = 30;
    var fill = Single<FillButtonColorUpdate>(decoder.PushFeature(feature));
    Equal((byte)7, fill.Button);
    Equal((byte)30, fill.Blue);

    feature[1] = 0x0D;
    BinaryPrimitives.WriteInt32LittleEndian(feature.AsSpan(2, 4), 900);
    Equal(900, Single<SleepDurationUpdate>(decoder.PushFeature(feature)).Seconds);
}

static void InvalidChunksRejected()
{
    var decoder = new StreamDeckPlusProtocolDecoder();
    Throws<InvalidDataException>(() => decoder.PushOutput(
        OutputChunk(0x07, target: 8, chunk: 0, done: true, [1])));
    Throws<InvalidDataException>(() => decoder.PushOutput(
        OutputChunk(0x07, target: 0, chunk: 2, done: true, [1])));
}

static void PanelInputTransport()
{
    var input = new byte[StreamDeckPlusProfile.InputReportLength];
    input[0] = StreamDeckPlusProfile.InputReportId;
    for (var i = 1; i < input.Length; i++) input[i] = (byte)(i * 17);

    var reports = PanelTransportProtocol.CreateInjectionReports(input, transaction: 41);
    Equal(27, reports.Count);
    var reassembled = new byte[input.Length];
    for (var index = 0; index < reports.Count; index++)
    {
        var report = reports[index];
        Equal(PanelTransportProtocol.ReportLength, report.Length);
        Equal(PanelTransportProtocol.ReportId, report[0]);
        Equal((byte)PanelTransportCommand.InjectChunk, report[5]);
        Equal((byte)41, report[7]);
        Equal((byte)index, report[8]);
        Equal((byte)reports.Count, report[9]);
        Equal((ushort)input.Length, BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(11, 2)));
        report.AsSpan(PanelTransportProtocol.HeaderLength, report[10])
            .CopyTo(reassembled.AsSpan(index * PanelTransportProtocol.PayloadLength));
    }
    Bytes(input, reassembled);

    var reset = PanelTransportProtocol.CreateResetReport();
    Equal((byte)PanelTransportCommand.Reset, reset[5]);
}

static void PanelCaptureTransport()
{
    var output = new byte[StreamDeckPlusProfile.OutputReportLength];
    output[0] = StreamDeckPlusProfile.OutputReportId;
    for (var i = 1; i < output.Length; i++) output[i] = (byte)(i * 29);

    var reports = PanelTransportProtocol.CreateCaptureReports(PanelCaptureKind.Output, output, transaction: 77);
    Equal(54, reports.Count);
    var reassembled = new byte[output.Length];
    for (var index = 0; index < reports.Count; index++)
    {
        var chunk = PanelTransportProtocol.ParseResponse(reports[index]);
        Equal(PanelTransportCommand.CaptureChunk, chunk.Command);
        Equal(PanelCaptureKind.Output, chunk.Kind);
        Equal((byte)77, chunk.Transaction);
        Equal((byte)index, chunk.Index);
        Equal((byte)reports.Count, chunk.Count);
        chunk.Data.CopyTo(reassembled, index * PanelTransportProtocol.PayloadLength);
    }
    Bytes(output, reassembled);

    var empty = new byte[PanelTransportProtocol.ReportLength];
    PanelTransportProtocol.CreateResetReport().AsSpan(0, 5).CopyTo(empty);
    Equal(PanelTransportCommand.None, PanelTransportProtocol.ParseResponse(empty).Command);
}

static void InvalidPanelTransportRejected()
{
    var invalidMagic = new byte[PanelTransportProtocol.ReportLength];
    invalidMagic[0] = PanelTransportProtocol.ReportId;
    Throws<InvalidDataException>(() => PanelTransportProtocol.ParseResponse(invalidMagic));

    var capture = PanelTransportProtocol.CreateCaptureReports(
        PanelCaptureKind.Feature,
        new byte[StreamDeckPlusProfile.FeatureReportLength],
        transaction: 1)[0];
    capture[9]++;
    Throws<InvalidDataException>(() => PanelTransportProtocol.ParseResponse(capture));
}

static byte[] OutputChunk(byte command, byte target, ushort chunk, bool done, byte[] data)
{
    var report = new byte[StreamDeckPlusProfile.OutputReportLength];
    report[0] = 0x02;
    report[1] = command;
    report[2] = target;
    report[3] = done ? (byte)1 : (byte)0;
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4, 2), checked((ushort)data.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(6, 2), chunk);
    data.CopyTo(report, 8);
    return report;
}

static T Single<T>(IReadOnlyList<DeviceUpdate> updates) where T : DeviceUpdate
{
    Equal(1, updates.Count);
    return updates[0] as T ?? throw new Exception($"Expected {typeof(T).Name}, got {updates[0].GetType().Name}");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}

static void Bytes(byte[] expected, byte[] actual)
{
    if (!expected.SequenceEqual(actual))
        throw new Exception($"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
