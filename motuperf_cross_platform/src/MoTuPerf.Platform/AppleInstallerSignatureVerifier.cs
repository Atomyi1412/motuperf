using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MoTuPerf.Platform
{
    internal interface IAppleInstallerSignatureVerifier
    {
        bool IsValid(string path, out string reason);
    }

    internal sealed class AppleInstallerSignatureVerifier : IAppleInstallerSignatureVerifier
    {
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public bool IsValid(string path, out string reason)
        {
            reason = "";
            if (!OperatingSystem.IsWindows())
            {
                reason = "当前系统不支持 Windows Authenticode 校验。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                reason = "安装包不存在。";
                return false;
            }

            WinTrustFileInfo fileInfo = new WinTrustFileInfo(path);
            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            IntPtr trustDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
                WinTrustData trustData = WinTrustData.Create(fileInfoPtr);
                Marshal.StructureToPtr(trustData, trustDataPtr, false);
                uint status = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, trustDataPtr);
                if (status != 0)
                {
                    reason = "Windows 未通过 Authenticode 签名验证。";
                    return false;
                }

                try
                {
                    X509Certificate2 certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                    string subject = certificate.Subject ?? "";
                    string issuer = certificate.Issuer ?? "";
                    if (subject.IndexOf("Apple", StringComparison.OrdinalIgnoreCase) < 0
                        && issuer.IndexOf("Apple", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        reason = "安装包签名者不是 Apple。";
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    reason = "无法读取 Apple 签名证书：" + exception.Message;
                    return false;
                }
                return true;
            }
            finally
            {
                if (fileInfo.FilePath != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfo.FilePath);
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(trustDataPtr);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionIdentifier,
            IntPtr trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo
        {
            public int StructureSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;

            public WinTrustFileInfo(string path)
            {
                StructureSize = Marshal.SizeOf<WinTrustFileInfo>();
                FilePath = Marshal.StringToCoTaskMemUni(path);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public int StructureSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public int UiChoice;
            public int RevocationChecks;
            public int UnionChoice;
            public IntPtr FileInfo;
            public int StateAction;
            public IntPtr StateData;
            public string UrlReference;
            public int ProviderFlags;
            public int UiContext;

            public static WinTrustData Create(IntPtr fileInfo)
            {
                return new WinTrustData
                {
                    StructureSize = Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = fileInfo,
                    StateAction = 0,
                    ProviderFlags = 0x00000010,
                    UiContext = 0
                };
            }
        }
    }
}
