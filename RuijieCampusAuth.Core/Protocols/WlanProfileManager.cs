using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace RuijieCampusAuth.Core.Protocols;

public sealed record WlanInterfaceInfo(Guid Id, string Description, int State);

public sealed record WlanProfileInfo(string Name, uint Flags);

public static class WlanProfileManager
{
    private const uint WlanClientVersion = 2;
    private const int ErrorSuccess = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfoNative
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;

        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanProfileInfoNative
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public int ConnectionMode;
        public string ProfileName;
        public IntPtr Ssid;
        public IntPtr BssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint clientVersion,
        IntPtr reserved,
        out uint negotiatedVersion,
        out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanEnumInterfaces(
        IntPtr handle,
        IntPtr reserved,
        out IntPtr list);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfileList(
        IntPtr handle,
        ref Guid interfaceGuid,
        IntPtr reserved,
        out IntPtr list);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfile(
        IntPtr handle,
        ref Guid interfaceGuid,
        uint flags,
        string profileXml,
        string? allUserSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool overwrite,
        out uint reason);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanDeleteProfile(
        IntPtr handle,
        ref Guid interfaceGuid,
        string profileName,
        IntPtr reserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfileEapXmlUserData(
        IntPtr handle,
        ref Guid interfaceGuid,
        string profileName,
        uint flags,
        string userXml,
        IntPtr reserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanConnect(
        IntPtr handle,
        ref Guid interfaceGuid,
        ref WlanConnectionParameters parameters,
        IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(
        IntPtr handle,
        ref Guid interfaceGuid,
        IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pointer);

    public static IReadOnlyList<WlanInterfaceInfo> ListInterfaces()
    {
        var handle = Open();
        try
        {
            var rc = WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (rc != ErrorSuccess)
            {
                throw new Win32Exception((int)rc, "WlanEnumInterfaces failed");
            }
            try
            {
                var count = Marshal.ReadInt32(list);
                var size = Marshal.SizeOf<WlanInterfaceInfoNative>();
                var result = new List<WlanInterfaceInfo>(count);
                for (var i = 0; i < count; i++)
                {
                    var pointer = IntPtr.Add(list, 8 + i * size);
                    var item = Marshal.PtrToStructure<WlanInterfaceInfoNative>(pointer);
                    result.Add(new WlanInterfaceInfo(
                        item.InterfaceGuid,
                        item.Description,
                        item.State));
                }
                return result;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    public static IReadOnlyList<WlanProfileInfo> ListProfiles(Guid interfaceGuid)
    {
        var handle = Open();
        try
        {
            var rc = WlanGetProfileList(
                handle,
                ref interfaceGuid,
                IntPtr.Zero,
                out var list);
            if (rc != ErrorSuccess)
            {
                throw new Win32Exception((int)rc, "WlanGetProfileList failed");
            }
            try
            {
                var count = Marshal.ReadInt32(list);
                var size = Marshal.SizeOf<WlanProfileInfoNative>();
                var result = new List<WlanProfileInfo>(count);
                for (var i = 0; i < count; i++)
                {
                    var pointer = IntPtr.Add(list, 8 + i * size);
                    var item = Marshal.PtrToStructure<WlanProfileInfoNative>(pointer);
                    result.Add(new WlanProfileInfo(item.ProfileName, item.Flags));
                }
                return result;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    public static Guid ResolveInterface(string? hint)
    {
        var interfaces = ListInterfaces();
        if (interfaces.Count == 0)
        {
            throw new InvalidOperationException("no WLAN interface");
        }
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var guidMatch = System.Text.RegularExpressions.Regex.Match(
                hint,
                "\\{?([0-9a-fA-F-]{36})\\}?");
            if (guidMatch.Success && Guid.TryParse(guidMatch.Groups[1].Value, out var guid))
            {
                var match = interfaces.FirstOrDefault(item => item.Id == guid);
                if (match is not null)
                {
                    return match.Id;
                }
            }
            var byName = interfaces.FirstOrDefault(item =>
                item.Description.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName.Id;
            }
        }
        return interfaces[0].Id;
    }

    public static void CreatePeapProfile(
        Guid interfaceGuid,
        string ssid,
        string identity,
        string password,
        string authentication = "WPA2",
        string encryption = "AES",
        bool autoConnect = true,
        string serverNames = "",
        string trustedRootCa = "")
    {
        var profileXml = BuildProfileXml(
            ssid,
            authentication,
            encryption,
            autoConnect,
            serverNames,
            trustedRootCa);
        var handle = Open();
        try
        {
            var rc = WlanSetProfile(
                handle,
                ref interfaceGuid,
                0,
                profileXml,
                null,
                true,
                out var reason);
            if (rc != ErrorSuccess)
            {
                throw new Win32Exception(
                    (int)rc,
                    $"WlanSetProfile failed (reason {reason})");
            }
            var userXml = BuildUserCredentialXml(identity, password);
            rc = WlanSetProfileEapXmlUserData(
                handle,
                ref interfaceGuid,
                ssid,
                0,
                userXml,
                IntPtr.Zero);
            if (rc != ErrorSuccess)
            {
                throw new Win32Exception(
                    (int)rc,
                    "WlanSetProfileEapXmlUserData failed");
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    public static void DeleteProfile(Guid interfaceGuid, string profileName)
    {
        var handle = Open();
        try
        {
            var rc = WlanDeleteProfile(
                handle,
                ref interfaceGuid,
                profileName,
                IntPtr.Zero);
            if (rc != ErrorSuccess)
            {
                throw new Win32Exception((int)rc, "WlanDeleteProfile failed");
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    public static void Connect(Guid interfaceGuid, string profileName)
    {
        var handle = Open();
        try
        {
            var parameters = new WlanConnectionParameters
            {
                ConnectionMode = 0,
                ProfileName = profileName,
                Ssid = IntPtr.Zero,
                BssidList = IntPtr.Zero,
                BssType = 1,
                Flags = 0,
            };
            var rc = WlanConnect(
                handle,
                ref interfaceGuid,
                ref parameters,
                IntPtr.Zero);
            if (rc != ErrorSuccess)
            {
                throw new Win32Exception((int)rc, "WlanConnect failed");
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    public static void Disconnect(Guid interfaceGuid)
    {
        var handle = Open();
        try
        {
            var rc = WlanDisconnect(handle, ref interfaceGuid, IntPtr.Zero);
            if (rc != ErrorSuccess)
            {
                throw new Win32Exception((int)rc, "WlanDisconnect failed");
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    public static string BuildProfileXml(
        string ssid,
        string authentication,
        string encryption,
        bool autoConnect,
        string serverNames,
        string trustedRootCa)
    {
        var mode = autoConnect ? "auto" : "manual";
        var names = string.Join(
            string.Empty,
            serverNames.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => $"<ServerName>{Escape(name)}</ServerName>"));
        var ca = string.IsNullOrWhiteSpace(trustedRootCa)
            ? string.Empty
            : $"<TrustedRootCA>{Escape(trustedRootCa)}</TrustedRootCA>";
        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{Escape(ssid)}</name>
              <SSIDConfig><SSID><name>{Escape(ssid)}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>{mode}</connectionMode>
              <MSM><security>
                <authEncryption>
                  <authentication>{Escape(authentication)}</authentication>
                  <encryption>{Escape(encryption)}</encryption>
                  <useOneX>true</useOneX>
                </authEncryption>
                <OneX xmlns="http://www.microsoft.com/networking/OneX/v1">
                  <authMode>user</authMode>
                  <EAPConfig>
                    <EapHostConfig xmlns="http://www.microsoft.com/provisioning/EapHostConfig">
                      <EapMethod>
                        <Type xmlns="http://www.microsoft.com/provisioning/EapCommon">25</Type>
                        <VendorId xmlns="http://www.microsoft.com/provisioning/EapCommon">0</VendorId>
                        <VendorType xmlns="http://www.microsoft.com/provisioning/EapCommon">0</VendorType>
                        <AuthorId xmlns="http://www.microsoft.com/provisioning/EapCommon">29115</AuthorId>
                      </EapMethod>
                      <Config xmlns="http://www.microsoft.com/provisioning/BaseEapMethodConfig">
                        <Eap xmlns="http://www.microsoft.com/provisioning/BaseEapConnectionPropertiesV1">
                          <Type>25</Type>
                          <EapType xmlns="http://www.microsoft.com/provisioning/MsPeapConnectionPropertiesV1">
                            <ServerValidation>
                              <DisableUserPromptForServerValidation>true</DisableUserPromptForServerValidation>
                              <ServerNames>{names}</ServerNames>
                              {ca}
                            </ServerValidation>
                            <FastReconnect>true</FastReconnect>
                            <InnerEapOptional>false</InnerEapOptional>
                            <Eap xmlns="http://www.microsoft.com/provisioning/BaseEapConnectionPropertiesV1">
                              <Type>26</Type>
                              <EapType xmlns="http://www.microsoft.com/provisioning/MsChapV2ConnectionPropertiesV1">
                                <UseWinLogonCredentials>false</UseWinLogonCredentials>
                              </EapType>
                            </Eap>
                            <EnableQuarantineChecks>false</EnableQuarantineChecks>
                            <RequireCryptoBinding>false</RequireCryptoBinding>
                            <PeapExtensions>
                              <PerformServerValidation>false</PerformServerValidation>
                              <AcceptServerName>false</AcceptServerName>
                            </PeapExtensions>
                          </EapType>
                        </Eap>
                      </Config>
                    </EapHostConfig>
                  </EAPConfig>
                </OneX>
              </security></MSM>
            </WLANProfile>
            """.Trim();
    }

    public static string BuildUserCredentialXml(string identity, string password)
    {
        return $"""
            <EapHostUserCredentials xmlns="http://www.microsoft.com/provisioning/EapHostUserCredentials" xmlns:eapCommon="http://www.microsoft.com/provisioning/EapCommon" xmlns:baseEap="http://www.microsoft.com/provisioning/BaseEapMethodUserCredentials">
              <EapMethod>
                <eapCommon:Type>25</eapCommon:Type>
                <eapCommon:AuthorId>29115</eapCommon:AuthorId>
              </EapMethod>
              <Credentials xmlns:eapUser="http://www.microsoft.com/provisioning/EapUserPropertiesV1" xmlns:baseEap="http://www.microsoft.com/provisioning/BaseEapUserPropertiesV1">
                <baseEap:Eap>
                  <EapType>25</EapType>
                  <Identity>{Escape(identity)}</Identity>
                  <Password>{Escape(password)}</Password>
                </baseEap:Eap>
              </Credentials>
            </EapHostUserCredentials>
            """.Trim();
    }

    private static string Escape(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }

    private static IntPtr Open()
    {
        var rc = WlanOpenHandle(
            WlanClientVersion,
            IntPtr.Zero,
            out _,
            out var handle);
        if (rc != ErrorSuccess)
        {
            throw new Win32Exception((int)rc, "WlanOpenHandle failed");
        }
        return handle;
    }
}
