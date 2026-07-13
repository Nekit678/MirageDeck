/*++

Copyright (C) Microsoft Corporation, All Rights Reserved.

Portions of this file are derived from the Microsoft vhidmini2 sample
and are licensed under the Microsoft Public License (MS-PL).
See LICENSE-MS-PL.

Modifications Copyright (c) 2026 Nikita Rybakov.

Module Name:
    vhidmini.h

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
    UCHAR InputRing[N4PRO_RING_CAPACITY][N4PRO_INPUT_REPORT_SIZE];
    ULONG InputHead;
    ULONG InputCount;
    UCHAR OutputRing[N4PRO_RING_CAPACITY][N4PRO_OUTPUT_REPORT_SIZE];
    ULONG OutputHead;
    ULONG OutputCount;
    ULONG OutputSequence;
    ULONG TransferBytesRemaining;
    USHORT TouchReleaseX;
    ULONG LastWriteStage;
    NTSTATUS LastWriteStatus;
    ULONG LastWriteInputLength;
    ULONG LastWriteOutputLength;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext);

NTSTATUS RequestCopyFromBuffer(WDFREQUEST Request, const VOID* Buffer, size_t Length);
NTSTATUS RequestGetHidXferPacketToRead(WDFREQUEST Request, HID_XFER_PACKET* Packet);
