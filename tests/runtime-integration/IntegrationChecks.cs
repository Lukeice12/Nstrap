using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nstrap.App.Services;
using Nstrap.Core;

internal static class IntegrationChecks
{
    private const string Name = "DFIntTaskSchedulerTargetFps";
    internal static int Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Embedded map has162 unique reviewed entries", () => { var m = RuntimeFlagMap.Load(); Equal(162, m.Descriptor.Locations.Count); Equal(162, m.Descriptor.Locations.Select(x => x.Name).Distinct().Count()); }),
            ("Partial eligible profile reports exact live counts", PartialProfile),
            ("Build mismatch produces zero writes", BuildMismatch),
            ("Repeat apply retains original baseline across service instances", RepeatApply),
            ("Restore preserves observed external changes", ExternalChange),
            ("Process lifetime change has no restore ownership", LifetimeChange),
            ("Pending journal does not prove ownership", Pending),
            ("Journal callback change cannot become a new authorized snapshot", JournalRace),
            ("Removed or disabled runtime flags restore owned originals", Removed),
            ("Active unverified runtime flag remains unchanged", Unverified),
            ("Restore uses name anchors without consumer code", RestoreWithoutCode),
            ("Edited ownership receipt prevents runtime writes", EditedReceipt),
            ("Exclusive process lease prevents another apply", Lease),
            ("No exact running Player causes zero writes", NoProcess),
            ("Launch readiness applies only once", ReadyOnce),
            ("Readiness timeout makes no JSON claim or memory writes", ReadyTimeout),
            ("Cancelled launch readiness makes no memory writes", ReadyCancelled),
            ("Failed final receipt leaves ambiguous pending values untouched", FinalReceiptFailure),
            ("External intended bytes survive rejection before native write", ExternalIntendedBeforeWrite),
            ("Native write attempts are tracked independently per flag", PerFlagNativeAttempts),
            ("Partial native attempt permits verified rollback", PartialAttemptRollback),
            ("Ambiguous partial native bytes remain untouched", PartialAttemptConflict),
            ("Registry-only and dual-proof unreadable-code verification", RegistryProof),
            ("Consumer-only entries cannot use registry fallback", ConsumerOnly),
            ("Malformed schema/proof/type/range metadata is rejected", InvalidProof),
            ("Changed name/pointer/length and Boolean domain reject", ChangedAnchors),
            ("Full212 profile inspects/applies162 and reports50 specific skips", FullProfile),
            ("Live refresh is read-only and separate from profile Enabled", LiveRefresh),
            ("Scoped on/off preserves the other flag's value and ownership", ScopedIndependence),
            ("Scoped repeated apply retains the first baseline", ScopedBaseline),
            ("Scoped conflicts/pending/unsupported flags make no writes", ScopedRefusals),
            ("Scoped stale process status cannot target a new lifetime", ScopedLifetime),
            ("Live display distinguishes an earlier applied profile value", LivePreviousValue),
            ("Scoped Off ignores invalid saved value and preserves other ownership", ScopedInvalidOff),
            ("Same-value scoped acceptance refuses different prepared bytes", ScopedSameValue),
            ("Explicit absolute runtime directory wins without probing legacy storage", RuntimeDirectoryOverride),
            ("Existing legacy runtime store retains update ownership", RuntimeDirectoryLegacy),
            ("Public runtime store falls back to local app data with absolute paths", RuntimeDirectoryFallback)
        };
        int failed = 0;
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} app integration checks passed (fake target memory only).");
        return failed == 0 ? 0 : 1;
    }
    private static void RuntimeDirectoryOverride()
    {
        Equal(@"J:\release-check\state", RuntimeFlagStore.ResolveDefaultDirectory(@"J:\release-check\state", @"C:\UnusedLocalAppData", _ => throw new Exception("Explicit override must win")));
        Reject(() => RuntimeFlagStore.ResolveDefaultDirectory("relative-state", @"C:\UnusedLocalAppData", _ => true));
        Reject(() => new RuntimeFlagStore("relative-state"));
    }
    private static void RuntimeDirectoryLegacy()
    {
        string? probed = null;
        Equal(@"J:\SKILLHELPER\nstrap-runtime-state", RuntimeFlagStore.ResolveDefaultDirectory(null, @"C:\UnusedLocalAppData", path => { probed = path; return true; }));
        Equal(@"J:\SKILLHELPER\nstrap-runtime-state", probed);
    }
    private static void RuntimeDirectoryFallback()
    {
        const string expected = @"C:\UnusedLocalAppData\Nstrap\runtime-state";
        Equal(expected, RuntimeFlagStore.ResolveDefaultDirectory(null, @"C:\UnusedLocalAppData", _ => false));
        Equal(expected, new RuntimeFlagStore(expected).DirectoryPath); // Constructor resolves only; no directory is created.
        Reject(() => RuntimeFlagStore.ResolveDefaultDirectory(null, "relative", _ => false));
    }
    private static void PartialProfile()
    {
        using var f = new Fixture();
        var profile = Profile(30); profile.Flags.Add(new() { Name = "FFlagNoReviewedMapping", Value = "true" });
        profile.Flags.Add(new() { Name = "FStringUnsupported", Value = "x", Enabled = false });
        var report = f.Service().Apply(f.Installation, profile);
        Check(report.Summary.StartsWith("Live values verified: 1/2;"), report.Summary);
        Equal(1, report.Flags.Count(x => x.Status == nameof(RuntimeFlagState.AppliedReadBack)));
        Equal(1, report.Flags.Count(x => x.Status == "Skipped")); Equal(1, report.Flags.Count(x => x.Status == "Disabled"));
        Equal(30, f.Value);
    }
    private static void BuildMismatch()
    {
        using var f = new Fixture();
        f.Memory.Session = f.Memory.Session with { ExecutableSha256 = new string('B', 64) };
        var report = f.Service().Apply(f.Installation, Profile(30));
        Check(report.Transaction is null, "unexpected transaction"); Equal(0, f.Memory.Writes);
    }
    private static void RepeatApply()
    {
        using var f = new Fixture();
        Check(f.Service().Apply(f.Installation, Profile(30)).Transaction!.AllWritesReadBack, "first apply failed");
        Check(f.Service().Apply(f.Installation, Profile(60)).Transaction!.AllWritesReadBack, "second apply failed");
        Equal(60, f.Value);
        var state = f.Store.Load(f.Installation.ExecutablePath, f.Memory.Identity, f.Map.Descriptor);
        Equal(0, BinaryPrimitives.ReadInt32LittleEndian(state.Flags.Single().Original));
        Check(f.Service().Restore(f.Installation).Flags.All(x => x.Status == "Restored"), "restore failed"); Equal(0, f.Value);
    }
    private static void ExternalChange()
    {
        using var f = new Fixture(); f.Service().Apply(f.Installation, Profile(30)); f.Value = 75;
        int writes = f.Memory.Writes; var report = f.Service().Restore(f.Installation);
        Check(report.Flags.Any(x => x.Status == "External change preserved"), "conflict not reported"); Equal(75, f.Value); Equal(writes, f.Memory.Writes);
    }
    private static void LifetimeChange()
    {
        using var f = new Fixture(); f.Service().Apply(f.Installation, Profile(30)); int writes = f.Memory.Writes;
        f.Memory.Session = f.Memory.Session with { ProcessSessionId = "a-new-process-lifetime" };
        f.Service().Restore(f.Installation); Equal(30, f.Value); Equal(writes, f.Memory.Writes);
    }
    private static void Pending()
    {
        using var f = new Fixture(); f.Value = 30;
        var state = new RuntimeOwnershipState(1, f.Installation.ExecutablePath, f.Memory.Identity,
            [new(Name, f.Rva, RuntimeFlagScalarType.Int32, Bytes(0), Bytes(0), Bytes(30))]);
        f.Store.Save(state);
        Check(f.Service().Restore(f.Installation).Flags.Any(x => x.Status == "Unresolved pending ownership"), "pending restored as owned");
        Equal(30, f.Value); Equal(0, f.Memory.Writes);
        f.Service().Apply(f.Installation, Profile(60)); Equal(0, f.Memory.Writes);
    }
    private static void JournalRace()
    {
        using var f = new Fixture(); var state = new RuntimeOwnershipState(1, f.Installation.ExecutablePath, f.Memory.Identity, []);
        int saves = 0;
        var applied = RuntimeFlagOwnership.Apply(f.Memory, f.Map.Descriptor, state, [new(Name, new RuntimeFlagValue.Int32(30))], _ => { if (++saves == 1) f.Value = 44; });
        Equal(0, f.Memory.Writes); Equal(44, f.Value); Check(applied.Transaction?.AllWritesReadBack == false, "journal race accepted");
    }
    private static void Removed()
    {
        foreach (bool disabled in new[] { false, true })
        {
            using var f = new Fixture(); f.Service().Apply(f.Installation, Profile(30));
            var profile = new FlagProfile(); if (disabled) { profile = Profile(30); profile.Flags[0].Enabled = false; }
            var report = f.Service().Apply(f.Installation, profile);
            Equal(0, f.Value); Check(report.Flags.Any(x => x.Status == "Restored"), "removed ownership not restored");
        }
    }
    private static void Unverified()
    {
        using var f = new Fixture(); f.Service().Apply(f.Installation, Profile(30)); int writes = f.Memory.Writes;
        f.Memory.Set(f.CodeRva, new byte[6]); // Readable mismatch must never fall back.
        var report = f.Service().Apply(f.Installation, Profile(60)); Equal(30, f.Value); Equal(writes, f.Memory.Writes);
        Check(report.Flags.Any(x => x.Status.StartsWith("Unverified")), "missing verification failure");
    }
    private static void RestoreWithoutCode()
    {
        using var f = new Fixture(); f.Service().Apply(f.Installation, Profile(30)); f.Memory.BlockedRva = f.CodeRva;
        var report = f.Service().Restore(f.Installation); Equal(0, f.Value); Check(report.Flags.All(x => x.Status == "Restored"), "restore required code");
    }
    private static void EditedReceipt()
    {
        using var f = new Fixture(); f.Service().Apply(f.Installation, Profile(30)); int writes = f.Memory.Writes;
        string path = Directory.GetFiles(f.DirectoryPath, "*.ownership.json").Single();
        string content = File.ReadAllText(path).Replace("\"Version\": 1", "\"Version\": 2"); File.WriteAllText(path, content);
        var report = f.Service().Apply(f.Installation, Profile(60)); Equal(writes, f.Memory.Writes); Check(report.Transaction is null, "tampered ownership accepted");
    }
    private static void Lease()
    {
        using var f = new Fixture(); using var lease = f.Store.Acquire(f.Memory.Identity);
        f.Service().Apply(f.Installation, Profile(30)); Equal(0, f.Memory.Writes);
    }
    private static void NoProcess()
    {
        using var f = new Fixture(); var service = new RuntimeFlagService(f.Map, f.Store, _ => [], (_, _, _) => f.Target);
        var report = service.Apply(f.Installation, Profile(30)); Equal(0, f.Memory.Writes); Check(report.ProcessId is null, "unexpected process");
    }
    private static void ReadyOnce()
    {
        using var f = new Fixture();
        var report = f.Service().ApplyWhenReadyAsync(f.Installation, Profile(30), TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult();
        Equal(1, f.Memory.Writes); Check(report.Transaction?.AllWritesReadBack == true, "ready write failed");
    }
    private static void ReadyTimeout()
    {
        using var f = new Fixture();
        var service = new RuntimeFlagService(f.Map, f.Store, _ => [], (_, _, _) => f.Target);
        var report = service.ApplyWhenReadyAsync(f.Installation, Profile(30), TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
        Equal(0, f.Memory.Writes); Check(!report.Summary.Contains("Settings", StringComparison.OrdinalIgnoreCase), "service claimed JSON result");
    }
    private static void ReadyCancelled()
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { f.Service().ApplyWhenReadyAsync(f.Installation, Profile(30), TimeSpan.FromSeconds(1), cancel.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Equal(0, f.Memory.Writes); }
    }
    private static void FinalReceiptFailure()
    {
        using var f = new Fixture(); var state = new RuntimeOwnershipState(1, f.Installation.ExecutablePath, f.Memory.Identity, []);
        RuntimeOwnershipState? pending = null; int saves = 0;
        var report = RuntimeFlagOwnership.Apply(f.Memory, f.Map.Descriptor, state, [new(Name, new RuntimeFlagValue.Int32(30))], receipt =>
        {
            if (++saves > 1) throw new IOException("Fake final receipt failure");
            pending = JsonSerializer.Deserialize<RuntimeOwnershipState>(JsonSerializer.Serialize(receipt))!;
        });
        Equal(30, f.Value); Check(report.Lines.Any(x => x.Status == "Unresolved pending ownership"), "receipt failure hidden");
        f.Store.Save(pending!); int writes = f.Memory.Writes;
        Check(f.Service().Restore(f.Installation).Flags.Any(x => x.Status == "Unresolved pending ownership"), "pending value restored");
        Equal(30, f.Value); Equal(writes, f.Memory.Writes);
    }
    private static void ExternalIntendedBeforeWrite()
    {
        using var f = new Fixture();
        var state = new RuntimeOwnershipState(1, f.Installation.ExecutablePath, f.Memory.Identity, []);
        int reads = 0;
        f.Memory.AfterRead = rva =>
        {
            // The third scalar read is the core's prewrite comparison. Change the
            // backing value after returning its old bytes, before the wrapper guard.
            if (rva == f.Rva && ++reads == 3) f.Value = 30;
        };
        var report = RuntimeFlagOwnership.Apply(f.Memory, f.Map.Descriptor, state,
            [new(Name, new RuntimeFlagValue.Int32(30))], _ => { });
        Check(report.Transaction?.AllWritesReadBack == false, "external change was accepted");
        Equal(0, f.Memory.Writes); Equal(30, f.Value);
        Check(report.Transaction!.Flags.Single().Detail.Contains("No native write was attempted", StringComparison.Ordinal),
            "rollback refusal did not explain lack of a native attempt");
    }
    private static void PerFlagNativeAttempts()
    {
        using var f = new Fixture();
        var other = f.Map.Descriptor.Locations.First(location => location.Name != Name && location.Type == RuntimeFlagScalarType.Int32);
        f.Memory.Set(other.RelativeVirtualAddress, Bytes(0));
        var state = new RuntimeOwnershipState(1, f.Installation.ExecutablePath, f.Memory.Identity, []);
        int secondReads = 0;
        f.Memory.AfterRead = rva =>
        {
            if (rva == other.RelativeVirtualAddress && ++secondReads == 3)
                f.Memory.Set(other.RelativeVirtualAddress, Bytes(30));
        };
        var report = RuntimeFlagOwnership.Apply(f.Memory, f.Map.Descriptor, state,
            [new(Name, new RuntimeFlagValue.Int32(30)), new(other.Name, new RuntimeFlagValue.Int32(30))], _ => { });
        Equal(2, f.Memory.Writes); Check(f.Memory.WriteAddresses.All(rva => rva == f.Rva), "a different flag's native attempt authorized this rollback");
        Equal(0, f.Value); Equal(30, BinaryPrimitives.ReadInt32LittleEndian(f.Memory.Get(other.RelativeVirtualAddress, 4)));
        Equal(RuntimeFlagState.RolledBack, report.Transaction!.Flags.Single(flag => flag.Name == Name).State);
        Equal(RuntimeFlagState.RollbackFailed, report.Transaction.Flags.Single(flag => flag.Name == other.Name).State);
    }
    private static void PartialAttemptRollback()
    {
        using var f = new Fixture();
        var state = new RuntimeOwnershipState(1, f.Installation.ExecutablePath, f.Memory.Identity, []);
        f.Memory.WriteOverride = (rva, source) =>
        {
            if (f.Memory.Writes != 1) return null;
            // A two-byte partial write happens to establish all four intended
            // bytes because the original/intended upper bytes are both zero.
            f.Memory.Set(rva, source[..2]); return 2;
        };
        var report = RuntimeFlagOwnership.Apply(f.Memory, f.Map.Descriptor, state,
            [new(Name, new RuntimeFlagValue.Int32(30))], _ => { });
        Equal(2, f.Memory.Writes); Equal(0, f.Value);
        Equal(RuntimeFlagState.RolledBack, report.Transaction!.Flags.Single().State);
        Equal(2, report.Transaction.Flags.Single().ReportedWrittenBytes);
    }
    private static void PartialAttemptConflict()
    {
        using var f = new Fixture();
        var state = new RuntimeOwnershipState(1, f.Installation.ExecutablePath, f.Memory.Identity, []);
        f.Memory.WriteOverride = (rva, source) => { f.Memory.Set(rva, source[..2]); return 2; };
        var report = RuntimeFlagOwnership.Apply(f.Memory, f.Map.Descriptor, state,
            [new(Name, new RuntimeFlagValue.Int32(0x12345678))], _ => { });
        Equal(1, f.Memory.Writes); Equal(0x5678, f.Value);
        Equal(RuntimeFlagState.RollbackConflict, report.Transaction!.Flags.Single().State);
    }
    private static JsonNode MapNode()
    {
        using var stream = typeof(RuntimeFlagMap).Assembly.GetManifestResourceStream("Nstrap.RuntimeFlags.ReviewedMap.json")!;
        return JsonNode.Parse(stream)!;
    }
    private static void RegistryProof()
    {
        using var f = new Fixture(); f.SeedAll();
        var root = MapNode(); var entry = root["Entries"]!.AsArray().First(item => item!["EvidenceKind"]!.GetValue<string>() == "CalibratedRegistryScalar")!;
        string name = entry["Name"]!.GetValue<string>();
        Check(f.Map.Verify(f.Memory, f.Target.ModuleBase, name) == "CalibratedRegistryScalar", "registry-only proof failed");
        f.Memory.BlockedRva = f.CodeRva;
        Check(f.Map.Verify(f.Memory, f.Target.ModuleBase, Name).Contains("consumer code unreadable"), "explicit dual proof did not permit unreadable-code fallback");
        Equal(0, f.Memory.Writes);
    }
    private static void ConsumerOnly()
    {
        using var f = new Fixture(); f.SeedAll();
        var entries = MapNode()["Entries"]!.AsArray().Where(item => item!["EvidenceKind"]!.GetValue<string>() == "ConsumerInstruction").ToArray();
        Equal(2, entries.Length);
        foreach (var entry in entries)
        {
            f.Memory.BlockedRva = entry!["ConsumerEvidence"]!["Rva"]!.GetValue<ulong>();
            Reject(() => f.Map.Verify(f.Memory, f.Target.ModuleBase, entry["Name"]!.GetValue<string>()));
        }
        Equal(0, f.Memory.Writes);
    }
    private static void InvalidProof()
    {
        Action<JsonNode>[] invalid =
        [
            root => root["SchemaVersion"] = 99,
            root => root["Entries"]![0]!["EvidenceKind"] = "UnknownProof",
            root => root["Entries"]![0]!["RegistryEvidence"]!["Approved"] = false,
            root => root["Entries"]![0]!["RegistryEvidence"]!["KeyDescriptorScalarChainVerified"] = false,
            root => root["Entries"]![0]!["RegistryEvidence"]!["CalibrationId"] = "missing",
            root => root["Entries"]![0]!["Width"] = 8,
            root => root["Entries"]![0]!["Rva"] = ulong.MaxValue,
            root => root["Entries"]![0]!["Type"] = "String",
            root => root["CalibrationGroups"]![0]!["Calibrators"]![0]!["Width"] = 4,
            root => root["Entries"]!.AsArray().First(item => item!["EvidenceKind"]!.GetValue<string>() == "ConsumerInstruction")!["EvidenceKind"] = "ConsumerInstructionAndCalibratedRegistryScalar"
        ];
        foreach (var edit in invalid) { var root = MapNode(); edit(root); Reject(() => _ = new RuntimeFlagMap(JsonSerializer.SerializeToElement(root))); }
    }
    private static void ChangedAnchors()
    {
        var entry = MapNode()["Entries"]!.AsArray().First(item => item!["Name"]!.GetValue<string>() == Name)!;
        foreach (string field in new[] { "NameStringRva", "NamePointerRva", "NameLengthRva" })
        {
            using var f = new Fixture(); f.Memory.Set(entry["StaticEvidence"]![field]!.GetValue<ulong>(), [0]);
            Reject(() => f.Map.Verify(f.Memory, f.Target.ModuleBase, Name)); Equal(0, f.Memory.Writes);
        }
        using var boolean = new Fixture(); boolean.SeedAll();
        var location = boolean.Map.Descriptor.Locations.First(item => item.Type == RuntimeFlagScalarType.Boolean);
        boolean.Memory.Set(location.RelativeVirtualAddress, [2]);
        Reject(() => boolean.Map.Verify(boolean.Memory, boolean.Target.ModuleBase, location.Name)); Equal(0, boolean.Memory.Writes);
    }
    private static void FullProfile()
    {
        using var f = new Fixture(); f.SeedAll();
        var profile = new FlagProfile();
        foreach (var location in f.Map.Descriptor.Locations)
            profile.Flags.Add(new() { Name = location.Name, Value = location.Type == RuntimeFlagScalarType.Boolean ? "true" : "73" });
        foreach (var entry in MapNode()["IneligibleEntries"]!.AsArray())
        {
            string name = entry!["Name"]!.GetValue<string>();
            profile.Flags.Add(new() { Name = name, Value = FlagValidator.GetTypeHint(name) switch { "Boolean" => "false", "String" => "fixture", _ => "7" } });
        }
        string original = JsonSerializer.Serialize(profile);
        var inspect = f.Service().Inspect(f.Installation, profile);
        Equal(0, f.Memory.Writes); Equal(162, inspect.Flags.Count(line => line.Status == "Verified")); Equal(50, inspect.Flags.Count(line => line.Status == "Skipped"));
        var applied = f.Service().Apply(f.Installation, profile);
        Equal(162, applied.Transaction!.Flags.Count(flag => flag.State == RuntimeFlagState.AppliedReadBack));
        Equal(50, applied.Flags.Count(line => line.Status == "Skipped")); Equal(162, f.Memory.Writes);
        foreach (string code in new[] { "RegistryKeyNotFound", "UnregisteredDescriptor", "WidthConflict", "UnsupportedString", "UnsupportedChannel" })
            Check(applied.Flags.Any(line => line.Detail.StartsWith(code + ":")), "missing exclusion reason " + code);
        Check(f.Service().Restore(f.Installation).Flags.All(line => line.Status == "Restored"), "full-profile restore failed");
        Equal(original, JsonSerializer.Serialize(profile));
    }
    private static void LiveRefresh()
    {
        using var f = new Fixture(); var profile = Profile(30); profile.Flags[0].Enabled = false;
        profile.Flags.Add(new() { Name = "FStringInstancesToPoolByName", Value = "keep" });
        string original = JsonSerializer.Serialize(profile);
        var snapshot = f.Service().ReadLiveFlags(f.Installation, profile);
        Check(snapshot.Session == f.Memory.Identity, "missing current session");
        var row = snapshot.Flags.Single(flag => flag.Name == Name);
        Check(!row.IsApplied && row.CanToggle && row.CurrentValue == "0", "profile Enabled was confused with live ownership");
        Check(snapshot.Flags.Single(flag => flag.Name != Name).Detail.StartsWith("UnsupportedString:"), "unsupported reason missing");
        Equal(0, f.Memory.Writes); Equal(original, JsonSerializer.Serialize(profile));
    }
    private static void ScopedIndependence()
    {
        using var f = new Fixture(); f.SeedAll();
        const string otherName = "DFIntS2PhysicsSenderRate";
        var other = f.Map.Descriptor.Locations.Single(flag => flag.Name == otherName);
        var a = Profile(30).Flags[0]; var b = new FlagEntry { Name = otherName, Value = "45" };
        var service = f.Service(); var session = f.Memory.Identity;
        Check(service.SetLiveFlag(f.Installation, a, true, session).Transaction?.AllWritesReadBack == true, "A on failed");
        Check(service.SetLiveFlag(f.Installation, b, true, session).Transaction?.AllWritesReadBack == true, "B on failed");
        Equal(30, f.Value); Equal(45, BinaryPrimitives.ReadInt32LittleEndian(f.Memory.Get(other.RelativeVirtualAddress, 4)));
        int before = f.Memory.WriteAddresses.Count;
        service.SetLiveFlag(f.Installation, a, false, session);
        Check(f.Memory.WriteAddresses.Skip(before).All(rva => rva == f.Rva), "off A wrote B");
        Equal(0, f.Value); Equal(45, BinaryPrimitives.ReadInt32LittleEndian(f.Memory.Get(other.RelativeVirtualAddress, 4)));
        var state = f.Store.Load(f.Installation.ExecutablePath, session, f.Map.Descriptor);
        Equal(otherName, state.Flags.Single().Name);
        var profile = new FlagProfile { Flags = [a, b] };
        var status = service.ReadLiveFlags(f.Installation, profile);
        Check(!status.Flags.Single(flag => flag.Name == Name).IsApplied && status.Flags.Single(flag => flag.Name == otherName).IsApplied, "independent status incorrect");
        service.SetLiveFlag(f.Installation, b, false, session);
        Equal(0, f.Store.Load(f.Installation.ExecutablePath, session, f.Map.Descriptor).Flags.Count);
    }
    private static void ScopedBaseline()
    {
        using var f = new Fixture(); f.Value = 11; var service = f.Service(); var session = f.Memory.Identity;
        service.SetLiveFlag(f.Installation, Profile(30).Flags[0], true, session);
        service.SetLiveFlag(f.Installation, Profile(60).Flags[0], true, session);
        Equal(60, f.Value);
        Equal(11, BinaryPrimitives.ReadInt32LittleEndian(f.Store.Load(f.Installation.ExecutablePath, session, f.Map.Descriptor).Flags.Single().Original));
        service.SetLiveFlag(f.Installation, Profile(60).Flags[0], false, session); Equal(11, f.Value);
    }
    private static void ScopedRefusals()
    {
        using var f = new Fixture(); var service = f.Service(); var session = f.Memory.Identity;
        service.SetLiveFlag(f.Installation, Profile(30).Flags[0], true, session); f.Value = 90; int writes = f.Memory.Writes;
        Check(service.SetLiveFlag(f.Installation, Profile(60).Flags[0], true, session).RequiresAttention, "conflict apply accepted");
        Check(service.SetLiveFlag(f.Installation, Profile(30).Flags[0], false, session).RequiresAttention, "conflict restore accepted");
        var row = service.ReadLiveFlags(f.Installation, Profile(30)).Flags.Single();
        Check(row.Status == "Conflict" && !row.CanToggle && !row.IsApplied, "conflict state incorrect");
        service.SetLiveFlag(f.Installation, new() { Name = "FStringInstancesToPoolByName", Value = "keep" }, true, session);
        Equal(writes, f.Memory.Writes); Equal(90, f.Value);
        var pending = new RuntimeOwnershipState(1, f.Installation.ExecutablePath, session, [new(Name, f.Rva, RuntimeFlagScalarType.Int32, Bytes(0), Bytes(0), Bytes(90))]);
        f.Store.Save(pending);
        service.SetLiveFlag(f.Installation, Profile(60).Flags[0], true, session);
        service.SetLiveFlag(f.Installation, Profile(60).Flags[0], false, session);
        row = service.ReadLiveFlags(f.Installation, Profile(60)).Flags.Single();
        Check(row.Status == "Pending review" && !row.CanToggle, "pending state not guarded"); Equal(writes, f.Memory.Writes);
    }
    private static void ScopedLifetime()
    {
        using var f = new Fixture(); var service = f.Service(); var old = service.ReadLiveFlags(f.Installation, Profile(30)).Session!;
        f.Memory.Session = old with { ProcessSessionId = "replacement-process" };
        Check(service.SetLiveFlag(f.Installation, Profile(30).Flags[0], true, old).RequiresAttention, "stale on accepted");
        Check(service.SetLiveFlag(f.Installation, Profile(30).Flags[0], false, old).RequiresAttention, "stale off accepted");
        Equal(0, f.Memory.Writes);
        var fresh = service.ReadLiveFlags(f.Installation, Profile(30));
        Check(fresh.Session == f.Memory.Identity && !fresh.Flags.Single().IsApplied, "new lifetime inherited ownership");
    }
    private static void LivePreviousValue()
    {
        using var f = new Fixture(); var service = f.Service();
        service.SetLiveFlag(f.Installation, Profile(30).Flags[0], true, f.Memory.Identity);
        var row = service.ReadLiveFlags(f.Installation, Profile(60)).Flags.Single();
        Check(row.IsApplied && row.Status == "On — previous value" && row.CurrentValue == "30" && row.SavedValue == "60", "different saved value was misrepresented");
    }
    private static void ScopedInvalidOff()
    {
        using var f = new Fixture(); f.SeedAll(); var service = f.Service(); var session = f.Memory.Identity;
        var other = new FlagEntry { Name = "DFIntS2PhysicsSenderRate", Value = "45" };
        service.SetLiveFlag(f.Installation, Profile(30).Flags[0], true, session);
        service.SetLiveFlag(f.Installation, other, true, session);
        var invalid = new FlagEntry { Name = Name, Value = "not-an-integer" };
        var profile = new FlagProfile { Flags = [invalid, other] };
        var row = service.ReadLiveFlags(f.Installation, profile).Flags.Single(item => item.Name == Name);
        Check(row.IsApplied && row.CanToggle, "invalid saved value disabled committed Off");
        int writes = f.Memory.Writes;
        Check(service.SetLiveFlag(f.Installation, invalid, true, session).RequiresAttention, "invalid On was reported as success"); Equal(writes, f.Memory.Writes);
        Check(!service.SetLiveFlag(f.Installation, invalid, false, session).RequiresAttention, "invalid saved value blocked Off");
        Equal(0, f.Value); Equal(other.Name, f.Store.Load(f.Installation.ExecutablePath, session, f.Map.Descriptor).Flags.Single().Name);
    }
    private static void ScopedSameValue()
    {
        using var f = new Fixture(); var service = f.Service(); var session = f.Memory.Identity;
        var refused = service.SetLiveFlag(f.Installation, Profile(30).Flags[0], true, session, requireSameValue: true);
        Check(refused.Transaction is null && refused.RequiresAttention, "different current value accepted or reported as success");
        Equal(0, f.Memory.Writes);
        Check(service.SetLiveFlag(f.Installation, Profile(0).Flags[0], true, session, requireSameValue: true).Transaction?.AllWritesReadBack == true, "same-value request failed");
        Equal(0, f.Value); Equal(1, f.Memory.Writes);
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is IOException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return; }
        throw new Exception("Expected proof rejection");
    }
    private static FlagProfile Profile(int value) => new() { Flags = [new() { Name = Name, Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) }] };
    private static byte[] Bytes(int value) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");

    private sealed class Fixture : IDisposable
    {
        internal RuntimeFlagMap Map = RuntimeFlagMap.Load();
        internal string DirectoryPath = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        internal RuntimeFlagStore Store;
        internal FakeMemory Memory;
        internal FakeTarget Target;
        internal RobloxInstallation Installation;
        internal ulong Rva, CodeRva;
        internal int Value { get => BinaryPrimitives.ReadInt32LittleEndian(Memory.Get(Rva, 4)); set => Memory.Set(Rva, Bytes(value)); }
        internal Fixture()
        {
            Store = new(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "RobloxPlayerBeta.exe");
            Installation = new(DirectoryPath, path, ClientKind.Player, DateTime.UtcNow);
            Memory = new(new("fixture-" + Guid.NewGuid().ToString("N"), Map.Descriptor.ExecutableSha256, Map.Descriptor.ModuleImageSize));
            Target = new(Memory, path);
            using var stream = typeof(RuntimeFlagMap).Assembly.GetManifestResourceStream("Nstrap.RuntimeFlags.ReviewedMap.json")!;
            using var doc = JsonDocument.Parse(stream);
            var entry = doc.RootElement.GetProperty("Entries").EnumerateArray().Single(x => x.GetProperty("Name").GetString() == Name);
            Rva = entry.GetProperty("Rva").GetUInt64(); CodeRva = entry.GetProperty("ConsumerEvidence").GetProperty("Rva").GetUInt64(); Value = 0;
            Seed(entry);
        }
        internal void SeedAll()
        {
            using var root = JsonDocument.Parse(MapNode().ToJsonString());
            foreach (var entry in root.RootElement.GetProperty("Entries").EnumerateArray()) Seed(entry);
        }
        private void Seed(JsonElement entry)
        {
            var anchors = entry.GetProperty("StaticEvidence");
            Memory.Set(entry.GetProperty("Rva").GetUInt64(), new byte[entry.GetProperty("Width").GetInt32()]);
            if (entry.TryGetProperty("ConsumerEvidence", out var code) && code.ValueKind != JsonValueKind.Null)
                Memory.Set(code.GetProperty("Rva").GetUInt64(), Convert.FromHexString(code.GetProperty("ExpectedBytesHex").GetString()!));
            ulong stringRva = anchors.GetProperty("NameStringRva").GetUInt64();
            Memory.Set(stringRva, Convert.FromHexString(anchors.GetProperty("ExpectedNameBytesHex").GetString()!));
            byte[] pointer = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(pointer, Target.ModuleBase + stringRva);
            Memory.Set(anchors.GetProperty("NamePointerRva").GetUInt64(), pointer);
            byte[] length = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(length, anchors.GetProperty("ExpectedNameLength").GetUInt64());
            Memory.Set(anchors.GetProperty("NameLengthRva").GetUInt64(), length);
        }
        internal RuntimeFlagService Service() => new(Map, Store, _ => [1234], (_, _, _) => Target);
        public void Dispose() { /* Retained J: receipts are useful for inspecting fake test evidence. */ }
    }
    private sealed class FakeTarget(FakeMemory memory, string path) : IRuntimeFlagTarget
    {
        public IRuntimeFlagMemory Memory => memory;
        public string ImagePath => path;
        public ulong ModuleBase => 0x10000000;
        public void Dispose() { }
    }
    private sealed class FakeMemory(RuntimeFlagSession session) : IRuntimeFlagMemory
    {
        private readonly Dictionary<ulong, byte> data = [];
        internal RuntimeFlagSession Session = session;
        internal ulong? BlockedRva;
        internal int Writes;
        internal Action<ulong>? AfterRead;
        internal Func<ulong, byte[], int?>? WriteOverride;
        internal List<ulong> WriteAddresses = [];
        public RuntimeFlagSession Identity => Session;
        internal void Set(ulong rva, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) data[rva + (ulong)i] = bytes[i]; }
        internal byte[] Get(ulong rva, int count) { byte[] bytes = new byte[count]; Read(rva, bytes); return bytes; }
        public int Read(ulong rva, Span<byte> destination)
        {
            if (rva == BlockedRva) throw new IOException("Fake unreadable consumer page.");
            for (int i = 0; i < destination.Length; i++) destination[i] = data.TryGetValue(rva + (ulong)i, out byte value) ? value : throw new IOException("Unmapped fake read.");
            AfterRead?.Invoke(rva);
            return destination.Length;
        }
        public int Write(ulong rva, ReadOnlySpan<byte> source)
        {
            Writes++; WriteAddresses.Add(rva);
            byte[] bytes = source.ToArray();
            if (WriteOverride?.Invoke(rva, bytes) is { } count) return count;
            Set(rva, bytes); return source.Length;
        }
    }
}
