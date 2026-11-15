/* Pure target guard, shared by the kernel and offline compiled tests.
 * Call BEFORE inspecting or changing any active channel.
 */
#ifndef NTPODS_AAP_GUARDS_H
#define NTPODS_AAP_GUARDS_H
#define LP_AAP_PSM 0x1001u
#define LP_MAX_TRANSFER_BYTES 8192u
#define LP_MAX_RECV_TIMEOUT_MS 5000u
static __inline NTSTATUS
LpValidateConnectTarget(BTH_ADDR BoundAddress, BTH_ADDR RequestedAddress, USHORT Psm)
{
    if (BoundAddress == 0 || (BoundAddress >> 48) != 0) {
        return STATUS_DEVICE_NOT_READY;
    }
    if (RequestedAddress == 0 || (RequestedAddress >> 48) != 0 ||
        RequestedAddress != BoundAddress || Psm != LP_AAP_PSM) {
        return STATUS_INVALID_PARAMETER;
    }
    return STATUS_SUCCESS;
}
#endif
