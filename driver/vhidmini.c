/*++

Copyright (C) Microsoft Corporation, All Rights Reserved.

Portions of this file are derived from the Microsoft vhidmini2 sample
and are licensed under the Microsoft Public License (MS-PL).
See LICENSE-MS-PL.

Modifications Copyright (c) 2026 Nikita Rybakov.

Purpose:
    Virtual HID minidriver implementing an Elgato Stream Deck + profile.

--*/
#include "vhidmini.h"

#define STREAMDECK_VID     0x0FD9
#define STREAMDECK_PID     0x0084
#define STREAMDECK_VERSION 0x0200

static const GUID G_PanelInterfaceGuid =
    { 0x2f5e3a9c, 0x54a4, 0x4a18, { 0xa7, 0x3d, 0x61, 0xf4, 0x0e, 0xb0, 0xd9, 0x2b } };
static const WCHAR ManufacturerString[] = L"Elgato";
static const WCHAR ProductString[] = L"Stream Deck +";
static const WCHAR SerialString[] = L"AL00J2A00001";
static const UCHAR FirmwareLd[8] = { '2', '.', '0', '.', '0', '.', '0', '1' };
static const UCHAR FirmwareAp2[8] = { '2', '.', '0', '.', '3', '.', '2', '0' };
static const UCHAR FirmwareAp1[8] = { '2', '.', '0', '.', '0', '.', '0', '1' };

/* Stream Deck + uses numbered reports. Counts exclude the report-ID byte:
 * input is 512 bytes total, output 1024, and every feature report 32. */
static HID_REPORT_DESCRIPTOR G_ReportDescriptor[] = {
    0x06, 0x00, 0xFF,       /* Usage Page (vendor defined) */
    0x09, 0x01,
    0xA1, 0x01,
    0x15, 0x00,
    0x26, 0xFF, 0x00,
    0x75, 0x08,

    0x85, 0x01,             /* Input report ID 1, 511-byte payload */
    0x09, 0x01,
    0x96, 0xFF, 0x01,
    0x81, 0x02,

    0x85, 0x02,             /* Output report ID 2, 1023-byte payload */
    0x09, 0x02,
    0x96, 0xFF, 0x03,
    0x91, 0x02,

    0x85, 0x03, 0x09, 0x03, 0x95, 0x1F, 0xB1, 0x02,
    0x85, 0x04, 0x09, 0x03, 0x95, 0x1F, 0xB1, 0x02,
    0x85, 0x05, 0x09, 0x03, 0x95, 0x1F, 0xB1, 0x02,
    0x85, 0x06, 0x09, 0x03, 0x95, 0x1F, 0xB1, 0x02,
    0x85, 0x07, 0x09, 0x03, 0x95, 0x1F, 0xB1, 0x02,
    0x85, 0x08, 0x09, 0x03, 0x95, 0x1F, 0xB1, 0x02,
    0x85, 0x0A, 0x09, 0x03, 0x95, 0x1F, 0xB1, 0x02,
    0xC0
};

static HID_DESCRIPTOR G_HidDescriptor = {
    0x09, 0x21, 0x0200, 0x00, 0x01,
    { { 0x22, sizeof(G_ReportDescriptor) } }
};

static NTSTATUS CreateQueues(WDFDEVICE Device);
static NTSTATUS ReadReport(PDEVICE_CONTEXT Context, WDFREQUEST Request, BOOLEAN* Complete);
static NTSTATUS CaptureOutput(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS SetFeature(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS GetFeature(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS GetInputReport(WDFREQUEST Request);
static NTSTATUS GetString(WDFREQUEST Request);
static NTSTATUS InjectInputRequest(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static NTSTATUS GetCaptureRequest(PDEVICE_CONTEXT Context, WDFREQUEST Request);
static VOID EnqueueInputLocked(PDEVICE_CONTEXT Context, const UCHAR* Report);
static VOID EnqueueCaptureLocked(PDEVICE_CONTEXT Context, UCHAR Kind, const UCHAR* Report, USHORT Length);
static NTSTATUS CopyInputReport(WDFREQUEST Request, const UCHAR* Report);
static NTSTATUS SubmitInput(PDEVICE_CONTEXT Context, const UCHAR* Report);
static VOID WriteLittleEndian16(UCHAR* Destination, USHORT Value);
static VOID WriteLittleEndian32(UCHAR* Destination, ULONG Value);

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
    context->HidAttributes.VendorID = STREAMDECK_VID;
    context->HidAttributes.ProductID = STREAMDECK_PID;
    context->HidAttributes.VersionNumber = STREAMDECK_VERSION;

    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = device;
    status = WdfWaitLockCreate(&lockAttributes, &context->RingLock);
    if (!NT_SUCCESS(status)) return status;
    status = WdfDeviceCreateDeviceInterface(device, &G_PanelInterfaceGuid, NULL);
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
    case IOCTL_UMDF_HID_SET_FEATURE:
        status = SetFeature(context, Request);
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
    case IOCTL_MIRAGE_INJECT_INPUT:
        status = InjectInputRequest(context, Request);
        break;
    case IOCTL_MIRAGE_GET_CAPTURE:
        status = GetCaptureRequest(context, Request);
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
    UCHAR report[STREAMDECK_INPUT_REPORT_SIZE];
    NTSTATUS status;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (Context->InputCount != 0) {
        RtlCopyMemory(report, Context->InputRing[Context->InputHead], sizeof(report));
        Context->InputHead = (Context->InputHead + 1) % STREAMDECK_RING_CAPACITY;
        Context->InputCount--;
        WdfWaitLockRelease(Context->RingLock);
        return CopyInputReport(Request, report);
    }
    status = WdfRequestForwardToIoQueue(Request, Context->ReadQueue);
    WdfWaitLockRelease(Context->RingLock);
    *Complete = !NT_SUCCESS(status);
    return status;
}

static NTSTATUS
CaptureOutput(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    HID_XFER_PACKET packet;
    NTSTATUS status = RequestGetHidXferPacketToWrite(Request, &packet);
    if (!NT_SUCCESS(status)) return status;
    if (packet.reportId != 0x02 || packet.reportBufferLen < STREAMDECK_OUTPUT_REPORT_SIZE
        || packet.reportBuffer[0] != 0x02)
        return STATUS_INVALID_BUFFER_SIZE;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    EnqueueCaptureLocked(Context, MIRAGE_CAPTURE_OUTPUT, packet.reportBuffer, STREAMDECK_OUTPUT_REPORT_SIZE);
    WdfWaitLockRelease(Context->RingLock);
    WdfRequestSetInformation(Request, STREAMDECK_OUTPUT_REPORT_SIZE);
    return STATUS_SUCCESS;
}

static NTSTATUS
SetFeature(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    HID_XFER_PACKET packet;
    UCHAR report[STREAMDECK_FEATURE_REPORT_SIZE];
    NTSTATUS status = RequestGetHidXferPacketToWrite(Request, &packet);
    if (!NT_SUCCESS(status)) return status;
    if (packet.reportId != 0x03 || packet.reportBufferLen < STREAMDECK_FEATURE_REPORT_SIZE
        || packet.reportBuffer[0] != 0x03)
        return STATUS_INVALID_BUFFER_SIZE;

    RtlCopyMemory(report, packet.reportBuffer, sizeof(report));
    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (report[1] == 0x0D)
        Context->SleepDurationSeconds = (LONG)((ULONG)report[2] | ((ULONG)report[3] << 8)
            | ((ULONG)report[4] << 16) | ((ULONG)report[5] << 24));
    EnqueueCaptureLocked(Context, MIRAGE_CAPTURE_FEATURE, report, STREAMDECK_FEATURE_REPORT_SIZE);
    WdfWaitLockRelease(Context->RingLock);
    WdfRequestSetInformation(Request, STREAMDECK_FEATURE_REPORT_SIZE);
    return STATUS_SUCCESS;
}

static NTSTATUS
GetFeature(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    HID_XFER_PACKET packet;
    UCHAR report[STREAMDECK_FEATURE_REPORT_SIZE];
    const UCHAR* firmware = NULL;
    NTSTATUS status = RequestGetHidXferPacketToRead(Request, &packet);
    if (!NT_SUCCESS(status)) return status;
    if (packet.reportBufferLen < STREAMDECK_FEATURE_REPORT_SIZE)
        return STATUS_INVALID_BUFFER_SIZE;

    RtlZeroMemory(report, sizeof(report));
    report[0] = packet.reportId;
    switch (packet.reportId) {
    case 0x04:
        firmware = FirmwareLd;
        break;
    case 0x05:
        firmware = FirmwareAp2;
        break;
    case 0x07:
        firmware = FirmwareAp1;
        break;
    case 0x06:
        report[1] = 12;
        RtlCopyMemory(report + 2, "AL00J2A00001", 12);
        break;
    case 0x08:
        report[1] = 2;                         /* rows */
        report[2] = 4;                         /* columns */
        WriteLittleEndian16(report + 3, 120);  /* key width */
        WriteLittleEndian16(report + 5, 120);  /* key height */
        WriteLittleEndian16(report + 7, 800);  /* LCD width */
        WriteLittleEndian16(report + 9, 480);  /* LCD height */
        report[11] = 24;                       /* image bits per pixel */
        report[12] = 0;
        break;
    case 0x0A:
        report[1] = 4;
        WriteLittleEndian32(report + 2, (ULONG)Context->SleepDurationSeconds);
        break;
    default:
        return STATUS_INVALID_PARAMETER;
    }
    if (firmware != NULL) {
        report[1] = 0x0C;
        RtlCopyMemory(report + 6, firmware, 8);
    }
    return RequestCopyFromBuffer(Request, report, sizeof(report));
}

static NTSTATUS
GetInputReport(WDFREQUEST Request)
{
    HID_XFER_PACKET packet;
    UCHAR report[STREAMDECK_INPUT_REPORT_SIZE];
    NTSTATUS status = RequestGetHidXferPacketToRead(Request, &packet);
    if (!NT_SUCCESS(status)) return status;
    if (packet.reportId != 0x01 || packet.reportBufferLen < STREAMDECK_INPUT_REPORT_SIZE)
        return STATUS_INVALID_BUFFER_SIZE;
    RtlZeroMemory(report, sizeof(report));
    report[0] = 0x01;
    report[1] = 0x00;
    report[2] = 8;
    return RequestCopyFromBuffer(Request, report, sizeof(report));
}

static NTSTATUS
InjectInputRequest(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    WDFMEMORY memory;
    const UCHAR* report;
    size_t length;
    NTSTATUS status = WdfRequestRetrieveInputMemory(Request, &memory);
    if (!NT_SUCCESS(status)) return status;
    report = (const UCHAR*)WdfMemoryGetBuffer(memory, &length);
    if (length != STREAMDECK_INPUT_REPORT_SIZE || report[0] != 0x01)
        return STATUS_INVALID_BUFFER_SIZE;
    status = SubmitInput(Context, report);
    if (NT_SUCCESS(status)) WdfRequestSetInformation(Request, length);
    return status;
}

static NTSTATUS
GetCaptureRequest(PDEVICE_CONTEXT Context, WDFREQUEST Request)
{
    MIRAGE_CAPTURED_REPORT capture;
    RtlZeroMemory(&capture, sizeof(capture));

    WdfWaitLockAcquire(Context->RingLock, NULL);
    if (Context->CaptureCount != 0) {
        RtlCopyMemory(&capture, &Context->CaptureRing[Context->CaptureHead], sizeof(capture));
        Context->CaptureHead = (Context->CaptureHead + 1) % STREAMDECK_RING_CAPACITY;
        Context->CaptureCount--;
    }
    WdfWaitLockRelease(Context->RingLock);
    return RequestCopyFromBuffer(Request, &capture, sizeof(capture));
}

static NTSTATUS
SubmitInput(PDEVICE_CONTEXT Context, const UCHAR* Report)
{
    WDFREQUEST readRequest = NULL;
    NTSTATUS status;

    WdfWaitLockAcquire(Context->RingLock, NULL);
    status = WdfIoQueueRetrieveNextRequest(Context->ReadQueue, &readRequest);
    if (!NT_SUCCESS(status)) EnqueueInputLocked(Context, Report);
    WdfWaitLockRelease(Context->RingLock);

    if (readRequest != NULL) {
        status = CopyInputReport(readRequest, Report);
        WdfRequestComplete(readRequest, status);
        return status;
    }
    return STATUS_SUCCESS;
}

static VOID
EnqueueInputLocked(PDEVICE_CONTEXT Context, const UCHAR* Report)
{
    ULONG tail;
    if (Context->InputCount == STREAMDECK_RING_CAPACITY) {
        Context->InputHead = (Context->InputHead + 1) % STREAMDECK_RING_CAPACITY;
        Context->InputCount--;
    }
    tail = (Context->InputHead + Context->InputCount) % STREAMDECK_RING_CAPACITY;
    RtlCopyMemory(Context->InputRing[tail], Report, STREAMDECK_INPUT_REPORT_SIZE);
    Context->InputCount++;
}

static VOID
EnqueueCaptureLocked(PDEVICE_CONTEXT Context, UCHAR Kind, const UCHAR* Report, USHORT Length)
{
    ULONG tail;
    PMIRAGE_CAPTURED_REPORT capture;
    if (Context->CaptureCount == STREAMDECK_RING_CAPACITY) {
        Context->CaptureHead = (Context->CaptureHead + 1) % STREAMDECK_RING_CAPACITY;
        Context->CaptureCount--;
    }
    tail = (Context->CaptureHead + Context->CaptureCount) % STREAMDECK_RING_CAPACITY;
    capture = &Context->CaptureRing[tail];
    RtlZeroMemory(capture, sizeof(*capture));
    capture->Kind = Kind;
    capture->Length = Length;
    RtlCopyMemory(capture->Data, Report, Length);
    Context->CaptureCount++;
}

static NTSTATUS
CopyInputReport(WDFREQUEST Request, const UCHAR* Report)
{
    return RequestCopyFromBuffer(Request, Report, STREAMDECK_INPUT_REPORT_SIZE);
}

static VOID
WriteLittleEndian16(UCHAR* Destination, USHORT Value)
{
    Destination[0] = (UCHAR)Value;
    Destination[1] = (UCHAR)(Value >> 8);
}

static VOID
WriteLittleEndian32(UCHAR* Destination, ULONG Value)
{
    Destination[0] = (UCHAR)Value;
    Destination[1] = (UCHAR)(Value >> 8);
    Destination[2] = (UCHAR)(Value >> 16);
    Destination[3] = (UCHAR)(Value >> 24);
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
