using System.IO;
using Microsoft.Win32;

namespace Nstrap.App.Services;

/// <summary>
/// An ordinary, optional current-user Run entry. Its exact command is the
/// ownership boundary; a different value is never overwritten or removed.
/// Windows Startup Apps remains free to disable it through StartupApproved.
/// </summary>
internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nstrap";
    private static readonly object Gate = new();

    public static bool IsEnabled
    {
        get
        {
            lock (Gate)
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
                return Matches(Read(key), CommandFor(Environment.ProcessPath!));
            }
        }
    }

    public static StartupChange SetEnabled(bool enabled)
    {
        UpdateHandoff.RequireUnelevated();
        if (enabled) RequirePortableExecutable();
        string command = CommandFor(Environment.ProcessPath!);
        if (enabled) RequireSupportedLength(command);
        lock (Gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            var previous = Read(key);
            if (enabled)
            {
                if (previous.Exists && !Matches(previous, command))
                    throw new IOException("The Nstrap startup entry points to another command. It was preserved. Review Windows Startup settings before enabling this copy.");
                if (Matches(previous, command)) return StartupChange.Unchanged();
                using var writable = key is null ? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true) : null;
                (key ?? writable!).SetValue(ValueName, command, RegistryValueKind.String);
                return new StartupChange(previous, new(true, RegistryValueKind.String, command));
            }
            // A missing or externally changed value is already outside this
            // copy's ownership. Turning our preference off preserves that value.
            if (!Matches(previous, command)) return StartupChange.Unchanged();
            RequirePortableExecutable();
            key!.DeleteValue(ValueName, throwOnMissingValue: false);
            return new StartupChange(previous, Snapshot.Missing);
        }
    }

    internal static void RepairAfterRename(string source, string target)
    {
        UpdateHandoff.RequireUnelevated();
        if (!string.Equals(target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Startup registration can only follow the verified running executable.");
        string oldCommand = CommandFor(source);
        lock (Gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (!Matches(Read(key), oldCommand)) return;
            string replacement = CommandFor(target);
            RequireSupportedLength(replacement);
            key!.SetValue(ValueName, replacement, RegistryValueKind.String);
        }
    }

    internal static void RemoveOwned(string authenticatedTarget)
    {
        UpdateHandoff.RequireUnelevated();
        string command = CommandFor(authenticatedTarget);
        lock (Gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (Matches(Read(key), command)) key!.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string CommandFor(string executable)
    {
        UpdateHandoff.RequireRegularPath(executable);
        if (!string.Equals(executable, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
            || !AppIdentity.IsExecutableName(Path.GetFileName(executable)))
            throw new IOException("Startup registration requires an ordinary local executable path.");
        return "\"" + executable + "\" --background";
    }

    private static void RequireSupportedLength(string command)
    {
        if (command.Length > 260)
            throw new IOException("This executable path is too long for Windows sign-in startup. Move the portable app to a shorter local path before enabling it.");
    }

    private static void RequirePortableExecutable()
    {
        // An armed removal session already authenticated and pinned this exact
        // published executable. Opening another ordinary read handle would
        // conflict with its DELETE access, so reuse that live validation proof.
        if (!EmergencyActions.IsNukePrepared) UpdateHandoff.RequireInstallable();
    }

    private static Snapshot Read(RegistryKey? key)
    {
        if (key is null || !key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase))
            return Snapshot.Missing;
        // Unknown registry types can return null from GetValue; they are still
        // existing foreign values, never permission to replace the entry.
        var kind = key.GetValueKind(ValueName);
        object? value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new(true, kind, value);
    }

    private static bool Matches(Snapshot value, string command)
    {
        const string suffix = "\" --background";
        if (!value.Exists || value.Kind != RegistryValueKind.String || value.Value is not string actual
            || actual.Length > 260 || actual.Length <= suffix.Length + 1 || actual[0] != '"'
            || !actual.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string path = actual[1..^suffix.Length];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.IndexOf('"') >= 0) return false;
        try
        {
            // Windows path casing may change between launches. Arguments and
            // quoting stay exact; only the canonical executable path is folded.
            return string.Equals(Path.GetFullPath(path), command[1..^suffix.Length], StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static bool Same(Snapshot left, Snapshot right) =>
        left.Exists == right.Exists && (!left.Exists || left.Kind == right.Kind && Equals(left.Value, right.Value));

    internal sealed record Snapshot(bool Exists, RegistryValueKind Kind, object? Value)
    {
        internal static Snapshot Missing { get; } = new(false, RegistryValueKind.None, null);
    }

    /// <summary>
    /// UI commits this lease only after its profile save succeeds. Rollback
    /// compares the exact value written, preserving subsequent external edits.
    /// Registry and profile storage do not support a shared atomic transaction.
    /// </summary>
    internal sealed class StartupChange : IDisposable
    {
        private readonly Snapshot? _previous;
        private readonly Snapshot? _written;
        private bool _finished;
        internal StartupChange(Snapshot previous, Snapshot written) { _previous = previous; _written = written; }
        private StartupChange() { _finished = true; }
        internal static StartupChange Unchanged() => new();
        public void Commit() => _finished = true;
        public void Dispose()
        {
            if (_finished) return;
            _finished = true;
            lock (Gate)
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                if (!Same(Read(key), _written!)) return;
                if (_previous!.Exists)
                {
                    using var writable = key is null ? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true) : null;
                    (key ?? writable!).SetValue(ValueName, _previous.Value!, _previous.Kind);
                }
                else key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
