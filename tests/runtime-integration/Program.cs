using System.Security.Cryptography;
using System.Text.Json;
using Nstrap.App.Services;
using Nstrap.Core;

if (args is ["--self-test"]) return IntegrationChecks.Run();
if (args is ["--scoped-same-values", var scopedImage, var scopedOutput]) return ScopedToggleCheck.Run(scopedImage, scopedOutput);
bool fpsOnly = args is ["--live-fps", _, _];
if (!fpsOnly && args is not ["--inspect-profile" or "--live-profile", _, _, _])
{
    Console.Error.WriteLine("Usage: --self-test | --inspect-profile <exe> <profiles.json> <J: output> | --live-profile <exe> <profiles.json> <J: output> | --live-fps <exe> <J: output>");
    return 2;
}
string mode = args[0], imagePath = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[fpsOnly ? 2 : 3]);
if (!Path.IsPathFullyQualified(args[1]) || !File.Exists(imagePath) || !Path.GetFileName(imagePath).Equals("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase)
    || !Path.IsPathFullyQualified(args[fpsOnly ? 2 : 3]) || !Path.GetPathRoot(output)!.Equals(@"J:\", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Requires an existing absolute Player image and an absolute J: output directory.");
var installation = new RobloxInstallation(Path.GetDirectoryName(imagePath)!, imagePath, ClientKind.Player, DateTime.UtcNow);
FlagProfile profile;
string? profilePath = null; byte[]? originalProfileHash = null;
if (fpsOnly) profile = new() { Name = "App service FPS acceptance", Flags = [new() { Name = "DFIntTaskSchedulerTargetFps", Value = "30" }] };
else
{
    profilePath = Path.GetFullPath(args[2]);
    if (!Path.IsPathFullyQualified(args[2]) || !Path.GetFileName(profilePath).Equals("profiles.json", StringComparison.OrdinalIgnoreCase) || !File.Exists(profilePath))
        throw new ArgumentException("An existing absolute profiles.json path is required.");
    originalProfileHash = SHA256.HashData(File.ReadAllBytes(profilePath));
    var state = new ProfileStore(Path.GetDirectoryName(profilePath)!).Load();
    profile = state.Profiles.Single(item => item.Id == state.SelectedProfileId).Copy("Selected profile acceptance");
    FlagValidator.Validate(profile.Flags);
}
var service = new RuntimeFlagService(output);
if (mode == "--inspect-profile")
{
    var report = service.Inspect(installation, profile);
    Console.WriteLine(JsonSerializer.Serialize(report, RuntimeFlagStore.Json));
    VerifyProfileUnchanged();
    return report.RequiresAttention || report.ProcessId is null ? 1 : 0;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
int result = 1; string[] appliedNames = [];
try
{
    var applied = service.Apply(installation, profile);
    Console.WriteLine(JsonSerializer.Serialize(applied, RuntimeFlagStore.Json));
    appliedNames = applied.Transaction?.Flags.Where(flag => flag.State == RuntimeFlagState.AppliedReadBack).Select(flag => flag.Name).ToArray() ?? [];
    if (applied.Transaction?.AllWritesReadBack != true || appliedNames.Length == 0)
        throw new IOException("The app service did not verify a runtime batch.");
    Console.WriteLine($"Verified {appliedNames.Length}/{profile.Flags.Count(flag => flag.Enabled)} live values; holding15 seconds before service Restore.");
    await Task.Delay(TimeSpan.FromSeconds(15), cancel.Token);
    result = 0;
}
catch (OperationCanceledException) { Console.WriteLine("Cancelled; restoring through app service."); }
catch (Exception error) { Console.Error.WriteLine(error.Message); }
finally
{
    var restored = service.Restore(installation);
    Console.WriteLine(JsonSerializer.Serialize(restored, RuntimeFlagStore.Json));
    if (restored.RequiresAttention || appliedNames.Any(name => !restored.Flags.Any(flag => flag.Name == name && flag.Status == "Restored"))) result = 1;
    VerifyProfileUnchanged();
}
return result;
void VerifyProfileUnchanged()
{
    if (profilePath is not null && !SHA256.HashData(File.ReadAllBytes(profilePath)).SequenceEqual(originalProfileHash!))
        throw new IOException("profiles.json changed during the run; investigate before further acceptance.");
}
