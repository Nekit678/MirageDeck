/*
 * MirageDeck USB Device Emulation (UDE) driver.
 * Copyright (c) 2026 Nikita Rybakov. MIT License.
 *
 * Presents a genuine USB topology to the Windows host-side USB stack and a
 * HID interface compatible with Elgato Stream Deck +. The protocol behavior
 * is implemented locally; no Elgato firmware or binaries are included.
 */
#include "ude.h"

#define STREAMDECK_VID     0x0FD9u
#define STREAMDECK_PID     0x0084u
#define STREAMDECK_VERSION 0x0200u

#define HID_REQUEST_GET_REPORT   0x01u
#define HID_REQUEST_GET_IDLE     0x02u
#define HID_REQUEST_GET_PROTOCOL 0x03u
#define HID_REQUEST_SET_REPORT   0x09u
#define HID_REQUEST_SET_IDLE     0x0Au
#define HID_REQUEST_SET_PROTOCOL 0x0Bu

#define HID_REPORT_TYPE_INPUT   0x01u
#define HID_REPORT_TYPE_OUTPUT  0x02u
#define HID_REPORT_TYPE_FEATURE 0x03u

#define MIRAGE_LANGUAGE_ID 0x0409u
#define MIRAGE_MANUFACTURER_INDEX 1u
#define MIRAGE_PRODUCT_INDEX      2u
#define MIRAGE_SERIAL_INDEX       3u

#define BASE_DEVICE_NAME       L"\\Device\\USBFDO-"
#define BASE_SYMBOLIC_LINK_NAME L"\\DosDevices\\HCD"
#define MAX_SUFFIX_SIZE         (11u * sizeof(WCHAR))
#define USB_HOST_DEVINTERFACE_REF_STRING L"GUID_DEVINTERFACE_USB_HOST_CONTROLLER"

static const UCHAR PanelTransportMagic[4] = { 'M', 'D', 'P', '1' };
static const UCHAR FirmwareLd[8] = { '2', '.', '0', '.', '0', '.', '0', '1' };
static const UCHAR FirmwareAp2[8] = { '2', '.', '0', '.', '3', '.', '2', '0' };
static const UCHAR FirmwareAp1[8] = { '2', '.', '0', '.', '0', '.', '0', '1' };

static const UCHAR G_ReportDescriptor[] = {
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
    0x85, 0x0B, 0x09, 0x03, 0x95, 0x1F, 0xB1, 0x02,
    0xC0
};

static const UCHAR G_HidDescriptor[] = {
    0x09, HID_HID_DESCRIPTOR_TYPE,
    0x00, 0x02,             /* HID 2.00 */
    0x00,
    0x01,
    HID_REPORT_DESCRIPTOR_TYPE,
    (UCHAR)(sizeof(G_ReportDescriptor) & 0xFFu),
    (UCHAR)(sizeof(G_ReportDescriptor) >> 8)
};

static const UCHAR G_UsbDeviceDescriptor[] = {
    0x12, USB_DEVICE_DESCRIPTOR_TYPE,
    0x00, 0x02,             /* USB 2.0 */
    0x00, 0x00, 0x00,
    0x40,                   /* EP0 max packet */
    0xD9, 0x0F,             /* VID 0FD9 */
    0x84, 0x00,             /* PID 0084 */
    0x00, 0x02,             /* device release 2.00 */
    MIRAGE_MANUFACTURER_INDEX,
    MIRAGE_PRODUCT_INDEX,
    MIRAGE_SERIAL_INDEX,
    0x01
};

static const UCHAR G_UsbConfigurationDescriptor[] = {
    0x09, USB_CONFIGURATION_DESCRIPTOR_TYPE,
    0x29, 0x00,             /* total length: 41 */
    0x01,                   /* interfaces */
    0x01,                   /* configuration value */
    0x00,
    0x80,                   /* bus powered */
    0xFA,                   /* 500 mA */

    0x09, USB_INTERFACE_DESCRIPTOR_TYPE,
    0x00, 0x00,
    0x02,                   /* two interrupt endpoints */
    USB_DEVICE_CLASS_HUMAN_INTERFACE,
    0x00, 0x00, 0x00,

    0x09, HID_HID_DESCRIPTOR_TYPE,
    0x00, 0x02,
    0x00,
    0x01,
    HID_REPORT_DESCRIPTOR_TYPE,
    (UCHAR)(sizeof(G_ReportDescriptor) & 0xFFu),
    (UCHAR)(sizeof(G_ReportDescriptor) >> 8),

    0x07, USB_ENDPOINT_DESCRIPTOR_TYPE,
    STREAMDECK_INPUT_ENDPOINT,
    USB_ENDPOINT_TYPE_INTERRUPT,
    0x00, 0x02,             /* 512-byte input report */
    0x04,                   /* 1 ms at high speed */

    0x07, USB_ENDPOINT_DESCRIPTOR_TYPE,
    STREAMDECK_OUTPUT_ENDPOINT,
    USB_ENDPOINT_TYPE_INTERRUPT,
    0x00, 0x04,             /* 1024-byte output report */
    0x04
};

static const UCHAR G_LanguageDescriptor[] = { 0x04, USB_STRING_DESCRIPTOR_TYPE, 0x09, 0x04 };
static const UNICODE_STRING G_ManufacturerString = RTL_CONSTANT_STRING(L"Elgato");
static const UNICODE_STRING G_ProductString = RTL_CONSTANT_STRING(L"Stream Deck +");
static const UNICODE_STRING G_SerialString = RTL_CONSTANT_STRING(L"AL00J2A00001");

C_ASSERT(sizeof(G_UsbDeviceDescriptor) == sizeof(USB_DEVICE_DESCRIPTOR));
C_ASSERT(sizeof(G_UsbConfigurationDescriptor) == 41);
C_ASSERT(sizeof(G_HidDescriptor) == 9);
C_ASSERT(sizeof(G_ReportDescriptor) <= 0xFFFF);

static NTSTATUS CreateControllerDevice(PWDFDEVICE_INIT* DeviceInit, WDFDEVICE* Device);
static NTSTATUS CreateUsbDevice(PDEVICE_CONTEXT Context);
static NTSTATUS CreateEndpoint(PDEVICE_CONTEXT Context, UCHAR Address,
                               PFN_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL Callback,
                               UDECXUSBENDPOINT* Endpoint);
static NTSTATUS CreateQueues(PDEVICE_CONTEXT Context);
static VOID CompleteControlData(WDFREQUEST Request, const VOID* Data, ULONG Length, USHORT RequestedLength);
static VOID CompleteControlStatus(WDFREQUEST Request, NTSTATUS Status);
static VOID StallControlRequest(WDFREQUEST Request);
static NTSTATUS CaptureOutput(PDEVICE_CONTEXT Context, const UCHAR* Report, ULONG Length);
static NTSTATUS SetFeature(PDEVICE_CONTEXT Context, const UCHAR* Report, ULONG Length);
static NTSTATUS GetFeature(PDEVICE_CONTEXT Context, UCHAR ReportId, UCHAR* Report, ULONG Capacity, PULONG Length);
static NTSTATUS GetInputReport(UCHAR ReportId, UCHAR* Report, ULONG Capacity, PULONG Length);
static NTSTATUS SetPanelFeature(PDEVICE_CONTEXT Context, const UCHAR* Report, ULONG Length);
static NTSTATUS GetPanelFeature(PDEVICE_CONTEXT Context, UCHAR* Report, ULONG Capacity, PULONG Length);
static NTSTATUS SubmitInput(PDEVICE_CONTEXT Context, const UCHAR* Report);
static VOID CompleteInputUrb(WDFREQUEST Request, const UCHAR* Report);
static VOID EnqueueInputLocked(PDEVICE_CONTEXT Context, const UCHAR* Report);
static VOID EnqueueCaptureLocked(PDEVICE_CONTEXT Context, UCHAR Kind, const UCHAR* Report, USHORT Length);
static BOOLEAN PanelReportHasMagic(const UCHAR* Report);
static UCHAR GetPanelChunkCount(SIZE_T Length);
static VOID WriteLittleEndian16(UCHAR* Destination, USHORT Value);
static VOID WriteLittleEndian32(UCHAR* Destination, ULONG Value);

NTSTATUS
DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;

    ExInitializeDriverRuntime(DrvRtPoolNxOptIn);
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    config.DriverPoolTag = 'DriM';
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES,
                           &config, WDF_NO_HANDLE);
}

NTSTATUS
EvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    WDFDEVICE device = NULL;
    PDEVICE_CONTEXT context;
    UDECX_WDF_DEVICE_CONFIG controllerConfig;
    WDF_OBJECT_ATTRIBUTES lockAttributes;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Driver);

    status = CreateControllerDevice(&DeviceInit, &device);
    if (!NT_SUCCESS(status)) return status;

    context = GetDeviceContext(device);
    context->Device = device;

    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = device;
    status = WdfSpinLockCreate(&lockAttributes, &context->StateLock);
    if (!NT_SUCCESS(status)) return status;

    UDECX_WDF_DEVICE_CONFIG_INIT(&controllerConfig, EvtQueryUsbCapability);
    controllerConfig.NumberOfUsb20Ports = 1;
    controllerConfig.NumberOfUsb30Ports = 0;
    status = UdecxWdfDeviceAddUsbDeviceEmulation(device, &controllerConfig);
    if (!NT_SUCCESS(status)) return status;

    status = CreateQueues(context);
    if (!NT_SUCCESS(status)) return status;

    return CreateUsbDevice(context);
}

static NTSTATUS
CreateControllerDevice(PWDFDEVICE_INIT* DeviceInit, WDFDEVICE* Device)
{
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_FILEOBJECT_CONFIG fileConfig;
    DECLARE_UNICODE_STRING_SIZE(deviceName, sizeof(BASE_DEVICE_NAME) + MAX_SUFFIX_SIZE);
    DECLARE_UNICODE_STRING_SIZE(symbolicLinkName, sizeof(BASE_SYMBOLIC_LINK_NAME) + MAX_SUFFIX_SIZE);
    UNICODE_STRING referenceString;
    ULONG instanceNumber;
    NTSTATUS status = STATUS_OBJECT_NAME_COLLISION;

    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, WDF_NO_EVENT_CALLBACK,
                               WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK);
    fileConfig.FileObjectClass = WdfFileObjectWdfCannotUseFsContexts;
    WdfDeviceInitSetFileObjectConfig(*DeviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    status = WdfDeviceInitAssignSDDLString(*DeviceInit, &SDDL_DEVOBJ_SYS_ALL_ADM_RWX_WORLD_RW_RES_R);
    if (!NT_SUCCESS(status)) return status;
    status = UdecxInitializeWdfDeviceInit(*DeviceInit);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    for (instanceNumber = 0; instanceNumber < MAXULONG; ++instanceNumber) {
        status = RtlUnicodeStringPrintf(&deviceName, L"%ws%lu", BASE_DEVICE_NAME, instanceNumber);
        if (!NT_SUCCESS(status)) return status;
        status = WdfDeviceInitAssignName(*DeviceInit, &deviceName);
        if (!NT_SUCCESS(status)) return status;
        status = WdfDeviceCreate(DeviceInit, &attributes, Device);
        if (status != STATUS_OBJECT_NAME_COLLISION) break;
    }
    if (!NT_SUCCESS(status)) return status;

    status = RtlUnicodeStringPrintf(&symbolicLinkName, L"%ws%lu",
                                    BASE_SYMBOLIC_LINK_NAME, instanceNumber);
    if (!NT_SUCCESS(status)) return status;
    status = WdfDeviceCreateSymbolicLink(*Device, &symbolicLinkName);
    if (!NT_SUCCESS(status)) return status;

    RtlInitUnicodeString(&referenceString, USB_HOST_DEVINTERFACE_REF_STRING);
    return WdfDeviceCreateDeviceInterface(*Device,
        (LPGUID)&GUID_DEVINTERFACE_USB_HOST_CONTROLLER, &referenceString);
}

static NTSTATUS
CreateQueues(PDEVICE_CONTEXT Context)
{
    WDF_IO_QUEUE_CONFIG config;
    NTSTATUS status;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&config, WdfIoQueueDispatchSequential);
    config.EvtIoDeviceControl = EvtControllerIoControl;
    config.PowerManaged = WdfFalse;
    status = WdfIoQueueCreate(Context->Device, &config, WDF_NO_OBJECT_ATTRIBUTES,
                              &Context->DefaultQueue);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG_INIT(&config, WdfIoQueueDispatchManual);
    config.EvtIoCanceledOnQueue = EvtPendingInputCanceled;
    config.PowerManaged = WdfFalse;
    return WdfIoQueueCreate(Context->Device, &config, WDF_NO_OBJECT_ATTRIBUTES,
                            &Context->PendingInputQueue);
}

static NTSTATUS
CreateUsbDevice(PDEVICE_CONTEXT Context)
{
    PUDECXUSBDEVICE_INIT init = NULL;
    UDECX_USB_DEVICE_STATE_CHANGE_CALLBACKS callbacks;
    UDECX_USB_DEVICE_PLUG_IN_OPTIONS plugInOptions;
    WDF_OBJECT_ATTRIBUTES attributes;
    PUSB_DEVICE_CONTEXT usbContext;
    UDECXUSBDEVICE usbDevice = NULL;
    NTSTATUS status;

    init = UdecxUsbDeviceInitAllocate(Context->Device);
    if (init == NULL) return STATUS_INSUFFICIENT_RESOURCES;

    UDECX_USB_DEVICE_CALLBACKS_INIT(&callbacks);
    UdecxUsbDeviceInitSetStateChangeCallbacks(init, &callbacks);
    UdecxUsbDeviceInitSetSpeed(init, UdecxUsbHighSpeed);
    UdecxUsbDeviceInitSetEndpointsType(init, UdecxEndpointTypeSimple);

    status = UdecxUsbDeviceInitAddDescriptor(init, (PUCHAR)G_UsbDeviceDescriptor,
                                              sizeof(G_UsbDeviceDescriptor));
    if (!NT_SUCCESS(status)) goto Exit;
    status = UdecxUsbDeviceInitAddDescriptor(init, (PUCHAR)G_UsbConfigurationDescriptor,
                                              sizeof(G_UsbConfigurationDescriptor));
    if (!NT_SUCCESS(status)) goto Exit;
    status = UdecxUsbDeviceInitAddDescriptorWithIndex(init, (PUCHAR)G_LanguageDescriptor,
                                                       sizeof(G_LanguageDescriptor), 0);
    if (!NT_SUCCESS(status)) goto Exit;
    status = UdecxUsbDeviceInitAddStringDescriptor(init, &G_ManufacturerString,
                                                    MIRAGE_MANUFACTURER_INDEX, MIRAGE_LANGUAGE_ID);
    if (!NT_SUCCESS(status)) goto Exit;
    status = UdecxUsbDeviceInitAddStringDescriptor(init, &G_ProductString,
                                                    MIRAGE_PRODUCT_INDEX, MIRAGE_LANGUAGE_ID);
    if (!NT_SUCCESS(status)) goto Exit;
    status = UdecxUsbDeviceInitAddStringDescriptor(init, &G_SerialString,
                                                    MIRAGE_SERIAL_INDEX, MIRAGE_LANGUAGE_ID);
    if (!NT_SUCCESS(status)) goto Exit;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, USB_DEVICE_CONTEXT);
    status = UdecxUsbDeviceCreate(&init, &attributes, &usbDevice);
    if (!NT_SUCCESS(status)) goto Exit;
    usbContext = GetUsbDeviceContext(usbDevice);
    usbContext->Controller = Context;
    Context->UsbDevice = usbDevice;

    status = CreateEndpoint(Context, STREAMDECK_CONTROL_ENDPOINT, EvtControlUrb,
                            &Context->ControlEndpoint);
    if (!NT_SUCCESS(status)) goto Exit;
    status = CreateEndpoint(Context, STREAMDECK_INPUT_ENDPOINT, EvtInputUrb,
                            &Context->InputEndpoint);
    if (!NT_SUCCESS(status)) goto Exit;
    status = CreateEndpoint(Context, STREAMDECK_OUTPUT_ENDPOINT, EvtOutputUrb,
                            &Context->OutputEndpoint);
    if (!NT_SUCCESS(status)) goto Exit;

    UDECX_USB_DEVICE_PLUG_IN_OPTIONS_INIT(&plugInOptions);
    plugInOptions.Usb20PortNumber = 1;
    status = UdecxUsbDevicePlugIn(usbDevice, &plugInOptions);
    if (!NT_SUCCESS(status)) goto Exit;

    Context->UsbDevice = usbDevice;
    usbDevice = NULL;

Exit:
    if (usbDevice != NULL) {
        Context->UsbDevice = NULL;
        WdfObjectDelete(usbDevice);
    }
    if (init != NULL) UdecxUsbDeviceInitFree(init);
    return status;
}

static NTSTATUS
CreateEndpoint(PDEVICE_CONTEXT Context, UCHAR Address,
               PFN_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL Callback,
               UDECXUSBENDPOINT* Endpoint)
{
    PUDECXUSBENDPOINT_INIT init = NULL;
    UDECX_USB_ENDPOINT_CALLBACKS callbacks;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_OBJECT_ATTRIBUTES queueAttributes;
    WDF_OBJECT_ATTRIBUTES endpointAttributes;
    PENDPOINT_QUEUE_CONTEXT queueContext;
    PENDPOINT_CONTEXT endpointContext;
    WDFQUEUE queue = NULL;
    UDECXUSBENDPOINT endpoint = NULL;
    NTSTATUS status;

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoInternalDeviceControl = Callback;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&queueAttributes, ENDPOINT_QUEUE_CONTEXT);
    status = WdfIoQueueCreate(Context->Device, &queueConfig, &queueAttributes, &queue);
    if (!NT_SUCCESS(status)) return status;
    queueContext = GetEndpointQueueContext(queue);
    queueContext->Controller = Context;
    queueContext->Address = Address;

    init = UdecxUsbSimpleEndpointInitAllocate(Context->UsbDevice);
    if (init == NULL) {
        WdfObjectDelete(queue);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    UdecxUsbEndpointInitSetEndpointAddress(init, Address);
    UDECX_USB_ENDPOINT_CALLBACKS_INIT(&callbacks, EvtEndpointReset);
    callbacks.EvtUsbEndpointStart = EvtEndpointStart;
    callbacks.EvtUsbEndpointPurge = EvtEndpointPurge;
    UdecxUsbEndpointInitSetCallbacks(init, &callbacks);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&endpointAttributes, ENDPOINT_CONTEXT);
    status = UdecxUsbEndpointCreate(&init, &endpointAttributes, &endpoint);
    if (!NT_SUCCESS(status)) {
        if (init != NULL) UdecxUsbEndpointInitFree(init);
        WdfObjectDelete(queue);
        return status;
    }
    endpointContext = GetEndpointContext(endpoint);
    endpointContext->Controller = Context;
    endpointContext->Address = Address;
    UdecxUsbEndpointSetWdfIoQueue(endpoint, queue);
    *Endpoint = endpoint;
    return STATUS_SUCCESS;
}

VOID
EvtControllerIoControl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputLength,
                       size_t InputLength, ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(OutputLength);
    UNREFERENCED_PARAMETER(InputLength);
    UNREFERENCED_PARAMETER(IoControlCode);

    if (!UdecxWdfDeviceTryHandleUserIoctl(WdfIoQueueGetDevice(Queue), Request))
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
}

NTSTATUS
EvtQueryUsbCapability(WDFDEVICE Device, PGUID CapabilityType, ULONG OutputBufferLength,
                      PVOID OutputBuffer, PULONG ResultLength)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(CapabilityType);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(OutputBuffer);
    *ResultLength = 0;
    return STATUS_NOT_SUPPORTED;
}

VOID
EvtControlUrb(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputLength,
              size_t InputLength, ULONG IoControlCode)
{
    PDEVICE_CONTEXT context = GetEndpointQueueContext(Queue)->Controller;
    WDF_USB_CONTROL_SETUP_PACKET setup;
    PUCHAR buffer;
    ULONG bufferLength;
    ULONG reportLength;
    UCHAR report[STREAMDECK_INPUT_REPORT_SIZE];
    UCHAR descriptorType;
    UCHAR reportType;
    UCHAR reportId;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(OutputLength);
    UNREFERENCED_PARAMETER(InputLength);

    if (IoControlCode != IOCTL_INTERNAL_USB_SUBMIT_URB) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }
    status = UdecxUrbRetrieveControlSetupPacket(Request, &setup);
    if (!NT_SUCCESS(status)) {
        UdecxUrbCompleteWithNtStatus(Request, status);
        return;
    }

    if (setup.Packet.bm.Request.Type == BMREQUEST_STANDARD
        && setup.Packet.bm.Request.Recipient == BMREQUEST_TO_INTERFACE
        && setup.Packet.bRequest == USB_REQUEST_GET_DESCRIPTOR) {
        descriptorType = setup.Packet.wValue.Bytes.HiByte;
        if (descriptorType == HID_HID_DESCRIPTOR_TYPE) {
            CompleteControlData(Request, G_HidDescriptor, sizeof(G_HidDescriptor),
                                setup.Packet.wLength);
            return;
        }
        if (descriptorType == HID_REPORT_DESCRIPTOR_TYPE) {
            CompleteControlData(Request, G_ReportDescriptor, sizeof(G_ReportDescriptor),
                                setup.Packet.wLength);
            return;
        }
        StallControlRequest(Request);
        return;
    }

    if (setup.Packet.bm.Request.Type != BMREQUEST_CLASS
        || setup.Packet.bm.Request.Recipient != BMREQUEST_TO_INTERFACE) {
        StallControlRequest(Request);
        return;
    }

    reportType = setup.Packet.wValue.Bytes.HiByte;
    reportId = setup.Packet.wValue.Bytes.LowByte;
    switch (setup.Packet.bRequest) {
    case HID_REQUEST_GET_REPORT:
        if (reportType == HID_REPORT_TYPE_FEATURE)
            status = GetFeature(context, reportId, report, sizeof(report), &reportLength);
        else if (reportType == HID_REPORT_TYPE_INPUT)
            status = GetInputReport(reportId, report, sizeof(report), &reportLength);
        else
            status = STATUS_NOT_SUPPORTED;
        if (!NT_SUCCESS(status)) {
            StallControlRequest(Request);
            return;
        }
        CompleteControlData(Request, report, reportLength, setup.Packet.wLength);
        return;

    case HID_REQUEST_SET_REPORT:
        status = UdecxUrbRetrieveBuffer(Request, &buffer, &bufferLength);
        if (NT_SUCCESS(status)) {
            if (reportType == HID_REPORT_TYPE_FEATURE)
                status = SetFeature(context, buffer, bufferLength);
            else if (reportType == HID_REPORT_TYPE_OUTPUT)
                status = CaptureOutput(context, buffer, bufferLength);
            else
                status = STATUS_NOT_SUPPORTED;
        }
        if (!NT_SUCCESS(status)) {
            StallControlRequest(Request);
            return;
        }
        UdecxUrbSetBytesCompleted(Request, bufferLength);
        UdecxUrbCompleteWithNtStatus(Request, STATUS_SUCCESS);
        return;

    case HID_REQUEST_GET_IDLE:
        report[0] = 0;
        CompleteControlData(Request, report, 1, setup.Packet.wLength);
        return;
    case HID_REQUEST_GET_PROTOCOL:
        report[0] = 1;
        CompleteControlData(Request, report, 1, setup.Packet.wLength);
        return;
    case HID_REQUEST_SET_IDLE:
    case HID_REQUEST_SET_PROTOCOL:
        CompleteControlStatus(Request, STATUS_SUCCESS);
        return;
    default:
        StallControlRequest(Request);
        return;
    }
}

VOID
EvtInputUrb(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputLength,
            size_t InputLength, ULONG IoControlCode)
{
    PDEVICE_CONTEXT context = GetEndpointQueueContext(Queue)->Controller;
    UCHAR report[STREAMDECK_INPUT_REPORT_SIZE];
    BOOLEAN haveReport = FALSE;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(OutputLength);
    UNREFERENCED_PARAMETER(InputLength);

    if (IoControlCode != IOCTL_INTERNAL_USB_SUBMIT_URB) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }

    WdfSpinLockAcquire(context->StateLock);
    if (context->InputCount != 0) {
        RtlCopyMemory(report, context->InputRing[context->InputHead], sizeof(report));
        context->InputHead = (context->InputHead + 1) % STREAMDECK_RING_CAPACITY;
        context->InputCount--;
        haveReport = TRUE;
        status = STATUS_SUCCESS;
    } else {
        status = WdfRequestForwardToIoQueue(Request, context->PendingInputQueue);
    }
    WdfSpinLockRelease(context->StateLock);

    if (haveReport) CompleteInputUrb(Request, report);
    else if (!NT_SUCCESS(status)) UdecxUrbCompleteWithNtStatus(Request, status);
}

VOID
EvtOutputUrb(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputLength,
             size_t InputLength, ULONG IoControlCode)
{
    PDEVICE_CONTEXT context = GetEndpointQueueContext(Queue)->Controller;
    PUCHAR buffer = NULL;
    ULONG length = 0;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(OutputLength);
    UNREFERENCED_PARAMETER(InputLength);

    if (IoControlCode != IOCTL_INTERNAL_USB_SUBMIT_URB) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }
    status = UdecxUrbRetrieveBuffer(Request, &buffer, &length);
    if (NT_SUCCESS(status)) status = CaptureOutput(context, buffer, length);
    if (NT_SUCCESS(status)) UdecxUrbSetBytesCompleted(Request, length);
    UdecxUrbCompleteWithNtStatus(Request, status);
}

VOID
EvtPendingInputCanceled(WDFQUEUE Queue, WDFREQUEST Request)
{
    UNREFERENCED_PARAMETER(Queue);
    UdecxUrbCompleteWithNtStatus(Request, STATUS_CANCELLED);
}

VOID
EvtEndpointReset(UDECXUSBENDPOINT Endpoint, WDFREQUEST Request)
{
    UNREFERENCED_PARAMETER(Endpoint);
    WdfRequestComplete(Request, STATUS_SUCCESS);
}

VOID
EvtEndpointStart(UDECXUSBENDPOINT Endpoint)
{
    PENDPOINT_CONTEXT endpointContext = GetEndpointContext(Endpoint);
    if (endpointContext->Address == STREAMDECK_INPUT_ENDPOINT)
        WdfIoQueueStart(endpointContext->Controller->PendingInputQueue);
}

VOID
EvtEndpointPurge(UDECXUSBENDPOINT Endpoint)
{
    PENDPOINT_CONTEXT endpointContext = GetEndpointContext(Endpoint);
    if (endpointContext->Address == STREAMDECK_INPUT_ENDPOINT) {
        WdfIoQueuePurge(endpointContext->Controller->PendingInputQueue,
                        EvtPendingInputPurgeComplete, Endpoint);
    } else {
        UdecxUsbEndpointPurgeComplete(Endpoint);
    }
}

VOID
EvtPendingInputPurgeComplete(WDFQUEUE Queue, WDFCONTEXT Context)
{
    UNREFERENCED_PARAMETER(Queue);
    UdecxUsbEndpointPurgeComplete((UDECXUSBENDPOINT)Context);
}

static VOID
CompleteControlData(WDFREQUEST Request, const VOID* Data, ULONG Length, USHORT RequestedLength)
{
    PUCHAR buffer;
    ULONG capacity;
    ULONG copyLength;
    NTSTATUS status = UdecxUrbRetrieveBuffer(Request, &buffer, &capacity);
    if (!NT_SUCCESS(status)) {
        UdecxUrbCompleteWithNtStatus(Request, status);
        return;
    }
    copyLength = Length;
    if (copyLength > capacity) copyLength = capacity;
    if (copyLength > RequestedLength) copyLength = RequestedLength;
    if (copyLength != 0) RtlCopyMemory(buffer, Data, copyLength);
    UdecxUrbSetBytesCompleted(Request, copyLength);
    UdecxUrbCompleteWithNtStatus(Request, STATUS_SUCCESS);
}

static VOID
CompleteControlStatus(WDFREQUEST Request, NTSTATUS Status)
{
    if (NT_SUCCESS(Status)) UdecxUrbSetBytesCompleted(Request, 0);
    UdecxUrbCompleteWithNtStatus(Request, Status);
}

static VOID
StallControlRequest(WDFREQUEST Request)
{
    UdecxUrbComplete(Request, USBD_STATUS_STALL_PID);
}

static NTSTATUS
CaptureOutput(PDEVICE_CONTEXT Context, const UCHAR* Report, ULONG Length)
{
    if (Length < STREAMDECK_OUTPUT_REPORT_SIZE || Report[0] != 0x02)
        return STATUS_INVALID_BUFFER_SIZE;
    WdfSpinLockAcquire(Context->StateLock);
    EnqueueCaptureLocked(Context, MIRAGE_CAPTURE_OUTPUT, Report, STREAMDECK_OUTPUT_REPORT_SIZE);
    WdfSpinLockRelease(Context->StateLock);
    return STATUS_SUCCESS;
}

static NTSTATUS
SetFeature(PDEVICE_CONTEXT Context, const UCHAR* Report, ULONG Length)
{
    if (Length < STREAMDECK_FEATURE_REPORT_SIZE) return STATUS_INVALID_BUFFER_SIZE;
    if (Report[0] == MIRAGE_PANEL_REPORT_ID)
        return SetPanelFeature(Context, Report, Length);
    if (Report[0] != 0x03) return STATUS_INVALID_PARAMETER;

    WdfSpinLockAcquire(Context->StateLock);
    if (Report[1] == 0x0D)
        Context->SleepDurationSeconds = (LONG)((ULONG)Report[2] | ((ULONG)Report[3] << 8)
            | ((ULONG)Report[4] << 16) | ((ULONG)Report[5] << 24));
    EnqueueCaptureLocked(Context, MIRAGE_CAPTURE_FEATURE, Report, STREAMDECK_FEATURE_REPORT_SIZE);
    WdfSpinLockRelease(Context->StateLock);
    return STATUS_SUCCESS;
}

static NTSTATUS
GetFeature(PDEVICE_CONTEXT Context, UCHAR ReportId, UCHAR* Report, ULONG Capacity, PULONG Length)
{
    const UCHAR* firmware = NULL;
    if (Capacity < STREAMDECK_FEATURE_REPORT_SIZE) return STATUS_BUFFER_TOO_SMALL;
    if (ReportId == MIRAGE_PANEL_REPORT_ID)
        return GetPanelFeature(Context, Report, Capacity, Length);

    RtlZeroMemory(Report, STREAMDECK_FEATURE_REPORT_SIZE);
    Report[0] = ReportId;
    switch (ReportId) {
    case 0x04: firmware = FirmwareLd; break;
    case 0x05: firmware = FirmwareAp2; break;
    case 0x07: firmware = FirmwareAp1; break;
    case 0x06:
        Report[1] = 12;
        RtlCopyMemory(Report + 2, "AL00J2A00001", 12);
        break;
    case 0x08:
        Report[1] = 2;
        Report[2] = 4;
        WriteLittleEndian16(Report + 3, 120);
        WriteLittleEndian16(Report + 5, 120);
        WriteLittleEndian16(Report + 7, 800);
        WriteLittleEndian16(Report + 9, 480);
        Report[11] = 24;
        Report[12] = 0;
        break;
    case 0x0A:
        Report[1] = 4;
        WdfSpinLockAcquire(Context->StateLock);
        WriteLittleEndian32(Report + 2, (ULONG)Context->SleepDurationSeconds);
        WdfSpinLockRelease(Context->StateLock);
        break;
    default:
        return STATUS_INVALID_PARAMETER;
    }
    if (firmware != NULL) {
        Report[1] = 0x0C;
        RtlCopyMemory(Report + 6, firmware, 8);
    }
    *Length = STREAMDECK_FEATURE_REPORT_SIZE;
    return STATUS_SUCCESS;
}

static NTSTATUS
GetInputReport(UCHAR ReportId, UCHAR* Report, ULONG Capacity, PULONG Length)
{
    if (ReportId != 0x01 || Capacity < STREAMDECK_INPUT_REPORT_SIZE)
        return STATUS_INVALID_BUFFER_SIZE;
    RtlZeroMemory(Report, STREAMDECK_INPUT_REPORT_SIZE);
    Report[0] = 0x01;
    Report[1] = 0x00;
    Report[2] = 8;
    *Length = STREAMDECK_INPUT_REPORT_SIZE;
    return STATUS_SUCCESS;
}

static NTSTATUS
SetPanelFeature(PDEVICE_CONTEXT Context, const UCHAR* Report, ULONG Length)
{
    UCHAR completedReport[STREAMDECK_INPUT_REPORT_SIZE];
    UCHAR index;
    UCHAR count;
    UCHAR payloadLength;
    UCHAR expectedCount;
    USHORT totalLength;
    SIZE_T offset;
    SIZE_T expectedLength;
    BOOLEAN completed = FALSE;
    NTSTATUS status = STATUS_SUCCESS;

    if (Length < STREAMDECK_FEATURE_REPORT_SIZE || !PanelReportHasMagic(Report))
        return STATUS_INVALID_PARAMETER;
    if (Report[5] == MIRAGE_PANEL_COMMAND_RESET) {
        WdfSpinLockAcquire(Context->StateLock);
        Context->InputAssemblyActive = FALSE;
        Context->InputAssemblyNextChunk = 0;
        Context->ActiveCaptureValid = FALSE;
        Context->ActiveCaptureChunk = 0;
        Context->CaptureHead = 0;
        Context->CaptureCount = 0;
        WdfSpinLockRelease(Context->StateLock);
        return STATUS_SUCCESS;
    }
    if (Report[5] != MIRAGE_PANEL_COMMAND_INJECT_CHUNK || Report[6] != MIRAGE_CAPTURE_NONE)
        return STATUS_INVALID_PARAMETER;

    index = Report[8];
    count = Report[9];
    payloadLength = Report[10];
    totalLength = (USHORT)((USHORT)Report[11] | ((USHORT)Report[12] << 8));
    expectedCount = GetPanelChunkCount(STREAMDECK_INPUT_REPORT_SIZE);
    if (totalLength != STREAMDECK_INPUT_REPORT_SIZE || count != expectedCount || index >= count)
        return STATUS_INVALID_BUFFER_SIZE;
    offset = (SIZE_T)index * MIRAGE_PANEL_PAYLOAD_SIZE;
    expectedLength = STREAMDECK_INPUT_REPORT_SIZE - offset;
    if (expectedLength > MIRAGE_PANEL_PAYLOAD_SIZE) expectedLength = MIRAGE_PANEL_PAYLOAD_SIZE;
    if (payloadLength != expectedLength) return STATUS_INVALID_BUFFER_SIZE;

    WdfSpinLockAcquire(Context->StateLock);
    if (index == 0) {
        Context->InputAssemblyActive = TRUE;
        Context->InputAssemblyTransaction = Report[7];
        Context->InputAssemblyNextChunk = 0;
    }
    if (!Context->InputAssemblyActive
        || Context->InputAssemblyTransaction != Report[7]
        || Context->InputAssemblyNextChunk != index) {
        status = STATUS_INVALID_DEVICE_STATE;
    } else {
        RtlCopyMemory(Context->InputAssembly + offset,
                      Report + MIRAGE_PANEL_HEADER_SIZE, payloadLength);
        Context->InputAssemblyNextChunk++;
        if (Context->InputAssemblyNextChunk == count) {
            RtlCopyMemory(completedReport, Context->InputAssembly, sizeof(completedReport));
            Context->InputAssemblyActive = FALSE;
            completed = TRUE;
        }
    }
    WdfSpinLockRelease(Context->StateLock);

    if (!NT_SUCCESS(status)) return status;
    if (completed) {
        if (completedReport[0] != 0x01) return STATUS_INVALID_PARAMETER;
        return SubmitInput(Context, completedReport);
    }
    return STATUS_SUCCESS;
}

static NTSTATUS
GetPanelFeature(PDEVICE_CONTEXT Context, UCHAR* Report, ULONG Capacity, PULONG Length)
{
    USHORT totalLength;
    UCHAR count;
    UCHAR index;
    SIZE_T offset;
    SIZE_T payloadLength;

    if (Capacity < STREAMDECK_FEATURE_REPORT_SIZE) return STATUS_BUFFER_TOO_SMALL;
    RtlZeroMemory(Report, STREAMDECK_FEATURE_REPORT_SIZE);
    Report[0] = MIRAGE_PANEL_REPORT_ID;
    RtlCopyMemory(Report + 1, PanelTransportMagic, sizeof(PanelTransportMagic));
    Report[5] = MIRAGE_PANEL_COMMAND_NONE;

    WdfSpinLockAcquire(Context->StateLock);
    if (!Context->ActiveCaptureValid && Context->CaptureCount != 0) {
        RtlCopyMemory(&Context->ActiveCapture,
            &Context->CaptureRing[Context->CaptureHead], sizeof(Context->ActiveCapture));
        Context->CaptureHead = (Context->CaptureHead + 1) % STREAMDECK_RING_CAPACITY;
        Context->CaptureCount--;
        Context->ActiveCaptureChunk = 0;
        Context->ActiveCaptureTransaction++;
        if (Context->ActiveCaptureTransaction == 0) Context->ActiveCaptureTransaction++;
        Context->ActiveCaptureValid = TRUE;
    }
    if (Context->ActiveCaptureValid) {
        totalLength = Context->ActiveCapture.Length;
        count = GetPanelChunkCount(totalLength);
        index = Context->ActiveCaptureChunk;
        offset = (SIZE_T)index * MIRAGE_PANEL_PAYLOAD_SIZE;
        payloadLength = totalLength - offset;
        if (payloadLength > MIRAGE_PANEL_PAYLOAD_SIZE) payloadLength = MIRAGE_PANEL_PAYLOAD_SIZE;

        Report[5] = MIRAGE_PANEL_COMMAND_CAPTURE_CHUNK;
        Report[6] = Context->ActiveCapture.Kind;
        Report[7] = Context->ActiveCaptureTransaction;
        Report[8] = index;
        Report[9] = count;
        Report[10] = (UCHAR)payloadLength;
        WriteLittleEndian16(Report + 11, totalLength);
        RtlCopyMemory(Report + MIRAGE_PANEL_HEADER_SIZE,
                      Context->ActiveCapture.Data + offset, payloadLength);

        Context->ActiveCaptureChunk++;
        if (Context->ActiveCaptureChunk == count) Context->ActiveCaptureValid = FALSE;
    }
    WdfSpinLockRelease(Context->StateLock);
    *Length = STREAMDECK_FEATURE_REPORT_SIZE;
    return STATUS_SUCCESS;
}

static NTSTATUS
SubmitInput(PDEVICE_CONTEXT Context, const UCHAR* Report)
{
    WDFREQUEST request = NULL;
    NTSTATUS status;

    WdfSpinLockAcquire(Context->StateLock);
    status = WdfIoQueueRetrieveNextRequest(Context->PendingInputQueue, &request);
    if (!NT_SUCCESS(status)) {
        EnqueueInputLocked(Context, Report);
        status = STATUS_SUCCESS;
    }
    WdfSpinLockRelease(Context->StateLock);

    if (request != NULL) CompleteInputUrb(request, Report);
    return status;
}

static VOID
CompleteInputUrb(WDFREQUEST Request, const UCHAR* Report)
{
    PUCHAR buffer;
    ULONG length;
    NTSTATUS status = UdecxUrbRetrieveBuffer(Request, &buffer, &length);
    if (NT_SUCCESS(status)) {
        if (length < STREAMDECK_INPUT_REPORT_SIZE) {
            status = STATUS_BUFFER_TOO_SMALL;
        } else {
            RtlCopyMemory(buffer, Report, STREAMDECK_INPUT_REPORT_SIZE);
            UdecxUrbSetBytesCompleted(Request, STREAMDECK_INPUT_REPORT_SIZE);
        }
    }
    UdecxUrbCompleteWithNtStatus(Request, status);
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

static BOOLEAN
PanelReportHasMagic(const UCHAR* Report)
{
    return Report[0] == MIRAGE_PANEL_REPORT_ID
        && Report[1] == PanelTransportMagic[0]
        && Report[2] == PanelTransportMagic[1]
        && Report[3] == PanelTransportMagic[2]
        && Report[4] == PanelTransportMagic[3];
}

static UCHAR
GetPanelChunkCount(SIZE_T Length)
{
    return (UCHAR)((Length + MIRAGE_PANEL_PAYLOAD_SIZE - 1) / MIRAGE_PANEL_PAYLOAD_SIZE);
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
