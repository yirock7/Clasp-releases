using AppleDeviceHub.Core.Connection;
using Windows.Devices.Radios;

namespace AppleDeviceHub.App.Connection;

/// <summary>
/// Windows implementation of <see cref="IBluetoothRadioSwitch"/> over Windows.Devices.Radios.
/// It only switches Bluetooth radios; it never touches audio devices or pairings. When a PC has
/// several Bluetooth radios, all of them are switched together and the state is On only when
/// every radio is On.
/// </summary>
public sealed class BluetoothRepair : IBluetoothRadioSwitch
{
    public async Task<RadioStateResult> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var (outcome, radios, detail) = await OpenRadiosAsync(cancellationToken).ConfigureAwait(false);
        if (outcome != RadioSwitchOutcome.Success) return new(outcome, BluetoothRadioState.Unknown, detail);
        return new(RadioSwitchOutcome.Success, Aggregate(radios));
    }

    public async Task<RadioSwitchResult> SetStateAsync(bool turnOn, CancellationToken cancellationToken = default)
    {
        var (outcome, radios, detail) = await OpenRadiosAsync(cancellationToken).ConfigureAwait(false);
        if (outcome != RadioSwitchOutcome.Success) return new(outcome, detail);

        var wanted = turnOn ? RadioState.On : RadioState.Off;
        foreach (var radio in radios)
        {
            if (radio.State == wanted) continue;
            cancellationToken.ThrowIfCancellationRequested();
            RadioAccessStatus status;
            try
            {
                status = await radio.SetStateAsync(wanted).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new(RadioSwitchOutcome.Failed, exception.GetType().Name);
            }
            if (status != RadioAccessStatus.Allowed) return new(Map(status), status.ToString());
        }

        // SetStateAsync can complete before the state is observable; confirm briefly.
        for (int i = 0; i < 20; i++)
        {
            if (radios.All(radio => radio.State == wanted)) return new(RadioSwitchOutcome.Success);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return new(RadioSwitchOutcome.Failed, "Bluetooth did not change. Try again.");
    }

    private static async Task<(RadioSwitchOutcome Outcome, IReadOnlyList<Radio> Radios, string? Detail)> OpenRadiosAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var access = await Radio.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (access != RadioAccessStatus.Allowed) return (Map(access), [], access.ToString());
            var all = await Radio.GetRadiosAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var bluetooth = all.Where(radio => radio.Kind == RadioKind.Bluetooth).ToList();
            return bluetooth.Count == 0
                ? (RadioSwitchOutcome.NoRadio, [], "No Bluetooth radio")
                : (RadioSwitchOutcome.Success, bluetooth, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (RadioSwitchOutcome.Failed, [], exception.GetType().Name);
        }
    }

    private static BluetoothRadioState Aggregate(IReadOnlyList<Radio> radios)
    {
        if (radios.All(radio => radio.State == RadioState.On)) return BluetoothRadioState.On;
        if (radios.Any(radio => radio.State == RadioState.Off)) return BluetoothRadioState.Off;
        return BluetoothRadioState.Unknown;
    }

    private static RadioSwitchOutcome Map(RadioAccessStatus status) => status switch
    {
        RadioAccessStatus.Allowed => RadioSwitchOutcome.Success,
        RadioAccessStatus.DeniedByUser or RadioAccessStatus.DeniedBySystem => RadioSwitchOutcome.AccessDenied,
        _ => RadioSwitchOutcome.Failed,
    };
}
