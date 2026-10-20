using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nstrap.App.Services;

/// <summary>
/// A same-user, same-session endpoint with one fixed operation: reveal the
/// existing window. No command execution, settings mutation or path input.
/// </summary>
internal sealed class InstanceReveal : IDisposable
{
    private static readonly byte[] Request = "Nstrap.Reveal.v1\n"u8.ToArray();
    private readonly NamedPipeServerStream _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receive;
    private readonly uint _sessionId;
    private int _disposed;

    private InstanceReveal(Action reveal)
    {
        using var process = Process.GetCurrentProcess();
        _sessionId = checked((uint)process.SessionId);
        _server = new NamedPipeServerStream(PipeName(), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
            128, 128);
        _receive = ReceiveAsync(reveal);
    }

    // The caller owns marshaling this callback onto its WPF Dispatcher.
    public static IDisposable StartReceiver(Action reveal)
    {
        ArgumentNullException.ThrowIfNull(reveal);
        return new InstanceReveal(reveal);
    }

    public static async Task<bool> TryRevealExistingAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var client = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(Request, timeout.Token).ConfigureAwait(false);
            byte[] answer = new byte[1];
            await client.ReadExactlyAsync(answer, timeout.Token).ConfigureAwait(false);
            return answer[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { return false; }
    }

    private async Task ReceiveAsync(Action reveal)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch { return; } // A broken listener stops; never spin on a failed pipe.
            bool disconnected = true;
            try
            {
                if (!GetNamedPipeClientSessionId(_server.SafePipeHandle, out uint clientSession)
                    || clientSession != _sessionId) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] request = new byte[Request.Length];
                await _server.ReadExactlyAsync(request, timeout.Token).ConfigureAwait(false);
                if (request.AsSpan().SequenceEqual(Request))
                {
                    reveal();
                    await _server.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
            { if (_stop.IsCancellationRequested) return; }
            catch { /* A reveal callback must not fault the background receiver. */ }
            finally
            {
                try { if (_server.IsConnected) _server.Disconnect(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { disconnected = false; }
            }
            if (!disconnected) return;
        }
    }

    private static string PipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string sid = identity.User?.Value ?? throw new IOException("Windows could not identify this user.");
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nstrap");
        string instance = App.InstanceMutexName(data);
        string key = sid + "|" + process.SessionId + "|" + instance;
        return "Nstrap-Reveal-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _server.Dispose();
        // Do not block a UI dispatcher while an already queued reveal is being
        // delivered. The receiver exits on cancellation and owns its CTS cleanup.
        _ = _receive.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);
}
