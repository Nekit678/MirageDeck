/*
 * Virtual Mirabox N4 Pro HID minidriver.
 * The WDF/HID request plumbing is derived from Microsoft's vhidmini2 sample
 * and remains available under the Microsoft Public License (see LICENSE-MS-PL).
 */
#include "vhidmini.h"

#define N4PRO_VID     0x5548
#define N4PRO_PID     0x1021
#define N4PRO_VERSION 0x0002

static const WCHAR ManufacturerString[] = L"HOTSPOTEKUSB";
static const WCHAR ProductString[] = L"HOTSPOTEKUSB HID DEMO";
static const WCHAR SerialString[] = L"123456712345";
static const UCHAR FirmwareVersion[] = "V4.N4 Pro E.02.009";

/* The Input/Output portion is byte-for-byte identical to firmware 02.009.
 * The final unnumbered Feature item is private panel transport. */
static HID_REPORT_DESCRIPTOR G_ReportDescriptor[] = {
    0x06, 0xA0, 0xFF, 0x09, 0x01, 0xA1, 0x01, 0x09, 0x02,
    0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x96, 0x00,
    0x02, 0x81, 0x02, 0x09, 0x03, 0x15, 0x00, 0x26, 0xFF,
    0x00, 0x75, 0x08, 0x96, 0x00, 0x04, 0x91, 0x02,
    0x09, 0x04,
    0x96, (N4PRO_FEATURE_PAYLOAD_SIZE & 0xFF),
          (N4PRO_FEATURE_PAYLOAD_SIZE >> 8),
    0xB1, 0x02,
    0xC0
};
C_ASSERT(sizeof(G_ReportDescriptor) == 43);

static HID_DESCRIPTOR G_HidDescriptor = {
    0x09, 0x21, 0x0200, 0x00, 0x01,
    { { 0x22, sizeof(G_ReportDescriptor) } }
};

static NTSTATUS CreateQueues(WDFDEVICE Device);
static NTSTATUS ReadReport(PDEVICE_CONTEXT Context, WDFREQUEST Request, BOOLEAN* Complete);
static NTSTATUS CaptureOutput(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS GetFeature(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS GetInputReport(WDFREQUEST Request);
static NTSTATUS GetString(WDFREQUEST Request);
static VOID ProcessProtocolLocked(PDEVICE_CONTEXT Context, const UCHAR* Payload);
static VOID EnqueueInputLocked(PDEVICE_CONTEXT Context, const UCHAR* Report);
static VOID CompletePendingRead(PDEVICE_CONTEXT Context);
static NTSTATUS CopyInputReport(WDFREQUEST Request, const UCHAR* Payload);
static NTSTATUS SubmitInput(PDEVICE_CONTEXT Context, const UCHAR* Payload);

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
    case IOCTL_UMDF_HID_GET_FEATURE:
        status = GetFeature(context, Request);
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
    UCHAR payload[N4PRO_INPUT_REPORT_SIZE];
    NTSTATUS status;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (Context->InputCount != 0) {
        RtlCopyMemory(payload, Context->InputRing[Context->InputHead], sizeof(payload));
        Context->InputHead = (Context->InputHead + 1) % N4PRO_RING_CAPACITY;
        Context->InputCount--;
        WdfWaitLockRelease(Context->RingLock);
        return CopyInputReport(Request, payload);
    }
    status = WdfRequestForwardToIoQueue(Request, Context->ReadQueue);
    WdfWaitLockRelease(Context->RingLock);
    *Complete = !NT_SUCCESS(status);
    return status;
}

static NTSTATUS
CaptureOutput(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    static const UCHAR sidePrefix[] = { 'M', 'B', 'V', 'E' };
    WDFMEMORY inputMemory;
    WDFMEMORY outputMemory;
    const UCHAR* report;
    const UCHAR* payload;
    size_t reportLength;
    size_t outputLength;
    ULONG tail;
    NTSTATUS status;

    Context->LastWriteStage = 1;
    Context->LastWriteStatus = STATUS_SUCCESS;
    Context->LastWriteInputLength = 0;
    Context->LastWriteOutputLength = 0;

    /* This ordering matches Microsoft's UMDF vhidmini2 helper. mshidumdf
     * exposes the report buffer as input memory only after the auxiliary
     * output memory for the report ID has been retrieved. */
    status = WdfRequestRetrieveOutputMemory(Request, &outputMemory);
    if (!NT_SUCCESS(status)) {
        Context->LastWriteStage = 2;
        Context->LastWriteStatus = status;
        return status;
    }
    WdfMemoryGetBuffer(outputMemory, &outputLength);
    Context->LastWriteOutputLength = (ULONG)outputLength;

    status = WdfRequestRetrieveInputMemory(Request, &inputMemory);
    if (!NT_SUCCESS(status)) {
        Context->LastWriteStage = 3;
        Context->LastWriteStatus = status;
        return status;
    }
    report = (const UCHAR*)WdfMemoryGetBuffer(inputMemory, &reportLength);
    Context->LastWriteInputLength = (ULONG)reportLength;
    if (reportLength < N4PRO_OUTPUT_REPORT_SIZE) {
        Context->LastWriteStage = 4;
        Context->LastWriteStatus = STATUS_INVALID_BUFFER_SIZE;
        return STATUS_INVALID_BUFFER_SIZE;
    }
    if (reportLength >= N4PRO_HID_OUTPUT_REPORT_SIZE && report[0] == 0)
        payload = report + 1;
    else
        payload = report;

    /* The panel injects input through a normal HID WriteFile report. This
     * avoids SET_FEATURE, which mshidumdf cannot marshal reliably for report
     * ID zero. The MBVE prefix cannot collide with Stream Dock's CRT protocol. */
    if (RtlCompareMemory(payload, sidePrefix, sizeof(sidePrefix)) == sizeof(sidePrefix)
        && payload[4] == N4PRO_SIDE_INJECT_INPUT) {
        status = SubmitInput(Context, payload + 5);
        Context->LastWriteStage = 5;
        Context->LastWriteStatus = status;
        WdfRequestSetInformation(Request, reportLength);
        return status;
    }

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

    Context->LastWriteStage = 6;
    Context->LastWriteStatus = STATUS_SUCCESS;
    WdfRequestSetInformation(Request, reportLength);
    return STATUS_SUCCESS;
}

static NTSTATUS
SubmitInput(PDEVICE_CONTEXT Context, const UCHAR* Payload)
{
    WDFREQUEST readRequest = NULL;
    NTSTATUS status;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    status = WdfIoQueueRetrieveNextRequest(Context->ReadQueue, &readRequest);
    if (!NT_SUCCESS(status)) {
        EnqueueInputLocked(Context, Payload);
    }
    WdfWaitLockRelease(Context->RingLock);

    if (readRequest != NULL) {
        status = CopyInputReport(readRequest, Payload);
        WdfRequestComplete(readRequest, status);
    } else {
        status = STATUS_SUCCESS;
    }
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
    UCHAR payload[N4PRO_INPUT_REPORT_SIZE];
    NTSTATUS status;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (Context->InputCount != 0) {
        status = WdfIoQueueRetrieveNextRequest(Context->ReadQueue, &request);
        if (NT_SUCCESS(status)) {
            RtlCopyMemory(payload, Context->InputRing[Context->InputHead], sizeof(payload));
            Context->InputHead = (Context->InputHead + 1) % N4PRO_RING_CAPACITY;
            Context->InputCount--;
        }
    }
    WdfWaitLockRelease(Context->RingLock);

    if (request != NULL) {
        status = CopyInputReport(request, payload);
        WdfRequestComplete(request, status);
    }
}

static NTSTATUS
CopyInputReport(WDFREQUEST Request, const UCHAR* Payload)
{
    UCHAR report[N4PRO_HID_INPUT_REPORT_SIZE];
    report[0] = 0;
    RtlCopyMemory(report + 1, Payload, N4PRO_INPUT_REPORT_SIZE);
    return RequestCopyFromBuffer(Request, report, sizeof(report));
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
GetFeature(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    HID_XFER_PACKET packet;
    UCHAR report[N4PRO_FEATURE_REPORT_SIZE];
    PN4PRO_SIDE_REPORT side = (PN4PRO_SIDE_REPORT)(report + 1);
    NTSTATUS status = RequestGetHidXferPacketToRead(Request, &packet);
    if (!NT_SUCCESS(status)) return status;
    if (packet.reportId != N4PRO_SIDE_REPORT_ID
        || packet.reportBufferLen < N4PRO_FEATURE_PAYLOAD_SIZE)
        return STATUS_INVALID_BUFFER_SIZE;

    RtlZeroMemory(report, sizeof(report));
    side->Magic = N4PRO_SIDE_MAGIC;
    side->Command = N4PRO_SIDE_NO_PACKET;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    side->Sequence = Context->OutputSequence;
    if (Context->OutputCount != 0) {
        side->Command = N4PRO_SIDE_OUTPUT_PACKET;
        RtlCopyMemory(side->Data, Context->OutputRing[Context->OutputHead], N4PRO_OUTPUT_REPORT_SIZE);
        Context->OutputHead = (Context->OutputHead + 1) % N4PRO_RING_CAPACITY;
        Context->OutputCount--;
    } else {
        ULONG diagnostic[5];
        diagnostic[0] = N4PRO_DIAGNOSTIC_MAGIC;
        diagnostic[1] = Context->LastWriteStage;
        diagnostic[2] = (ULONG)Context->LastWriteStatus;
        diagnostic[3] = Context->LastWriteInputLength;
        diagnostic[4] = Context->LastWriteOutputLength;
        RtlCopyMemory(side->Data, diagnostic, sizeof(diagnostic));
    }
    WdfWaitLockRelease(Context->RingLock);
    if (packet.reportBufferLen >= N4PRO_FEATURE_REPORT_SIZE)
        return RequestCopyFromBuffer(Request, report, sizeof(report));
    return RequestCopyFromBuffer(Request, side, sizeof(*side));
}

static NTSTATUS
GetInputReport(WDFREQUEST Request)
{
    HID_XFER_PACKET packet;
    UCHAR payload[N4PRO_INPUT_REPORT_SIZE];
    NTSTATUS status = RequestGetHidXferPacketToRead(Request, &packet);
    if (!NT_SUCCESS(status)) return status;
    if (packet.reportId != 0 || packet.reportBufferLen < N4PRO_HID_INPUT_REPORT_SIZE)
        return STATUS_INVALID_BUFFER_SIZE;
    RtlZeroMemory(payload, sizeof(payload));
    RtlCopyMemory(payload, FirmwareVersion, sizeof(FirmwareVersion));
    return CopyInputReport(Request, payload);
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
