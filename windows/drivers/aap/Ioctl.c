/*++
    Ioctl.c - user-mode bridge. Translates DeviceIoControl calls from the
    NTPods app into L2CAP operations.
--*/

#include "NTPodsAAP.h"

//
// The app closed its handle (clean exit OR crash -> the OS closes it for us).
// Release the L2CAP channel so it doesn't leak. LpDisconnect is idempotent and
// runs at PASSIVE_LEVEL, which is where EvtFileClose is called.
//
VOID
LpEvtFileClose(
    _In_ WDFFILEOBJECT FileObject
)
{
    PDEVICE_CONTEXT ctx = DeviceGetContext(WdfFileObjectGetDevice(FileObject));

    if (ctx->State != LpDisconnected || ctx->ChannelHandle != NULL) {
        KdPrint(("NTPodsAAP: app handle closed -> releasing L2CAP channel\n"));
        LpDisconnect(ctx);
    }
}

VOID
LpEvtIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
)
{
    NTSTATUS        status      = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR       information = 0;
    WDFDEVICE       device      = WdfIoQueueGetDevice(Queue);
    PDEVICE_CONTEXT ctx         = DeviceGetContext(device);
    PVOID           inBuf, outBuf;
    size_t          sz;

    // Preserve the published FILE_ANY_ACCESS wire values, but dynamically
    // require the actual operation's access on the caller's open handle.
    switch (IoControlCode) {
    case IOCTL_LP_CONNECT:
    case IOCTL_LP_DISCONNECT:
    case IOCTL_LP_SEND:
        status = IoValidateDeviceIoControlAccess(WdfRequestWdmGetIrp(Request), FILE_WRITE_ACCESS);
        break;
    case IOCTL_LP_RECEIVE:
    case IOCTL_LP_GET_STATUS:
        status = IoValidateDeviceIoControlAccess(WdfRequestWdmGetIrp(Request), FILE_READ_ACCESS);
        break;
    case IOCTL_LP_ATT_SEND:
    case IOCTL_LP_ATT_RECEIVE:
        // This personal-release profile exposes AAP only. Keep the legacy
        // numbers reserved; hearing-aid/ATT operations are not reachable.
        status = STATUS_NOT_SUPPORTED;
        break;
    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }
    if (!NT_SUCCESS(status)) {
        WdfRequestCompleteWithInformation(Request, status, 0);
        return;
    }

    switch (IoControlCode) {

    case IOCTL_LP_CONNECT: {
        PLP_CONNECT_INPUT  in;
        PLP_CONNECT_OUTPUT out;

        if (InputBufferLength < sizeof(LP_CONNECT_INPUT) ||
            OutputBufferLength < sizeof(LP_CONNECT_OUTPUT)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        if (InputBufferLength != sizeof(LP_CONNECT_INPUT)) {
            status = STATUS_INVALID_PARAMETER;
            break;
        }
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(LP_CONNECT_INPUT), &inBuf, &sz);
        if (!NT_SUCCESS(status)) break;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(LP_CONNECT_OUTPUT), &outBuf, &sz);
        if (!NT_SUCCESS(status)) break;

        in  = (PLP_CONNECT_INPUT)inBuf;
        out = (PLP_CONNECT_OUTPUT)outBuf;

        status       = LpConnect(ctx, in->BluetoothAddress, in->Psm);
        out->Status  = (LONG)status;
        out->Success = NT_SUCCESS(status) ? 1u : 0u;
        information  = sizeof(LP_CONNECT_OUTPUT);
        status       = STATUS_SUCCESS; // the connect result is inside the struct
        break;
    }

    case IOCTL_LP_DISCONNECT:
        if (InputBufferLength != 0 || OutputBufferLength != 0) {
            status = STATUS_INVALID_PARAMETER;
            break;
        }
        status = LpDisconnect(ctx);
        break;

    case IOCTL_LP_SEND: {
        if (InputBufferLength == 0 || InputBufferLength > LP_MAX_TRANSFER_BYTES ||
            OutputBufferLength != 0) {
            status = STATUS_INVALID_PARAMETER;
            break;
        }
        status = WdfRequestRetrieveInputBuffer(Request, 1, &inBuf, &sz);
        if (!NT_SUCCESS(status)) break;
        status = LpSend(ctx, inBuf, (ULONG)sz);
        break;
    }

    case IOCTL_LP_RECEIVE: {
        ULONG timeoutMs = 0;
        ULONG bytesRead = 0;

        if (InputBufferLength != 0 && InputBufferLength != sizeof(LP_RECEIVE_INPUT)) {
            status = STATUS_INVALID_PARAMETER;
            break;
        }
        if (InputBufferLength == sizeof(LP_RECEIVE_INPUT)) {
            status = WdfRequestRetrieveInputBuffer(Request, sizeof(LP_RECEIVE_INPUT), &inBuf, &sz);
            if (!NT_SUCCESS(status)) break;
            timeoutMs = ((PLP_RECEIVE_INPUT)inBuf)->TimeoutMs;
        }
        if (timeoutMs > LP_MAX_RECV_TIMEOUT_MS || OutputBufferLength > LP_MAX_TRANSFER_BYTES) {
            status = STATUS_INVALID_PARAMETER;
            break;
        }
        if (OutputBufferLength == 0) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        status = WdfRequestRetrieveOutputBuffer(Request, 1, &outBuf, &sz);
        if (!NT_SUCCESS(status)) break;

        status = LpReceive(ctx, outBuf, (ULONG)OutputBufferLength, &bytesRead,
                           timeoutMs ? timeoutMs : LP_RECV_TIMEOUT_MS);
        if (NT_SUCCESS(status)) {
            if (bytesRead > OutputBufferLength) status = STATUS_DATA_ERROR;
            else information = bytesRead;
        }
        break;
    }

    case IOCTL_LP_GET_STATUS: {
        PLP_STATUS_OUTPUT out;
        if (InputBufferLength != 0) {
            status = STATUS_INVALID_PARAMETER;
            break;
        }
        if (OutputBufferLength < sizeof(LP_STATUS_OUTPUT)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(LP_STATUS_OUTPUT), &outBuf, &sz);
        if (!NT_SUCCESS(status)) break;

        out                      = (PLP_STATUS_OUTPUT)outBuf;
        WdfSpinLockAcquire(ctx->Lock);
        out->State               = (ULONG)ctx->State;
        out->ConnectedAddress    = ctx->State == LpConnected ? ctx->RemoteAddress : 0;
        // Retain the 32-byte status ABI; this release has no ATT surface.
        out->AttServerRegistered = 0;
        out->AttIndicationCount  = 0;
        out->AttAcceptStatus     = STATUS_NOT_SUPPORTED;
        out->AttChannelOpen      = 0;
        out->AttRegisterStatus   = STATUS_NOT_SUPPORTED;
        WdfSpinLockRelease(ctx->Lock);
        information              = sizeof(LP_STATUS_OUTPUT);
        break;
    }

    default:
        break;
    }

    WdfRequestCompleteWithInformation(Request, status, information);
}
