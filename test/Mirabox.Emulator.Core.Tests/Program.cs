using Mirabox.Emulator.Core;
using System.Buffers.Binary;

var tests = new (string Name, Action Run)[]
{
    ("input key packet", TestInputKey),
    ("input knob packet", TestInputKnob),
    ("brightness command", TestBrightness),
    ("fragmented key image", TestImage),
    ("background image", TestBackground),
    ("report-id prefix", TestReportIdPrefix),
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
return failed;

static void TestInputKey()
{
    var report = InputReportFactory.Key(0x04, true);
    Equal(512, report.Length);
    Equal((byte)'A', report[0]);
    Equal((byte)'O', report[5]);
    Equal((byte)0x04, report[9]);
    Equal((byte)1, report[10]);
}

static void TestInputKnob()
{
    var report = InputReportFactory.KnobRotate(2, -1);
    Equal((byte)0x90, report[9]);
    Equal((byte)0, report[10]);
}

static void TestBrightness()
{
    var packet = Packet("LIG");
    packet[10] = 42;
    var update = Single<BrightnessUpdate>(new MiraboxProtocolDecoder().Push(packet));
    Equal((byte)42, update.Value);
}

static void TestImage()
{
    var bytes = Enumerable.Range(0, 1500).Select(i => (byte)(i % 251)).ToArray();
    var command = Packet("BAT");
    BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(8, 4), (uint)bytes.Length);
    command[12] = 11;
    var decoder = new MiraboxProtocolDecoder();
    Equal(0, decoder.Push(command).Count);
    var first = new byte[1024];
    bytes.AsSpan(0, 1024).CopyTo(first);
    Equal(0, decoder.Push(first).Count);
    var second = new byte[1024];
    bytes.AsSpan(1024).CopyTo(second);
    var update = Single<ImageUpdate>(decoder.Push(second));
    Equal((byte)11, update.Slot);
    True(bytes.SequenceEqual(update.EncodedImage));
}

static void TestBackground()
{
    var command = Packet("LOG");
    BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(8, 4), 4);
    var decoder = new MiraboxProtocolDecoder();
    decoder.Push(command);
    var data = new byte[1024];
    data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; data[3] = 0xD9;
    var update = Single<BackgroundUpdate>(decoder.Push(data));
    Equal(4, update.EncodedImage.Length);
}

static void TestReportIdPrefix()
{
    var packet = new byte[1025];
    Packet("STP").CopyTo(packet, 1);
    _ = Single<RefreshUpdate>(new MiraboxProtocolDecoder().Push(packet));
}

static byte[] Packet(string command)
{
    var packet = new byte[1024];
    "CRT\0\0"u8.CopyTo(packet);
    System.Text.Encoding.ASCII.GetBytes(command).CopyTo(packet, 5);
    return packet;
}

static T Single<T>(IReadOnlyList<DeviceUpdate> updates) where T : DeviceUpdate
{
    Equal(1, updates.Count);
    return updates[0] as T ?? throw new Exception($"Expected {typeof(T).Name}, got {updates[0].GetType().Name}");
}

static void Equal<T>(T expected, T actual) where T : IEquatable<T>
{
    if (!expected.Equals(actual)) throw new Exception($"Expected {expected}, got {actual}");
}

static void True(bool value)
{
    if (!value) throw new Exception("Expected true");
}

