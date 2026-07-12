#pragma once

#define N4PRO_INPUT_REPORT_SIZE  512u
#define N4PRO_OUTPUT_REPORT_SIZE 1024u
#define N4PRO_RING_CAPACITY      128u

/* Private panel transport. It deliberately lives outside the HID report
 * descriptor, so applications see the exact descriptor of the real N4 Pro. */
#define IOCTL_MIRABOX_INJECT_INPUT \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_MIRABOX_GET_OUTPUT \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
