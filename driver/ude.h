/*
 * MirageDeck USB Device Emulation (UDE) driver.
 * Copyright (c) 2026 Nikita Rybakov. MIT License.
 */
#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <usb.h>
#include <wdfusb.h>
#include <usbdlib.h>
#include <initguid.h>
#include <usbiodef.h>
#include <usbioctl.h>
#include <hidport.h>
#include <ude/1.0/UdeCx.h>
#include <ntstrsafe.h>
#include <wdmsec.h>

#include "common.h"

#define STREAMDECK_CONTROL_ENDPOINT 0x00u
#define STREAMDECK_INPUT_ENDPOINT   0x81u
#define STREAMDECK_OUTPUT_ENDPOINT  0x01u

typedef struct _DEVICE_CONTEXT {
    WDFDEVICE Device;
    UDECXUSBDEVICE UsbDevice;
    UDECXUSBENDPOINT ControlEndpoint;
    UDECXUSBENDPOINT InputEndpoint;
    UDECXUSBENDPOINT OutputEndpoint;
    WDFQUEUE DefaultQueue;
    WDFQUEUE PendingInputQueue;
    WDFSPINLOCK StateLock;

    UCHAR InputRing[STREAMDECK_RING_CAPACITY][STREAMDECK_INPUT_REPORT_SIZE];
    ULONG InputHead;
    ULONG InputCount;

    MIRAGE_CAPTURED_REPORT CaptureRing[STREAMDECK_RING_CAPACITY];
    ULONG CaptureHead;
    ULONG CaptureCount;
    MIRAGE_CAPTURED_REPORT ActiveCapture;
    UCHAR ActiveCaptureTransaction;
    UCHAR ActiveCaptureChunk;
    BOOLEAN ActiveCaptureValid;

    UCHAR InputAssembly[STREAMDECK_INPUT_REPORT_SIZE];
    UCHAR InputAssemblyTransaction;
    UCHAR InputAssemblyNextChunk;
    BOOLEAN InputAssemblyActive;
    LONG SleepDurationSeconds;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext);

typedef struct _USB_DEVICE_CONTEXT {
    PDEVICE_CONTEXT Controller;
} USB_DEVICE_CONTEXT, *PUSB_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(USB_DEVICE_CONTEXT, GetUsbDeviceContext);

typedef struct _ENDPOINT_CONTEXT {
    PDEVICE_CONTEXT Controller;
    UCHAR Address;
} ENDPOINT_CONTEXT, *PENDPOINT_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(ENDPOINT_CONTEXT, GetEndpointContext);

typedef struct _ENDPOINT_QUEUE_CONTEXT {
    PDEVICE_CONTEXT Controller;
    UCHAR Address;
} ENDPOINT_QUEUE_CONTEXT, *PENDPOINT_QUEUE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(ENDPOINT_QUEUE_CONTEXT, GetEndpointQueueContext);

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtControllerIoControl;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL EvtControlUrb;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL EvtInputUrb;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL EvtOutputUrb;
EVT_WDF_IO_QUEUE_IO_CANCELED_ON_QUEUE EvtPendingInputCanceled;
EVT_WDF_IO_QUEUE_STATE EvtPendingInputPurgeComplete;
EVT_UDECX_WDF_DEVICE_QUERY_USB_CAPABILITY EvtQueryUsbCapability;
EVT_UDECX_USB_ENDPOINT_RESET EvtEndpointReset;
EVT_UDECX_USB_ENDPOINT_START EvtEndpointStart;
EVT_UDECX_USB_ENDPOINT_PURGE EvtEndpointPurge;
