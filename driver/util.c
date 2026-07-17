/*++

Copyright (C) Microsoft Corporation, All Rights Reserved.

Portions of this file are derived from the Microsoft vhidmini2 sample
and are licensed under the Microsoft Public License (MS-PL).
See LICENSE-MS-PL.

Modifications Copyright (c) 2026 Nikita Rybakov.

--*/
#include "vhidmini.h"

NTSTATUS
RequestCopyFromBuffer(WDFREQUEST Request, const VOID* Buffer, size_t Length)
{
    WDFMEMORY memory;
    size_t capacity;
    NTSTATUS status = WdfRequestRetrieveOutputMemory(Request, &memory);
    if (!NT_SUCCESS(status)) return status;
    WdfMemoryGetBuffer(memory, &capacity);
    if (capacity < Length) return STATUS_INVALID_BUFFER_SIZE;
    status = WdfMemoryCopyFromBuffer(memory, 0, (PVOID)Buffer, Length);
    if (NT_SUCCESS(status)) WdfRequestSetInformation(Request, Length);
    return status;
}

NTSTATUS
RequestGetHidXferPacketToRead(WDFREQUEST Request, HID_XFER_PACKET* Packet)
{
    WDFMEMORY inputMemory;
    WDFMEMORY outputMemory;
    size_t inputLength;
    size_t outputLength;
    NTSTATUS status = WdfRequestRetrieveInputMemory(Request, &inputMemory);
    if (!NT_SUCCESS(status)) return status;
    {
        PUCHAR input = (PUCHAR)WdfMemoryGetBuffer(inputMemory, &inputLength);
        if (inputLength < sizeof(UCHAR)) return STATUS_INVALID_BUFFER_SIZE;
        Packet->reportId = *input;
    }
    status = WdfRequestRetrieveOutputMemory(Request, &outputMemory);
    if (!NT_SUCCESS(status)) return status;
    Packet->reportBuffer = (PUCHAR)WdfMemoryGetBuffer(outputMemory, &outputLength);
    Packet->reportBufferLen = (ULONG)outputLength;
    return STATUS_SUCCESS;
}

NTSTATUS
RequestGetHidXferPacketToWrite(WDFREQUEST Request, HID_XFER_PACKET* Packet)
{
    WDFMEMORY inputMemory;
    WDFMEMORY outputMemory;
    size_t inputLength;
    size_t outputLength;
    NTSTATUS status;

    /* mshidumdf stores the report ID in the auxiliary output-buffer length. */
    status = WdfRequestRetrieveOutputMemory(Request, &outputMemory);
    if (!NT_SUCCESS(status)) return status;
    WdfMemoryGetBuffer(outputMemory, &outputLength);
    Packet->reportId = (UCHAR)outputLength;

    status = WdfRequestRetrieveInputMemory(Request, &inputMemory);
    if (!NT_SUCCESS(status)) return status;
    Packet->reportBuffer = (PUCHAR)WdfMemoryGetBuffer(inputMemory, &inputLength);
    Packet->reportBufferLen = (ULONG)inputLength;
    return STATUS_SUCCESS;
}
