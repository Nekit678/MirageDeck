/* Derived from Microsoft's vhidmini2 sample, licensed under MS-PL. */
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
