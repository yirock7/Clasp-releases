using System.Runtime.InteropServices;
using AppleDeviceHub.App.Audio;

namespace AppleDeviceHub.Core.Tests.Audio;

public class MtaThreadTests
{
    [Fact]
    public void Run_uses_a_distinct_com_initialized_mta_thread()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var (workerThreadId, apartment, comApartment, hresult) = MtaThread.Run(() =>
        {
            var result = CoGetApartmentType(out var type, out _);
            return (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), type, result);
        });

        Assert.NotEqual(callerThreadId, workerThreadId);
        Assert.Equal(ApartmentState.MTA, apartment);
        Assert.Equal(1, comApartment); // APTTYPE_MTA
        Assert.True(hresult >= 0);
    }

    [Fact]
    public void Run_rethrows_work_failure_to_the_caller()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MtaThread.Run<int>(() => throw new InvalidOperationException("expected test failure")));

        Assert.Equal("expected test failure", exception.Message);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoGetApartmentType(out int apartmentType, out int apartmentQualifier);
}
