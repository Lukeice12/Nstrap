using System.Text.Json;
using Nstrap.App.Services;
using Nstrap.Core;

internal static class ScopedToggleCheck
{
    // Coordinator-only acceptance. No profile/config file is read or written.
    internal static int Run(string image, string output)
    {
        if (!Path.IsPathFullyQualified(image) || !File.Exists(image) || !Path.GetFileName(image).Equals("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(output) || !Path.GetPathRoot(output)!.Equals(@"J:\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Requires an absolute Player image and a J: output directory.");
        var installation = new RobloxInstallation(Path.GetDirectoryName(Path.GetFullPath(image))!, Path.GetFullPath(image), ClientKind.Player, DateTime.UtcNow);
        var profile = new FlagProfile { Flags = [new() { Name = "DFIntTaskSchedulerTargetFps", Value = "0" }, new() { Name = "DFIntS2PhysicsSenderRate", Value = "0" }] };
        var service = new RuntimeFlagService(output);
        var initial = service.ReadLiveFlags(installation, profile);
        Console.WriteLine(JsonSerializer.Serialize(initial, RuntimeFlagStore.Json));
        if (initial.Session is null || initial.Flags.Any(flag => !flag.CanToggle || flag.IsApplied))
            throw new IOException("Both scoped test flags must be available and not already owned in this receipt store.");
        foreach (var flag in profile.Flags) flag.Value = initial.Flags.Single(row => row.Name == flag.Name).CurrentValue;
        int result = 1;
        try
        {
            foreach (var flag in profile.Flags)
            {
                var report = service.SetLiveFlag(installation, flag, true, initial.Session, requireSameValue: true);
                Console.WriteLine(JsonSerializer.Serialize(report, RuntimeFlagStore.Json));
                if (report.Transaction?.AllWritesReadBack != true) throw new IOException("Same-value scoped On was refused.");
            }
            var offA = service.SetLiveFlag(installation, profile.Flags[0], false, initial.Session);
            Console.WriteLine(JsonSerializer.Serialize(offA, RuntimeFlagStore.Json));
            var middle = service.ReadLiveFlags(installation, profile);
            if (middle.Session != initial.Session || middle.Flags[0].IsApplied || !middle.Flags[1].IsApplied
                || middle.Flags.Where((flag, index) => flag.CurrentValue != initial.Flags[index].CurrentValue).Any())
                throw new IOException("Scoped Off A did not preserve B ownership and both original values.");
            Console.WriteLine("PASS scoped Off A preserved B ownership and both captured values.");
            result = 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); }
        finally
        {
            foreach (var flag in profile.Flags)
            {
                var report = service.SetLiveFlag(installation, flag, false, initial.Session);
                Console.WriteLine(JsonSerializer.Serialize(report, RuntimeFlagStore.Json));
                if (report.RequiresAttention) result = 1;
            }
            var final = service.ReadLiveFlags(installation, profile);
            Console.WriteLine(JsonSerializer.Serialize(final, RuntimeFlagStore.Json));
            if (final.Session != initial.Session || final.Flags.Any(flag => flag.IsApplied)
                || final.Flags.Where((flag, index) => flag.CurrentValue != initial.Flags[index].CurrentValue).Any()) result = 1;
        }
        return result;
    }
}
