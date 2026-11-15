/* Host-only tests: compile the real IOCTL dispatcher against a tiny WDF shim.
 * No device interface, driver load, Bluetooth call, or kernel API is available.
 */
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#define _NTPODSAAP_H_
#define UNREFERENCED_PARAMETER(x) (void)(x)
#define KdPrint(x) ((void)0)
typedef int32_t NTSTATUS;
typedef uint32_t ULONG;
typedef int32_t LONG;
typedef uint16_t USHORT;
typedef uint64_t ULONGLONG;
typedef uintptr_t ULONG_PTR;
typedef void VOID;
typedef void *PVOID;
typedef uint64_t BTH_ADDR;
typedef struct { ULONG State; ULONGLONG RemoteAddress; ULONG AttServerRegistered;
 ULONG AttIndicationCount; LONG AttAcceptStatus; ULONG AttConnected;
 LONG AttRegisterStatus; void *Lock; void *ChannelHandle; } DEVICE_CONTEXT, *PDEVICE_CONTEXT;
typedef DEVICE_CONTEXT *WDFDEVICE;
typedef DEVICE_CONTEXT *WDFQUEUE;
typedef DEVICE_CONTEXT *WDFFILEOBJECT;
typedef struct { void *input; size_t inLength; void *output; size_t outLength;
 ULONG access; NTSTATUS status; ULONG_PTR information; } TEST_REQUEST, *WDFREQUEST;
typedef TEST_REQUEST *PIRP;
#define STATUS_SUCCESS ((NTSTATUS)0)
#define STATUS_INVALID_DEVICE_REQUEST ((NTSTATUS)-1)
#define STATUS_BUFFER_TOO_SMALL ((NTSTATUS)-2)
#define STATUS_INVALID_PARAMETER ((NTSTATUS)-3)
#define STATUS_ACCESS_DENIED ((NTSTATUS)-4)
#define STATUS_NOT_SUPPORTED ((NTSTATUS)-5)
#define STATUS_INVALID_BUFFER_SIZE ((NTSTATUS)-6)
#define STATUS_DATA_ERROR ((NTSTATUS)-7)
#define NT_SUCCESS(s) ((s) >= 0)
#define FILE_READ_ACCESS 1u
#define FILE_WRITE_ACCESS 2u
#define LpDisconnected 0
#define LpConnected 2
#define IOCTL_LP_CONNECT 0x80002000u
#define IOCTL_LP_DISCONNECT 0x80002004u
#define IOCTL_LP_SEND 0x80002008u
#define IOCTL_LP_RECEIVE 0x8000200cu
#define IOCTL_LP_GET_STATUS 0x80002010u
#define IOCTL_LP_ATT_SEND 0x80002014u
#define IOCTL_LP_ATT_RECEIVE 0x80002018u
#define LP_RECV_TIMEOUT_MS 5000u
#define LP_MAX_TRANSFER_BYTES 8192u
#define LP_MAX_RECV_TIMEOUT_MS 5000u
#pragma pack(push, 1)
typedef struct { ULONGLONG BluetoothAddress; USHORT Psm; } LP_CONNECT_INPUT, *PLP_CONNECT_INPUT;
typedef struct { ULONG Success; LONG Status; } LP_CONNECT_OUTPUT, *PLP_CONNECT_OUTPUT;
typedef struct { ULONG TimeoutMs; } LP_RECEIVE_INPUT, *PLP_RECEIVE_INPUT;
typedef struct { ULONG State; ULONGLONG ConnectedAddress; ULONG AttServerRegistered;
 ULONG AttIndicationCount; LONG AttAcceptStatus; ULONG AttChannelOpen;
 LONG AttRegisterStatus; } LP_STATUS_OUTPUT, *PLP_STATUS_OUTPUT;
#pragma pack(pop)
static unsigned connects, disconnects, sends, receives, attCalls, accessChecks;
static ULONG requiredAccess, receiveTimeout, receiveCapacity;
static ULONG fakeBytesRead = 3;
static WDFDEVICE WdfIoQueueGetDevice(WDFQUEUE q) { return q; }
static WDFDEVICE WdfFileObjectGetDevice(WDFFILEOBJECT f) { return f; }
static PDEVICE_CONTEXT DeviceGetContext(WDFDEVICE d) { return d; }
static PIRP WdfRequestWdmGetIrp(WDFREQUEST r) { return r; }
static NTSTATUS IoValidateDeviceIoControlAccess(PIRP r, ULONG a) {
 ++accessChecks; requiredAccess = a;
 return ((r->access & a) == a) ? STATUS_SUCCESS : STATUS_ACCESS_DENIED;
}
static NTSTATUS WdfRequestRetrieveInputBuffer(WDFREQUEST r, size_t min, PVOID *b, size_t *len) {
 if (r->inLength < min || !r->input) return STATUS_BUFFER_TOO_SMALL;
 *b = r->input; *len = r->inLength; return STATUS_SUCCESS;
}
static NTSTATUS WdfRequestRetrieveOutputBuffer(WDFREQUEST r, size_t min, PVOID *b, size_t *len) {
 if (r->outLength < min || !r->output) return STATUS_BUFFER_TOO_SMALL;
 *b = r->output; *len = r->outLength; return STATUS_SUCCESS;
}
static void WdfRequestCompleteWithInformation(WDFREQUEST r, NTSTATUS s, ULONG_PTR n) {
 r->status = s; r->information = n;
}
static void WdfSpinLockAcquire(void *lock) { (void)lock; }
static void WdfSpinLockRelease(void *lock) { (void)lock; }
static NTSTATUS LpConnect(PDEVICE_CONTEXT c, BTH_ADDR a, USHORT p) {
 (void)c; (void)a; (void)p; ++connects; return STATUS_SUCCESS;
}
static NTSTATUS LpDisconnect(PDEVICE_CONTEXT c) { (void)c; ++disconnects; return STATUS_SUCCESS; }
static NTSTATUS LpSend(PDEVICE_CONTEXT c, PVOID b, ULONG n) {
 (void)c; (void)b; (void)n; ++sends; return STATUS_SUCCESS;
}
static NTSTATUS LpReceive(PDEVICE_CONTEXT c, PVOID b, ULONG n, ULONG *read, ULONG t) {
 (void)c; (void)b; ++receives; receiveCapacity = n; receiveTimeout = t;
 *read = fakeBytesRead; return STATUS_SUCCESS;
}
static NTSTATUS LpAttSend(PDEVICE_CONTEXT c, PVOID b, ULONG n) {
 (void)c; (void)b; (void)n; ++attCalls; return STATUS_SUCCESS;
}
static NTSTATUS LpAttReceive(PDEVICE_CONTEXT c, PVOID b, ULONG n, ULONG *r, ULONG t) {
 (void)c; (void)b; (void)n; (void)t; *r = 0; ++attCalls; return STATUS_SUCCESS;
}
#include "../Ioctl.c"
static unsigned checks, failures;
#define CHECK(expr) do { ++checks; if (!(expr)) { ++failures; \
 fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #expr); } } while (0)
static DEVICE_CONTEXT device;
static unsigned char input[LP_MAX_TRANSFER_BYTES + 1], output[LP_MAX_TRANSFER_BYTES + 1];
static TEST_REQUEST run(ULONG code, size_t inLen, size_t outLen, ULONG access) {
 TEST_REQUEST r = {input, inLen, output, outLen, access, 999, 999};
 LpEvtIoDeviceControl(&device, &r, outLen, inLen, code); return r;
}
int main(void) {
 TEST_REQUEST r;
 unsigned before;
 memset(input, 0, sizeof(input)); memset(output, 0xa5, sizeof(output));
 before = sends; r = run(IOCTL_LP_SEND, 4, 0, 0);
 CHECK(r.status == STATUS_ACCESS_DENIED && sends == before && r.information == 0);
 r = run(IOCTL_LP_SEND, 4, 0, FILE_READ_ACCESS); CHECK(r.status == STATUS_ACCESS_DENIED);
 r = run(IOCTL_LP_RECEIVE, 0, 8, FILE_WRITE_ACCESS); CHECK(r.status == STATUS_ACCESS_DENIED);
 r = run(IOCTL_LP_GET_STATUS, 0, sizeof(LP_STATUS_OUTPUT), 0); CHECK(r.status == STATUS_ACCESS_DENIED);
 r = run(IOCTL_LP_CONNECT, sizeof(LP_CONNECT_INPUT), sizeof(LP_CONNECT_OUTPUT), FILE_READ_ACCESS);
 CHECK(r.status == STATUS_ACCESS_DENIED);
 r = run(IOCTL_LP_DISCONNECT, 0, 0, FILE_READ_ACCESS); CHECK(r.status == STATUS_ACCESS_DENIED);
 r = run(IOCTL_LP_SEND, LP_MAX_TRANSFER_BYTES + 1, 0, 3); CHECK(!NT_SUCCESS(r.status));
 r = run(IOCTL_LP_SEND, 0, 0, 3); CHECK(!NT_SUCCESS(r.status));
 before = sends; r = run(IOCTL_LP_SEND, 4, 1, 3);
 CHECK(!NT_SUCCESS(r.status) && sends == before);
 r = run(IOCTL_LP_SEND, LP_MAX_TRANSFER_BYTES, 0, FILE_WRITE_ACCESS);
 CHECK(r.status == STATUS_SUCCESS && requiredAccess == FILE_WRITE_ACCESS);
 before = connects; r = run(IOCTL_LP_CONNECT, sizeof(LP_CONNECT_INPUT) + 1, sizeof(LP_CONNECT_OUTPUT), 3);
 CHECK(!NT_SUCCESS(r.status) && connects == before);
 before = disconnects; r = run(IOCTL_LP_DISCONNECT, 1, 0, 3);
 CHECK(!NT_SUCCESS(r.status) && disconnects == before);
 before = receives; r = run(IOCTL_LP_RECEIVE, 1, 8, 3);
 CHECK(!NT_SUCCESS(r.status) && receives == before);
 r = run(IOCTL_LP_RECEIVE, 0, 0, 3); CHECK(!NT_SUCCESS(r.status));
 r = run(IOCTL_LP_RECEIVE, 0, LP_MAX_TRANSFER_BYTES + 1, 3); CHECK(!NT_SUCCESS(r.status));
 ((LP_RECEIVE_INPUT *)input)->TimeoutMs = LP_MAX_RECV_TIMEOUT_MS + 1;
 before = receives; r = run(IOCTL_LP_RECEIVE, sizeof(LP_RECEIVE_INPUT), 8, 3);
 CHECK(!NT_SUCCESS(r.status) && receives == before);
 ((LP_RECEIVE_INPUT *)input)->TimeoutMs = 250;
 r = run(IOCTL_LP_RECEIVE, sizeof(LP_RECEIVE_INPUT), 8, FILE_READ_ACCESS);
 CHECK(r.status == STATUS_SUCCESS && r.information == 3 && receiveTimeout == 250 && receiveCapacity == 8);
 r = run(IOCTL_LP_RECEIVE, 0, 8, FILE_READ_ACCESS);
 CHECK(r.status == STATUS_SUCCESS && receiveTimeout == LP_RECV_TIMEOUT_MS);
 fakeBytesRead = 9; r = run(IOCTL_LP_RECEIVE, 0, 8, 3);
 CHECK(!NT_SUCCESS(r.status) && r.information == 0); fakeBytesRead = 3;
 before = attCalls; r = run(IOCTL_LP_ATT_SEND, 4, 0, 3);
 CHECK(r.status == STATUS_NOT_SUPPORTED && attCalls == before);
 r = run(IOCTL_LP_ATT_RECEIVE, 0, 8, 3);
 CHECK(r.status == STATUS_NOT_SUPPORTED && attCalls == before);
 device.State = 2; device.RemoteAddress = 0x123456789abc;
 r = run(IOCTL_LP_GET_STATUS, 0, sizeof(LP_STATUS_OUTPUT), FILE_READ_ACCESS);
 CHECK(r.status == STATUS_SUCCESS && r.information == sizeof(LP_STATUS_OUTPUT));
 CHECK(((LP_STATUS_OUTPUT *)output)->ConnectedAddress == device.RemoteAddress);
 r = run(0xdeadbeef, 0, 0, 3); CHECK(r.status == STATUS_INVALID_DEVICE_REQUEST);
 CHECK(accessChecks > 0);
 printf("IOCTL dispatcher: %u checks, %u failures\n", checks, failures);
 return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
