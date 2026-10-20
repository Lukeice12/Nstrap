using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Nstrap.Core;

namespace Nstrap.App.Services;

public static class OfficialClient
{
    private static readonly Uri InstallerUri = new("https://setup.rbxcdn.com/RobloxPlayerInstaller.exe");
    public static bool IsRunning(ClientKind kind)
    {
        var processes = Process.GetProcessesByName(kind == ClientKind.Player ? "RobloxPlayerBeta" : "RobloxStudioBeta");
        bool running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return running;
    }

    public static void EnsureClosed(ClientKind kind)
    {
        if (IsRunning(kind))
            throw new InvalidOperationException($"Close Roblox {kind} before changing its flags. Settings are read when the client starts.");
    }

    public static void Launch(RobloxInstallation installation)
    {
        if (!File.Exists(installation.ExecutablePath))
            throw new FileNotFoundException("This Roblox installation was moved or updated. Refresh installations.");
        using var pinned = new FileStream(installation.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        VerifyPublisher(installation.ExecutablePath);
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
