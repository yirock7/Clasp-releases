/* Compiles the unmodified LpConnect function extracted from L2cap.c by the
 * runner. Only allocation/submission/locking are fake; the target validation,
 * channel-state branches and BRB population are production code. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef int32_t NTSTATUS;
typedef uint16_t USHORT;
typedef uint64_t BTH_ADDR;
typedef void *PBRB;
typedef void *PVOID;
typedef void *L2CAP_CHANNEL_HANDLE;
typedef unsigned char BOOLEAN;
typedef void VOID;
#define TRUE 1
#define FALSE 0
#define STATUS_DEVICE_NOT_CONNECTED ((NTSTATUS)-4)
#define STATUS_UNSUCCESSFUL ((NTSTATUS)-5)
#define STATUS_SUCCESS ((NTSTATUS)0)
#define STATUS_DEVICE_NOT_READY ((NTSTATUS)-1)
#define STATUS_INVALID_PARAMETER ((NTSTATUS)-2)
#define STATUS_INSUFFICIENT_RESOURCES ((NTSTATUS)-3)
#define NT_SUCCESS(s) ((s) >= 0)
#define KdPrint(x) ((void)0)
#define LP_POOL_TAG 1
#define BRB_L2CA_OPEN_CHANNEL 1
#define BRB_L2CA_CLOSE_CHANNEL 2
#define CF_ROLE_EITHER 1
#define CF_LINK_AUTHENTICATED 2
#define CF_LINK_ENCRYPTED 4
#define CFG_MTU 1
#define CALLBACK_DISCONNECT 1
#define LpDisconnected 0
#define LpConnecting 1
#define LpConnected 2
#include "../AapGuards.h"
typedef unsigned INDICATION_CODE;
#define IndicationRemoteDisconnect 1
typedef struct { L2CAP_CHANNEL_HANDLE ConnectionHandle;
 struct { struct { BOOLEAN CloseNow; } Disconnect; } Parameters;
} INDICATION_PARAMETERS, *PINDICATION_PARAMETERS;
static VOID LpIndicationCallback(void *, INDICATION_CODE, PINDICATION_PARAMETERS);
struct _BRB_L2CA_OPEN_CHANNEL {
 BTH_ADDR BtAddress; USHORT Psm; unsigned ChannelFlags;
 struct { unsigned Flags; struct { unsigned Min, Preferred, Max; } Mtu; } ConfigOut, ConfigIn;
 unsigned IncomingQueueDepth, CallbackFlags;
 void (*Callback)(void *, INDICATION_CODE, PINDICATION_PARAMETERS); void *CallbackContext, *ReferenceObject;
 L2CAP_CHANNEL_HANDLE ChannelHandle;
};
struct _BRB_L2CA_CLOSE_CHANNEL { BTH_ADDR BtAddress; L2CAP_CHANNEL_HANDLE ChannelHandle; };
typedef struct {
 BTH_ADDR BoundAddress; unsigned HasBthInterface;
 unsigned State; BTH_ADDR RemoteAddress; USHORT Psm; void *Lock;
 void *WdmDeviceObject; L2CAP_CHANNEL_HANDLE ChannelHandle;
 BOOLEAN OpenInterrupted; L2CAP_CHANNEL_HANDLE OpeningDisconnectedHandle;
 struct { void *(*BthAllocateBrb)(unsigned,unsigned); void (*BthFreeBrb)(PBRB); } BthInterface;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;
static unsigned allocations, submissions, allocatedType;
static BOOLEAN failAllocation, disconnectDuringOpen;
static NTSTATUS closeStatus=STATUS_SUCCESS;
static BTH_ADDR submittedAddress;
static USHORT submittedPsm;
static void *allocate(unsigned type, unsigned tag) {
 (void)tag; ++allocations; allocatedType=type;
 return failAllocation ? NULL : calloc(1, sizeof(struct _BRB_L2CA_OPEN_CHANNEL));
}
static void WdfSpinLockAcquire(void *lock) { (void)lock; }
static void WdfSpinLockRelease(void *lock) { (void)lock; }
static NTSTATUS LpDisconnect(PDEVICE_CONTEXT c);
static NTSTATUS LpSubmitBrbSync(PDEVICE_CONTEXT c, PBRB ptr) {
 struct _BRB_L2CA_OPEN_CHANNEL *brb = ptr; ++submissions;
 if (allocatedType==BRB_L2CA_CLOSE_CHANNEL) return closeStatus;
 submittedAddress=brb->BtAddress; submittedPsm=brb->Psm;
 brb->ChannelHandle=(void *)(uintptr_t)0x1234;
 if (disconnectDuringOpen) {
  INDICATION_PARAMETERS p={0}; p.ConnectionHandle=brb->ChannelHandle;
  LpIndicationCallback(c,IndicationRemoteDisconnect,&p);
 }
 return STATUS_SUCCESS;
}
#include "l2cap_connect_under_test.h"
#include "l2cap_disconnect_under_test.h"
#include "l2cap_indication_under_test.h"
static unsigned checks, failures;
#define CHECK(expr) do { ++checks; if (!(expr)) { ++failures; \
 fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #expr); } } while (0)
int main(void) {
 DEVICE_CONTEXT ctx = {0};
 unsigned psmIndex;
 const BTH_ADDR bound=0x123456789abc;
 ctx.BthInterface.BthAllocateBrb=allocate; ctx.BthInterface.BthFreeBrb=free;
 ctx.BoundAddress=bound; ctx.HasBthInterface=1; ctx.State=LpConnected;
 ctx.RemoteAddress=bound; ctx.Psm=LP_AAP_PSM; ctx.ChannelHandle=(void *)(uintptr_t)0x2345;
 // Every non-AAP PSM must leave the existing channel and BRB allocator untouched.
 for (psmIndex=0;psmIndex<=0xffff;psmIndex++) {
  NTSTATUS status=LpConnect(&ctx,bound,(USHORT)psmIndex);
  if (psmIndex==LP_AAP_PSM) CHECK(status==STATUS_SUCCESS);
  else CHECK(status==STATUS_INVALID_PARAMETER);
 }
 CHECK(allocations==0 && submissions==0);
 CHECK(ctx.State==LpConnected && ctx.ChannelHandle==(void *)(uintptr_t)0x2345);
 CHECK(LpConnect(&ctx,0,LP_AAP_PSM)==STATUS_INVALID_PARAMETER);
 CHECK(LpConnect(&ctx,bound+1,LP_AAP_PSM)==STATUS_INVALID_PARAMETER);
 CHECK(LpConnect(&ctx,0x1000000000000,LP_AAP_PSM)==STATUS_INVALID_PARAMETER);
 CHECK(allocations==0);
 ctx.BoundAddress=0; CHECK(LpConnect(&ctx,bound,LP_AAP_PSM)==STATUS_DEVICE_NOT_READY);
 ctx.BoundAddress=0x1000000000000; CHECK(LpConnect(&ctx,bound,LP_AAP_PSM)==STATUS_DEVICE_NOT_READY);
 ctx.BoundAddress=bound; ctx.HasBthInterface=0;
 CHECK(LpConnect(&ctx,bound,LP_AAP_PSM)==STATUS_DEVICE_NOT_READY);
 ctx.HasBthInterface=1; ctx.State=LpDisconnected; ctx.ChannelHandle=NULL;
 CHECK(LpConnect(&ctx,bound,LP_AAP_PSM)==STATUS_SUCCESS);
 CHECK(allocations==1 && submissions==1 && submittedAddress==bound && submittedPsm==LP_AAP_PSM);
 CHECK(ctx.State==LpConnected && ctx.RemoteAddress==bound && ctx.Psm==LP_AAP_PSM);
 // Local close must not lose the only channel handle when allocation fails.
 failAllocation=1; CHECK(LpDisconnect(&ctx)==STATUS_INSUFFICIENT_RESOURCES);
 CHECK(ctx.ChannelHandle==(void *)(uintptr_t)0x1234 && ctx.State==LpDisconnected);
 failAllocation=0; closeStatus=STATUS_UNSUCCESSFUL;
 CHECK(LpDisconnect(&ctx)==STATUS_UNSUCCESSFUL);
 CHECK(ctx.ChannelHandle==(void *)(uintptr_t)0x1234);
 { unsigned before=submissions;
   CHECK(LpConnect(&ctx,bound,LP_AAP_PSM)==STATUS_UNSUCCESSFUL);
   CHECK(submissions==before+1 && allocatedType==BRB_L2CA_CLOSE_CHANNEL);
 }
 closeStatus=STATUS_SUCCESS; CHECK(LpDisconnect(&ctx)==STATUS_SUCCESS);
 CHECK(ctx.ChannelHandle==NULL && ctx.RemoteAddress==0 && ctx.Psm==0);
 CHECK(LpDisconnect(&ctx)==STATUS_SUCCESS);
 // Disconnect arriving before the open BRB returns must never publish success.
 disconnectDuringOpen=1;
 CHECK(LpConnect(&ctx,bound,LP_AAP_PSM)==STATUS_DEVICE_NOT_CONNECTED);
 CHECK(ctx.State==LpDisconnected && ctx.ChannelHandle==NULL && ctx.RemoteAddress==0);
 disconnectDuringOpen=0;
 CHECK(LpConnect(&ctx,bound,LP_AAP_PSM)==STATUS_SUCCESS);
 // A late callback for a different retired channel leaves the current one alone.
 { INDICATION_PARAMETERS p={0}; p.ConnectionHandle=(void *)(uintptr_t)0x7777;
   LpIndicationCallback(&ctx,IndicationRemoteDisconnect,&p);
   CHECK(p.Parameters.Disconnect.CloseNow==TRUE);
   CHECK(ctx.State==LpConnected && ctx.ChannelHandle==(void *)(uintptr_t)0x1234);
   p.ConnectionHandle=ctx.ChannelHandle;
   LpIndicationCallback(&ctx,IndicationRemoteDisconnect,&p);
   CHECK(ctx.State==LpDisconnected && ctx.ChannelHandle==NULL && ctx.RemoteAddress==0);
 }
 printf("Production LpConnect: %u checks, %u failures\n",checks,failures);
 return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
