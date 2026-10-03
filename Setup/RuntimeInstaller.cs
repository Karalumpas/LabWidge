using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LabWidgeSetup
{
    /// <summary>Finds, downloads and installs Microsoft .NET Desktop Runtime.</summary>
    internal static class RuntimeInstaller
    {
        public const int RequiredMajor = 8;
        public const string DownloadPage = "https://dotnet.microsoft.com/download/dotnet/8.0";
        private const string DownloadUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

        /// <summary>The highest installed Microsoft.WindowsDesktop.App (x64) with major ≥ 8, or null.</summary>
        public static Version FindDesktopRuntime()
        {
            var versions = new System.Collections.Generic.List<Version>();

            // 1) The default location for x64 runtimes
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var root in new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"), Path.Combine(programFiles, "dotnet") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var dir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(dir)) continue;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (TryParse(Path.GetFileName(sub), out var v)) versions.Add(v);
                }
            }

            // 2) The registry (written by the runtime installer in the 32-bit view)
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"))
                {
                    if (key != null)
                    {
                        foreach (var name in key.GetValueNames())
                        {
                            if (TryParse(name, out var v)) versions.Add(v);
                        }
                    }
                }
            }
            catch
            {
                // No access – the folder scan is enough.
            }

            return versions.Where(v => v.Major >= RequiredMajor).OrderByDescending(v => v).FirstOrDefault();
        }

        private static bool TryParse(string text, out Version version)
        {
            var clean = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return Version.TryParse(clean, out version);
        }

        public static async Task<string> DownloadAsync(IProgress<(long Received, long Total)> progress, CancellationToken token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var target = Path.Combine(Path.GetTempPath(), "LabWidge-windowsdesktop-runtime-8-win-x64.exe");

            using (var client = new WebClient())
            using (token.Register(client.CancelAsync))
            {
                client.Headers.Add("User-Agent", "LabWidge-Setup");
                client.DownloadProgressChanged += (_, e) => progress.Report((e.BytesReceived, e.TotalBytesToReceive));
                try
                {
                    await client.DownloadFileTaskAsync(new Uri(DownloadUrl), target);
                }
                catch (WebException) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
            }

            VerifyMicrosoftSignature(target);
            return target;
        }

        /// <summary>Rejects the file unless it has a valid Authenticode signature from Microsoft.</summary>
        public static void VerifyMicrosoftSignature(string path)
        {
            if (!WinTrust.IsTrusted(path))
                throw new InvalidOperationException(L.T("The downloaded file has no valid digital signature and was not run.", "Den hentede fil har ikke en gyldig digital signatur og blev ikke kørt."));

            var subject = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).Subject;
            if (subject.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException(L.T($"The downloaded file is not signed by Microsoft ({subject}) and was not run.", $"Den hentede fil er ikke signeret af Microsoft ({subject}) og blev ikke kørt."));
        }

        /// <summary>Runs the runtime installer silently with administrator rights. Returns true on success.</summary>
        public static Task<bool> InstallAsync(string installerPath)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                try
                {
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        // 0 = OK, 3010 = OK but a restart is recommended, 1638 = a newer version already exists
                        return p.ExitCode == 0 || p.ExitCode == 3010 || p.ExitCode == 1638;
                    }
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    throw new OperationCanceledException(L.T("Administrator rights were refused.", "Administratorrettigheder blev afvist."));
                }
            });
        }

        private static class WinTrust
        {
            private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WINTRUST_FILE_INFO
            {
                public uint cbStruct;
                public string pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct WINTRUST_DATA
            {
                public uint cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint dwUIChoice;
                public uint fdwRevocationChecks;
                public uint dwUnionChoice;
                public IntPtr pFile;
                public uint dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint dwProvFlags;
                public uint dwUIContext;
                public IntPtr pSignatureSettings;
            }

            [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
            private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

            public static bool IsTrusted(string path)
            {
                var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = path };
                var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(file));
                var dataPtr = IntPtr.Zero;
                try
                {
                    Marshal.StructureToPtr(file, filePtr, false);
                    var data = new WINTRUST_DATA
                    {
                        cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                        dwUIChoice = 2,          // WTD_UI_NONE
                        fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                        dwUnionChoice = 1,       // WTD_CHOICE_FILE
                        pFile = filePtr,
                        dwStateAction = 0,
                        dwProvFlags = 0x40       // WTD_SAFER_FLAG
                    };
                    dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(data));
                    Marshal.StructureToPtr(data, dataPtr, false);
                    return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, dataPtr) == 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(filePtr);
                    if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
                }
            }
        }
    }
}
