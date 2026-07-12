/*
 * Virtual Mirabox N4 Pro HID minidriver.
 * The WDF/HID request plumbing is derived from Microsoft's vhidmini2 sample
 * and remains available under the Microsoft Public License (see LICENSE-MS-PL).
 */
#include "vhidmini.h"

#define N4PRO_VID     0x5548
#define N4PRO_PID     0x1008
#define N4PRO_VERSION 0x0002

static const WCHAR ManufacturerString[] = L"HOTSPOTEKUSB";
static const WCHAR ProductString[] = L"HOTSPOTEKUSB HID DEMO";
static const WCHAR SerialString[] = L"123456712345";
static const UCHAR FirmwareVersion[] = "V4.N4 Pro E.02.009";

/* {9A6C3D56-2683-4B22-9359-8FB4C38479B9} */
static const GUID GUID_DEVINTERFACE_MIRABOX_EMULATOR = {
    0x9a6c3d56, 0x2683, 0x4b22,
    { 0x93, 0x59, 0x8f, 0xb4, 0xc3, 0x84, 0x79, 0xb9 }
};

/* Exact 36-byte report descriptor extracted from N4 Pro firmware 02.009. */
static HID_REPORT_DESCRIPTOR G_ReportDescriptor[] = {
    0x06, 0xA0, 0xFF, 0x09, 0x01, 0xA1, 0x01, 0x09, 0x02,
    0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x96, 0x00,
    0x02, 0x81, 0x02, 0x09, 0x03, 0x15, 0x00, 0x26, 0xFF,
    0x00, 0x75, 0x08, 0x96, 0x00, 0x04, 0x91, 0x02, 0xC0
};
C_ASSERT(sizeof(G_ReportDescriptor) == 36);

static HID_DESCRIPTOR G_HidDescriptor = {
    0x09, 0x21, 0x0200, 0x00, 0x01,
    { { 0x22, sizeof(G_ReportDescriptor) } }
};

static NTSTATUS CreateQueues(WDFDEVICE Device);
static NTSTATUS ReadReport(PDEVICE_CONTEXT Context, WDFREQUEST Request, BOOLEAN* Complete);
static NTSTATUS CaptureOutput(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS InjectInput(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS GetCapturedOutput(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS GetInputReport(WDFREQUEST Request);
static NTSTATUS GetString(WDFREQUEST Request);
static VOID ProcessProtocolLocked(PDEVICE_CONTEXT Context, const UCHAR* Payload);
static VOID EnqueueInputLocked(PDEVICE_CONTEXT Context, const UCHAR* Report);
static VOID CompletePendingRead(PDEVICE_CONTEXT Context);

NTSTATUS
DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
EvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES lockAttributes;
    WDFDEVICE device;
    PDEVICE_CONTEXT context;
    NTSTATUS status;
    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    context = GetDeviceContext(device);
    RtlZeroMemory(context, sizeof(*context));
    context->Device = device;
    context->HidDescriptor = G_HidDescriptor;
    context->ReportDescriptor = G_ReportDescriptor;
    context->HidAttributes.Size = sizeof(HID_DEVICE_ATTRIBUTES);
    context->HidAttributes.VendorID = N4PRO_VID;
    context->HidAttributes.ProductID = N4PRO_PID;
    context->HidAttributes.VersionNumber = N4PRO_VERSION;

    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = device;
    status = WdfWaitLockCreate(&lockAttributes, &context->RingLock);
    if (!NT_SUCCESS(status)) return status;
    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_MIRABOX_EMULATOR, NULL);
    if (!NT_SUCCESS(status)) return status;
    return CreateQueues(device);
}

#ifndef _KERNEL_MODE
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtIoDeviceControl;
#endif

static NTSTATUS
CreateQueues(WDFDEVICE Device)
{
    WDF_IO_QUEUE_CONFIG config;
    PDEVICE_CONTEXT context = GetDeviceContext(Device);
    NTSTATUS status;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&config, WdfIoQueueDispatchParallel);
    config.EvtIoDeviceControl = EvtIoDeviceControl;
    status = WdfIoQueueCreate(Device, &config, WDF_NO_OBJECT_ATTRIBUTES, &context->DefaultQueue);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG_INIT(&config, WdfIoQueueDispatchManual);
    return WdfIoQueueCreate(Device, &config, WDF_NO_OBJECT_ATTRIBUTES, &context->ReadQueue);
}

VOID
EvtIoDeviceControl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputLength,
                   size_t InputLength, ULONG IoControlCode)
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfIoQueueGetDevice(Queue));
    NTSTATUS status = STATUS_NOT_IMPLEMENTED;
    BOOLEAN complete = TRUE;
    UNREFERENCED_PARAMETER(OutputLength);
    UNREFERENCED_PARAMETER(InputLength);

    switch (IoControlCode) {
    case IOCTL_HID_GET_DEVICE_DESCRIPTOR:
        status = RequestCopyFromBuffer(Request, &context->HidDescriptor, context->HidDescriptor.bLength);
        break;
    case IOCTL_HID_GET_DEVICE_ATTRIBUTES:
        status = RequestCopyFromBuffer(Request, &context->HidAttributes, sizeof(context->HidAttributes));
        break;
    case IOCTL_HID_GET_REPORT_DESCRIPTOR:
        status = RequestCopyFromBuffer(Request, context->ReportDescriptor, sizeof(G_ReportDescriptor));
        break;
    case IOCTL_HID_READ_REPORT:
        status = ReadReport(context, Request, &complete);
        break;
    case IOCTL_HID_WRITE_REPORT:
    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT:
        status = CaptureOutput(context, Request);
        break;
    case IOCTL_MIRABOX_INJECT_INPUT:
        status = InjectInput(context, Request);
        break;
    case IOCTL_MIRABOX_GET_OUTPUT:
        status = GetCapturedOutput(context, Request);
        break;
    case IOCTL_UMDF_HID_GET_INPUT_REPORT:
        status = GetInputReport(Request);
        break;
    case IOCTL_HID_GET_STRING:
        status = GetString(Request);
        break;
    case IOCTL_HID_ACTIVATE_DEVICE:
    case IOCTL_HID_DEACTIVATE_DEVICE:
        status = STATUS_SUCCESS;
        break;
    default:
        break;
    }
    if (complete) WdfRequestComplete(Request, status);
}

static NTSTATUS
ReadReport(PDEVICE_CONTEXT Context, WDFREQUEST Request, BOOLEAN* Complete)
{
    UCHAR report[N4PRO_INPUT_REPORT_SIZE];
    NTSTATUS status;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (Context->InputCount != 0) {
        RtlCopyMemory(report, Context->InputRing[Context->InputHead], sizeof(report));
        Context->InputHead = (Context->InputHead + 1) % N4PRO_RING_CAPACITY;
        Context->InputCount--;
        WdfWaitLockRelease(Context->RingLock);
        return RequestCopyFromBuffer(Request, report, sizeof(report));
    }
    status = WdfRequestForwardToIoQueue(Request, Context->ReadQueue);
    WdfWaitLockRelease(Context->RingLock);
    *Complete = !NT_SUCCESS(status);
    return status;
}

static const UCHAR*
OutputPayload(const HID_XFER_PACKET* Packet)
{
    if (Packet->reportBufferLen >= N4PRO_OUTPUT_REPORT_SIZE + 1 && Packet->reportBuffer[0] == 0)
        return Packet->reportBuffer + 1;
    return Packet->reportBuffer;
}

static NTSTATUS
CaptureOutput(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    HID_XFER_PACKET packet;
    const UCHAR* payload;
    ULONG tail;
    NTSTATUS status = RequestGetHidXferPacketToWrite(Request, &packet);
    if (!NT_SUCCESS(status)) return status;
    if (packet.reportId != 0 || packet.reportBufferLen < N4PRO_OUTPUT_REPORT_SIZE)
        return STATUS_INVALID_BUFFER_SIZE;
    payload = OutputPayload(&packet);

    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (Context->OutputCount == N4PRO_RING_CAPACITY) {
        Context->OutputHead = (Context->OutputHead + 1) % N4PRO_RING_CAPACITY;
        Context->OutputCount--;
    }
    tail = (Context->OutputHead + Context->OutputCount) % N4PRO_RING_CAPACITY;
    RtlCopyMemory(Context->OutputRing[tail], payload, N4PRO_OUTPUT_REPORT_SIZE);
    Context->OutputCount++;
    Context->OutputSequence++;
    ProcessProtocolLocked(Context, payload);
    WdfWaitLockRelease(Context->RingLock);
    CompletePendingRead(Context);

    WdfRequestSetInformation(Request, packet.reportBufferLen);
    return STATUS_SUCCESS;
}

static NTSTATUS
InjectInput(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    UCHAR* report;
    size_t length;
    WDFREQUEST readRequest = NULL;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(
        Request, N4PRO_INPUT_REPORT_SIZE, (PVOID*)&report, &length);
    if (!NT_SUCCESS(status)) return status;
    if (length != N4PRO_INPUT_REPORT_SIZE)
        return STATUS_INVALID_BUFFER_SIZE;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    status = WdfIoQueueRetrieveNextRequest(Context->ReadQueue, &readRequest);
    if (!NT_SUCCESS(status)) {
        EnqueueInputLocked(Context, report);
    }
    WdfWaitLockRelease(Context->RingLock);

    if (readRequest != NULL) {
        status = RequestCopyFromBuffer(readRequest, report, N4PRO_INPUT_REPORT_SIZE);
        WdfRequestComplete(readRequest, status);
    } else {
        status = STATUS_SUCCESS;
    }
    WdfRequestSetInformation(Request, N4PRO_INPUT_REPORT_SIZE);
    return status;
}

static ULONG
ReadBigEndian32(const UCHAR* Value)
{
    return ((ULONG)Value[0] << 24) | ((ULONG)Value[1] << 16)
         | ((ULONG)Value[2] << 8) | (ULONG)Value[3];
}

static VOID
EnqueueInputLocked(PDEVICE_CONTEXT Context, const UCHAR* Report)
{
    ULONG tail;
    if (Context->InputCount == N4PRO_RING_CAPACITY) {
        Context->InputHead = (Context->InputHead + 1) % N4PRO_RING_CAPACITY;
        Context->InputCount--;
    }
    tail = (Context->InputHead + Context->InputCount) % N4PRO_RING_CAPACITY;
    RtlCopyMemory(Context->InputRing[tail], Report, N4PRO_INPUT_REPORT_SIZE);
    Context->InputCount++;
}

static VOID
CompletePendingRead(PDEVICE_CONTEXT Context)
{
    WDFREQUEST request = NULL;
    UCHAR report[N4PRO_INPUT_REPORT_SIZE];
    NTSTATUS status;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (Context->InputCount != 0) {
        status = WdfIoQueueRetrieveNextRequest(Context->ReadQueue, &request);
        if (NT_SUCCESS(status)) {
            RtlCopyMemory(report, Context->InputRing[Context->InputHead], sizeof(report));
            Context->InputHead = (Context->InputHead + 1) % N4PRO_RING_CAPACITY;
            Context->InputCount--;
        }
    }
    WdfWaitLockRelease(Context->RingLock);

    if (request != NULL) {
        status = RequestCopyFromBuffer(request, report, sizeof(report));
        WdfRequestComplete(request, status);
    }
}

static VOID
ProcessProtocolLocked(PDEVICE_CONTEXT Context, const UCHAR* Payload)
{
    static const UCHAR commandPrefix[] = { 'C', 'R', 'T', 0, 0 };
    UCHAR ack[N4PRO_INPUT_REPORT_SIZE];

    if (Context->TransferBytesRemaining != 0) {
        if (Context->TransferBytesRemaining > N4PRO_OUTPUT_REPORT_SIZE)
            Context->TransferBytesRemaining -= N4PRO_OUTPUT_REPORT_SIZE;
        else
            Context->TransferBytesRemaining = 0;
    } else if (RtlCompareMemory(Payload, commandPrefix, sizeof(commandPrefix)) == sizeof(commandPrefix)) {
        if (RtlCompareMemory(Payload + 5, "BAT", 3) == 3
            || RtlCompareMemory(Payload + 5, "LOG", 3) == 3)
            Context->TransferBytesRemaining = ReadBigEndian32(Payload + 8);
        else if (RtlCompareMemory(Payload + 5, "BGPIC", 5) == 5)
            Context->TransferBytesRemaining = ReadBigEndian32(Payload + 10);
    }

    if (Context->TransferBytesRemaining == 0) {
        RtlZeroMemory(ack, sizeof(ack));
        ack[0] = 'A'; ack[1] = 'C'; ack[2] = 'K';
        ack[5] = 'O'; ack[6] = 'K'; ack[9] = 0xFF;
        EnqueueInputLocked(Context, ack);
    }
}

static NTSTATUS
GetCapturedOutput(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    UCHAR* output;
    size_t length;
    NTSTATUS status = WdfRequestRetrieveOutputBuffer(
        Request, N4PRO_OUTPUT_REPORT_SIZE, (PVOID*)&output, &length);
    if (!NT_SUCCESS(status)) return status;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (Context->OutputCount != 0) {
        RtlCopyMemory(output, Context->OutputRing[Context->OutputHead], N4PRO_OUTPUT_REPORT_SIZE);
        Context->OutputHead = (Context->OutputHead + 1) % N4PRO_RING_CAPACITY;
        Context->OutputCount--;
        WdfRequestSetInformation(Request, N4PRO_OUTPUT_REPORT_SIZE);
    } else {
        WdfRequestSetInformation(Request, 0);
    }
    WdfWaitLockRelease(Context->RingLock);
    return STATUS_SUCCESS;
}

static NTSTATUS
GetInputReport(WDFREQUEST Request)
{
    HID_XFER_PACKET packet;
    UCHAR report[N4PRO_INPUT_REPORT_SIZE];
    NTSTATUS status = RequestGetHidXferPacketToRead(Request, &packet);
    if (!NT_SUCCESS(status)) return status;
    if (packet.reportId != 0 || packet.reportBufferLen < sizeof(report))
        return STATUS_INVALID_BUFFER_SIZE;
    RtlZeroMemory(report, sizeof(report));
    RtlCopyMemory(report, FirmwareVersion, sizeof(FirmwareVersion));
    return RequestCopyFromBuffer(Request, report, sizeof(report));
}

static NTSTATUS
GetStringId(WDFREQUEST Request, ULONG* StringId)
{
    WDFMEMORY memory;
    size_t length;
    ULONG* value;
    NTSTATUS status = WdfRequestRetrieveInputMemory(Request, &memory);
    if (!NT_SUCCESS(status)) return status;
    value = (ULONG*)WdfMemoryGetBuffer(memory, &length);
    if (length < sizeof(ULONG)) return STATUS_INVALID_BUFFER_SIZE;
    *StringId = *value & 0xFFFF;
    return STATUS_SUCCESS;
}

static NTSTATUS
GetString(WDFREQUEST Request)
{
    ULONG id;
    NTSTATUS status = GetStringId(Request, &id);
    if (!NT_SUCCESS(status)) return status;
    switch (id) {
    case HID_STRING_ID_IMANUFACTURER:
        return RequestCopyFromBuffer(Request, ManufacturerString, sizeof(ManufacturerString));
    case HID_STRING_ID_IPRODUCT:
        return RequestCopyFromBuffer(Request, ProductString, sizeof(ProductString));
    case HID_STRING_ID_ISERIALNUMBER:
        return RequestCopyFromBuffer(Request, SerialString, sizeof(SerialString));
    default:
        return STATUS_INVALID_PARAMETER;
    }
}
