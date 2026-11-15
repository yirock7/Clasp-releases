/* Host fixture for the production kernel-only BTHENUM identity query. The
 * runner extracts the function unchanged; WDF/IRP transport is simulated. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef int32_t NTSTATUS;
typedef void *PVOID;
typedef void VOID;
typedef uint64_t BTH_ADDR;
#define NT_SUCCESS(s) ((s)>=0)
#define STATUS_SUCCESS ((NTSTATUS)0)
#define STATUS_DEVICE_CONFIGURATION_ERROR ((NTSTATUS)-1)
#define STATUS_UNSUCCESSFUL ((NTSTATUS)-2)
#define WDF_NO_OBJECT_ATTRIBUTES NULL
#define IRP_MJ_INTERNAL_DEVICE_CONTROL 15
#define IOCTL_INTERNAL_BTHENUM_GET_DEVINFO 0x41000bu
#define WDF_REQUEST_SEND_OPTION_SYNCHRONOUS 1
#define WDF_REQUEST_SEND_OPTION_TIMEOUT 2
#define WDF_REL_TIMEOUT_IN_SEC(s) (-(s)*10000000LL)
#define BDIF_ADDRESS 1
#define RtlZeroMemory(p,n) memset(p,0,n)
typedef struct { unsigned flags; BTH_ADDR address; unsigned classOfDevice; char name[248]; } BTH_DEVICE_INFO;
typedef struct { unsigned MajorFunction;
 struct { struct { unsigned IoControlCode; unsigned OutputBufferLength; } DeviceIoControl; } Parameters;
} IO_STACK_LOCATION;
typedef struct { struct { void *SystemBuffer; } AssociatedIrp; } IRP, *PIRP;
typedef struct { IRP Irp; IO_STACK_LOCATION Stack; NTSTATUS Status; size_t Information; } REQUEST, *WDFREQUEST;
typedef struct { unsigned Flags; int64_t Timeout; } WDF_REQUEST_SEND_OPTIONS;
#define WDF_REQUEST_SEND_OPTIONS_INIT(o,f) ((o)->Flags=(f))
#define WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(o,t) ((o)->Timeout=(t))
typedef struct { BTH_ADDR BoundAddress; void *IoTarget; } DEVICE_CONTEXT, *PDEVICE_CONTEXT;
static REQUEST mockRequest;
static BTH_DEVICE_INFO fakeInfo;
static NTSTATUS fakeCreateStatus, fakeSendStatus;
static size_t fakeInformation=sizeof(BTH_DEVICE_INFO);
static unsigned created, sent, deleted, badFormat;
static NTSTATUS WdfRequestCreate(void *attributes, void *target, WDFREQUEST *out) {
 (void)attributes; (void)target; ++created;
 if (!NT_SUCCESS(fakeCreateStatus)) return fakeCreateStatus;
 memset(&mockRequest,0,sizeof(mockRequest)); *out=&mockRequest; return STATUS_SUCCESS;
}
static void WdfRequestWdmFormatUsingStackLocation(WDFREQUEST r, IO_STACK_LOCATION *s) { r->Stack=*s; }
static PIRP WdfRequestWdmGetIrp(WDFREQUEST r) { return &r->Irp; }
static unsigned WdfRequestSend(WDFREQUEST r, void *target, WDF_REQUEST_SEND_OPTIONS *options) {
 (void)target; ++sent;
 if (r->Stack.MajorFunction!=IRP_MJ_INTERNAL_DEVICE_CONTROL ||
     r->Stack.Parameters.DeviceIoControl.IoControlCode!=IOCTL_INTERNAL_BTHENUM_GET_DEVINFO ||
     r->Stack.Parameters.DeviceIoControl.OutputBufferLength!=sizeof(fakeInfo) ||
     !r->Irp.AssociatedIrp.SystemBuffer || options->Flags!=3 ||
     options->Timeout!=WDF_REL_TIMEOUT_IN_SEC(5)) ++badFormat;
 if (NT_SUCCESS(fakeSendStatus)) memcpy(r->Irp.AssociatedIrp.SystemBuffer,&fakeInfo,sizeof(fakeInfo));
 r->Status=fakeSendStatus; r->Information=fakeInformation;
 return NT_SUCCESS(fakeSendStatus);
}
static NTSTATUS WdfRequestGetStatus(WDFREQUEST r) { return r->Status; }
static size_t WdfRequestGetInformation(WDFREQUEST r) { return r->Information; }
static void WdfObjectDelete(WDFREQUEST r) {
 ++deleted; if (r->Irp.AssociatedIrp.SystemBuffer) ++badFormat;
}
#include "bound_query_under_test.h"
static unsigned checks, failures;
#define CHECK(expr) do { ++checks; if (!(expr)) { ++failures; \
 fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #expr); } } while (0)
int main(void) {
 DEVICE_CONTEXT ctx={0};
 const BTH_ADDR bound=0x123456789abc;
 fakeInfo.flags=BDIF_ADDRESS; fakeInfo.address=bound;
 CHECK(LpQueryBoundAddress(&ctx)==STATUS_SUCCESS && ctx.BoundAddress==bound);
 CHECK(badFormat==0 && created==1 && sent==1 && deleted==1);
 fakeInfo.flags=0;
 CHECK(LpQueryBoundAddress(&ctx)==STATUS_DEVICE_CONFIGURATION_ERROR && ctx.BoundAddress==0);
 fakeInfo.flags=BDIF_ADDRESS; fakeInfo.address=0; ctx.BoundAddress=bound;
 CHECK(LpQueryBoundAddress(&ctx)==STATUS_DEVICE_CONFIGURATION_ERROR && ctx.BoundAddress==0);
 fakeInfo.address=0x1000000000000;
 CHECK(LpQueryBoundAddress(&ctx)==STATUS_DEVICE_CONFIGURATION_ERROR && ctx.BoundAddress==0);
 fakeInfo.address=bound; fakeInformation=sizeof(fakeInfo)-1;
 CHECK(LpQueryBoundAddress(&ctx)==STATUS_DEVICE_CONFIGURATION_ERROR && ctx.BoundAddress==0);
 fakeInformation=sizeof(fakeInfo)+1;
 CHECK(LpQueryBoundAddress(&ctx)==STATUS_DEVICE_CONFIGURATION_ERROR && ctx.BoundAddress==0);
 fakeInformation=sizeof(fakeInfo); fakeSendStatus=STATUS_UNSUCCESSFUL; ctx.BoundAddress=bound;
 CHECK(LpQueryBoundAddress(&ctx)==STATUS_UNSUCCESSFUL && ctx.BoundAddress==0);
 fakeSendStatus=STATUS_SUCCESS; fakeCreateStatus=STATUS_UNSUCCESSFUL; ctx.BoundAddress=bound;
 { unsigned beforeSent=sent, beforeDeleted=deleted;
 CHECK(LpQueryBoundAddress(&ctx)==STATUS_UNSUCCESSFUL && ctx.BoundAddress==0);
 CHECK(sent==beforeSent && deleted==beforeDeleted);
 }
 CHECK(badFormat==0);
 printf("Production bound query: %u checks, %u failures\n",checks,failures);
 return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
