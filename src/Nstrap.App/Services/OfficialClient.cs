using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nstrap.Core;

namespace Nstrap.App.Services;

public static class OfficialClient
{
    private const uint ProcessQueryLimitedInformation = 0x1000, ProcessTerminate = 0x0001, ProcessSynchronize = 0x00100000;
    private static readonly Uri InstallerUri = new("https://setup.rbxcdn.com/RobloxPlayerInstaller.exe");
    public static bool IsRunning(ClientKind kind)
    {
        var processes = Process.GetProcessesByName(kind == ClientKind.Player ? "RobloxPlayerBeta" : "RobloxStudioBeta");
        bool running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return running;
    }

    public static bool IsPlayerRunningForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Windows could not identify the account for the FPS settings.");
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // WTS supplies the primary-token SID without opening another user's token.
            // Do not filter by session: this account's FPS settings are shared across sessions.
            if (!WTSEnumerateProcesses(IntPtr.Zero, 0, 1, out buffer, out int count))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not check Player sessions for your account. The FPS change remains pending.");
            if (count < 0 || (count > 0 && buffer == IntPtr.Zero))
                throw new IOException("Windows returned an invalid Player session list. The FPS change remains pending.");
            int size = Marshal.SizeOf<WtsProcessInfo>();
            for (int index = 0; index < count; index++)
            {
                var process = Marshal.PtrToStructure<WtsProcessInfo>(IntPtr.Add(buffer, checked(index * size)));
                if (!string.Equals(Marshal.PtrToStringUni(process.ProcessName), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (process.UserSid == IntPtr.Zero)
                    throw new IOException("Windows could not identify the account for a Player session. The FPS change remains pending.");
                if (user.Equals(new SecurityIdentifier(process.UserSid))) return true;
            }
            return false;
        }
        finally { if (buffer != IntPtr.Zero) WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsProcessInfo
    {
        public uint SessionId, ProcessId;
        public IntPtr ProcessName, UserSid;
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesW", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateProcesses(IntPtr server, uint reserved, uint version, out IntPtr processes, out int count);
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr memory);

    public static async Task ClosePlayerClientsAsync()
    {
        using var ownProcess = Process.GetCurrentProcess();
        int session = ownProcess.SessionId;
        var candidates = Process.GetProcessesByName("RobloxPlayerBeta");
        var clients = new List<SafeProcessHandle>();
        try
        {
            // Identify every target before terminating any of them. Exact executable
            // name, Windows session, and Roblox signature exclude unrelated processes.
            foreach (var process in candidates)
            {
                SafeProcessHandle? handle = null;
                bool retained = false;
                try
                {
                    if (!IsLiveInSession(process, session)) continue;
                    // Do not use Process.SafeHandle/MainModule: those can request
                    // ALL_ACCESS or VM_READ, which protected clients need not allow.
                    handle = OpenPlayerProcess(ProcessQueryLimitedInformation | ProcessTerminate | ProcessSynchronize, false, (uint)process.Id);
                    int openError = Marshal.GetLastWin32Error();
                    if (handle.IsInvalid)
                    {
                        if (openError == 87 || HasExited(process)) continue; // PID disappeared.
                        throw new Win32Exception(openError);
                    }
                    if (PinnedProcessExited(handle)) continue;
                    // Refresh session data after pinning. If the process exits during
                    // the lookup, the handle check prevents acting on a reused PID.
                    process.Refresh();
                    if (process.SessionId != session || PinnedProcessExited(handle)) continue;
                    var name = new StringBuilder(32768);
                    uint length = (uint)name.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, name, ref length))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var path = name.ToString();
                    if (!string.Equals(Path.GetFileName(path), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A running process could not be identified as Roblox Player. Close it manually and try Launch again.");
                    using var pinned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    VerifyPublisher(path);
                    clients.Add(handle);
                    retained = true;
                }
                catch (Exception ex) when ((ex is InvalidOperationException or Win32Exception or IOException)
                    && handle is { IsInvalid: false } && PinnedProcessExited(handle))
                {
                    // The client exited between enumeration and inspection.
                }
                finally { if (!retained) handle?.Dispose(); }
            }

            var elapsed = Stopwatch.StartNew();
            foreach (var handle in clients)
            {
                if (PinnedProcessExited(handle)) continue;
                if (!TerminatePlayerProcess(handle, 1))
                {
                    int error = Marshal.GetLastWin32Error();
                    // TerminateProcess can report access denied for an exited process.
                    if (!PinnedProcessExited(handle)) throw new Win32Exception(error);
                }
            }
            while (clients.Any(handle => !PinnedProcessExited(handle)))
            {
                var remaining = TimeSpan.FromSeconds(20) - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new IOException("Roblox Player did not exit within 20 seconds. Close it in Task Manager, then click Launch Roblox again. No new client was started.");
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
            if (HasPlayerInCurrentSession())
                throw new IOException("A Roblox Player session is still running or started again. Close it, then click Launch Roblox again. No new client was started.");
        }
        catch (Win32Exception ex)
        {
            throw new IOException("Windows could not close a Roblox Player process. Close it in Task Manager, then click Launch Roblox again. No new client was started.", ex);
        }
        finally
        {
            foreach (var handle in clients) handle.Dispose();
            foreach (var process in candidates) process.Dispose();
        }
    }

    private static bool PinnedProcessExited(SafeProcessHandle handle)
    {
        uint result = WaitForPlayerProcess(handle, 0);
        return result switch
        {
            0 => true, // WAIT_OBJECT_0
            0x102 => false, // WAIT_TIMEOUT
            0xFFFFFFFF => throw new Win32Exception(Marshal.GetLastWin32Error()),
            _ => throw new IOException("Windows returned an unexpected process wait result.")
        };
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true, ExactSpelling = true)]
    private static extern SafeProcessHandle OpenPlayerProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminatePlayerProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true, ExactSpelling = true)]
    private static extern uint WaitForPlayerProcess(SafeProcessHandle process, uint milliseconds);

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static bool HasPlayerInCurrentSession()
    {
        using var ownProcess = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("RobloxPlayerBeta");
        try { return processes.Any(process => IsLiveInSession(process, ownProcess.SessionId)); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool IsLiveInSession(Process process, int session)
    {
        try { return !HasExited(process) && process.SessionId == session; }
        catch (Exception ex) when ((ex is InvalidOperationException or Win32Exception) && HasExited(process)) { return false; }
    }

    public static void Launch(RobloxInstallation installation)
    {
        if (!File.Exists(installation.ExecutablePath))
            throw new FileNotFoundException("This Roblox installation was moved or updated. Refresh installations.");
        using var pinned = new FileStream(installation.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        VerifyPublisher(installation.ExecutablePath);
        if (HasPlayerInCurrentSession())
            throw new IOException("A Roblox Player session started while launch was being prepared. Click Launch Roblox again to close it and start with the applied settings.");
        var start = new ProcessStartInfo(installation.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = installation.DirectoryPath
        };
        if (installation.Kind == ClientKind.Player) start.ArgumentList.Add("--app");
        using var process = Process.Start(start) ?? throw new IOException("Windows could not start Roblox.");
    }

    public static async Task InstallPlayerAsync(string dataDirectory, IProgress<string> progress)
    {
        var directory = Path.Combine(dataDirectory, "downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "RobloxPlayerInstaller.exe");
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(4) };
            progress.Report("Downloading the official Roblox installer…");
            using var response = await client.GetAsync(InstallerUri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            const long maximum = 64L * 1024 * 1024;
            long? total = response.Content.Headers.ContentLength;
            if (total > maximum) throw new IOException("The official installer exceeds the expected 64 MB download limit.");
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                while (true)
                {
                    int read = await source.ReadAsync(buffer, timeout.Token);
                    if (read == 0) break;
                    received += read;
                    if (received > maximum) throw new IOException("The official installer exceeds the 64 MB download limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    progress.Report(total > 0
                        ? $"Downloading official installer · {received * 100 / total}%"
                        : $"Downloading official installer · {received / 1024:N0} KB");
                }
            }
            progress.Report("Verifying Windows signature and Roblox publisher…");
            using var pinned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await Task.Run(() => VerifyPublisher(path));
            progress.Report("The official Roblox installer is running. Follow its prompts…");
            using var installer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = directory })
                ?? throw new IOException("Windows could not start the Roblox installer.");
            await installer.WaitForExitAsync();
            if (installer.ExitCode != 0) throw new IOException($"The official installer exited with code {installer.ExitCode}. Roblox may show more details in its own installer.");
        }
        finally
        {
            // Delete only our fixed download file and now-empty unique directory.
            try { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void OpenWeb(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only HTTPS links may be opened.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static void VerifyPublisher(string path)
    {
        var file = new WinTrustFile
        {
            Size = (uint)Marshal.SizeOf<WinTrustFile>(),
            Path = path
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(),
            UIChoice = 2, RevocationChecks = 1, UnionChoice = 1, File = pointer,
            StateAction = 1, ProviderFlags = 0x80
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0)
                throw new InvalidDataException($"Windows could not verify this executable's trusted signature (0x{status:X8}). Check your connection and Windows certificates; the executable was not started.");
#pragma warning disable SYSLIB0057
            using var signed = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(signed.GetRawCertData());
            if (!string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), "Roblox Corporation", StringComparison.Ordinal))
                throw new InvalidDataException("The executable is not signed by Roblox Corporation. It was not started.");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<WinTrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr window, [In] ref Guid action, ref WinTrustData data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UIChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UIContext;
    }
}
