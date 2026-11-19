using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RuijieCampusAuth.Core;

public sealed record ServiceState(bool Installed, bool Running, string Detail);

public static class ServiceInstaller
{
    public const string ServiceName = "RuijieCampusAuth";
    public const string DisplayName = "Ruijie Auth";
    private const string LinuxUnitPath = "/etc/systemd/system/ruijie-campus-auth.service";
    // Legacy identifiers kept only for one-way migration from earlier builds.
    private const string LegacyServiceName = "HXXY-CampusAuth";
    private const string LegacyLinuxUnitPath = "/etc/systemd/system/hxxy-campus-auth.service";

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceDelete = 0x10000;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 0x02;
    private const uint ServiceErrorNormal = 0x01;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceControlStop = 0x01;
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceDoesNotExist = 1060;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(
        IntPtr scManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static string ServiceExecutablePath
    {
        get
        {
            var name = OperatingSystem.IsWindows()
                ? "RuijieCampusAuth.Service.exe"
                : "RuijieCampusAuth.Service";
            return Path.Combine(AppContext.BaseDirectory, name);
        }
    }

    private static void RemoveLegacyService(IntPtr scm)
    {
        var service = OpenService(
            scm,
            LegacyServiceName,
            ServiceStop | ServiceQueryStatus | ServiceDelete);
        if (service == IntPtr.Zero)
        {
            return;
        }
        try
        {
            var status = new ServiceStatus();
            if (QueryServiceStatus(service, ref status) && status.CurrentState == 4)
            {
                ControlService(service, ServiceControlStop, ref status);
            }
            DeleteService(service);
        }
        catch
        {
            // Best effort migration; the new service can still be installed.
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    public static ServiceState Query()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (OperatingSystem.IsLinux())
            {
                var installed = File.Exists(LinuxUnitPath) ||
                                File.Exists(LegacyLinuxUnitPath);
                return new ServiceState(
                    installed,
                    false,
                    installed ? "已安装（systemd）" : "未安装");
            }
            return new ServiceState(false, false, "当前平台暂不支持服务安装");
        }

        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            return new ServiceState(false, false, Win32Message("OpenSCManager"));
        }
        try
        {
            var service = OpenService(scm, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    var legacy = OpenService(scm, LegacyServiceName, ServiceQueryStatus);
                    if (legacy != IntPtr.Zero)
                    {
                        CloseServiceHandle(legacy);
                        return new ServiceState(true, false, "旧服务名，需重新安装");
                    }
                }
                return new ServiceState(
                    error != ErrorServiceDoesNotExist,
                    false,
                    error == ErrorServiceDoesNotExist ? "未安装" : Win32Message(error));
            }
            try
            {
                var status = new ServiceStatus();
                if (!QueryServiceStatus(service, ref status))
                {
                    return new ServiceState(true, false, Win32Message("QueryServiceStatus"));
                }
                var running = status.CurrentState == 4;
                return new ServiceState(true, running, running ? "运行中" : "已停止");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static void Install()
    {
        var executable = ServiceExecutablePath;
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("服务组件不存在", executable);
        }
        if (OperatingSystem.IsLinux())
        {
            if (!Environment.IsPrivilegedProcess)
            {
                throw new UnauthorizedAccessException("安装 systemd 服务需要 root");
            }
            var workingDirectory = AppContext.BaseDirectory.TrimEnd('/');
            var unit = $"""
                [Unit]
                Description=Ruijie Auth
                After=network-online.target
                Wants=network-online.target

                [Service]
                Type=notify
                ExecStart={executable}
                WorkingDirectory={workingDirectory}
                User=root
                Restart=always
                RestartSec=5

                [Install]
                WantedBy=multi-user.target
                """;
            File.Delete(LegacyLinuxUnitPath);
            File.WriteAllText(LinuxUnitPath, unit);
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("当前平台暂不支持服务安装");
        }

        var scm = OpenSCManager(null, null, ScManagerAllAccess);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed");
        }
        try
        {
            RemoveLegacyService(scm);
            var service = CreateService(
                scm,
                ServiceName,
                DisplayName,
                ServiceAllAccess,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                $"\"{executable}\"",
                null,
                IntPtr.Zero,
                null,
                null,
                null);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceExists)
                {
                    throw new Win32Exception(error, "CreateService failed");
                }
                service = OpenService(scm, ServiceName, ServiceAllAccess);
                if (service == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(), "OpenService failed");
                }
                if (!ChangeServiceConfig(
                        service,
                        ServiceNoChange,
                        ServiceAutoStart,
                        ServiceNoChange,
                        $"\"{executable}\"",
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null,
                        DisplayName))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(), "ChangeServiceConfig failed");
                }
            }
            CloseServiceHandle(service);
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static void Uninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("当前平台暂不支持服务安装");
            }
            if (!Environment.IsPrivilegedProcess)
            {
                throw new UnauthorizedAccessException("卸载 systemd 服务需要 root");
            }
            if (File.Exists(LinuxUnitPath))
            {
                File.Delete(LinuxUnitPath);
            }
            return;
        }

        var scm = OpenSCManager(null, null, ScManagerAllAccess);
        if (scm == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return;
            }
            throw new Win32Exception(error, "OpenSCManager failed");
        }
        try
        {
            var service = OpenService(
                scm, ServiceName, ServiceStop | ServiceQueryStatus | ServiceDelete);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    return;
                }
                throw new Win32Exception(error, "OpenService failed");
            }
            try
            {
                var status = new ServiceStatus();
                ControlService(service, ServiceControlStop, ref status);
                if (!DeleteService(service))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceDoesNotExist)
                    {
                        throw new Win32Exception(error, "DeleteService failed");
                    }
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Linux 请使用 systemctl start ruijie-campus-auth.service");
        }
        var scm = OpenSCManager(null, null, ScManagerAllAccess);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed");
        }
        try
        {
            var service = OpenService(scm, ServiceName, ServiceStart | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenService failed");
            }
            try
            {
                if (!StartService(service, 0, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1056)
                    {
                        throw new Win32Exception(error, "StartService failed");
                    }
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static void Stop()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Linux 请使用 systemctl stop ruijie-campus-auth.service");
        }
        var scm = OpenSCManager(null, null, ScManagerAllAccess);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed");
        }
        try
        {
            var service = OpenService(scm, ServiceName, ServiceStop | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenService failed");
            }
            try
            {
                var status = new ServiceStatus();
                if (!ControlService(service, ServiceControlStop, ref status))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1062)
                    {
                        throw new Win32Exception(error, "ControlService failed");
                    }
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static string Win32Message(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return $"{operation} failed ({error}): {new Win32Exception(error).Message}";
    }

    private static string Win32Message(int error)
    {
        return $"{error}: {new Win32Exception(error).Message}";
    }
}
