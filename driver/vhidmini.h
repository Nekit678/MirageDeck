/*++

Copyright (C) Microsoft Corporation, All Rights Reserved.

Portions of this file are derived from the Microsoft vhidmini2 sample
and are licensed under the Microsoft Public License (MS-PL).
See LICENSE-MS-PL.

Modifications Copyright (c) 2026 Nikita Rybakov.

--*/
#pragma once

#include <windows.h>
#include <wdf.h>
#include <hidport.h>
#include "common.h"

typedef UCHAR HID_REPORT_DESCRIPTOR, *PHID_REPORT_DESCRIPTOR;

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;

typedef struct _DEVICE_CONTEXT {
    WDFDEVICE Device;
    WDFQUEUE DefaultQueue;
    WDFQUEUE ReadQueue;
    WDFWAITLOCK RingLock;
    HID_DEVICE_ATTRIBUTES HidAttributes;
    HID_DESCRIPTOR HidDescriptor;
    PHID_REPORT_DESCRIPTOR ReportDescriptor;
    UCHAR InputRing[STREAMDECK_RING_CAPACITY][STREAMDECK_INPUT_REPORT_SIZE];
    ULONG InputHead;
    ULONG InputCount;
    MIRAGE_CAPTURED_REPORT CaptureRing[STREAMDECK_RING_CAPACITY];
    ULONG CaptureHead;
    ULONG CaptureCount;
    LONG SleepDurationSeconds;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext);

NTSTATUS RequestCopyFromBuffer(WDFREQUEST Request, const VOID* Buffer, size_t Length);
NTSTATUS RequestGetHidXferPacketToRead(WDFREQUEST Request, HID_XFER_PACKET* Packet);
NTSTATUS RequestGetHidXferPacketToWrite(WDFREQUEST Request, HID_XFER_PACKET* Packet);
