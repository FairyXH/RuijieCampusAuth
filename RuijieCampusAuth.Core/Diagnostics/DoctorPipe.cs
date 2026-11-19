using System.Runtime.InteropServices;

namespace RuijieCampusAuth.Core.Diagnostics;

public static class DoctorPipe
{
    public const string PipePath = @"\\.\pipe\SU_DOCTOR_INTERFACE";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipe(string name, int timeout);

    public static bool IsAvailable(int timeoutMilliseconds = 100)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            return WaitNamedPipe(PipePath, Math.Max(0, timeoutMilliseconds));
        }
        catch
        {
            return false;
        }
    }
}
