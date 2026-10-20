using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nstrap.Core;

var checks = new List<(string Name, Action Run)>
{
    ("Validation and JSON import", Validation),
    ("Validated file import and merge", FileImportMerge),
    ("Invalid import leaves originals unchanged", InvalidFileImport),
    ("Profile persistence", Persistence),
    ("Apply and restore owned keys", ApplyRestore),
    ("External edit conflict", ExternalConflict),
    ("Missing-file restore", MissingFileRestore),
    ("Original null and no-op restoration", NullAndNoOpRestore),
    ("Interrupted operation recovery", Recovery),
    ("Player and Studio discovery", Discovery)
};
if (args is ["--import-file", var samplePath])
    checks.Add(("Supplied file round-trip through fixture apply", () => SuppliedFileImport(samplePath)));
else if (args.Length != 0)
    throw new ArgumentException("Usage: Nstrap.Checks [--import-file path]");
int failures = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
Console.WriteLine($"{checks.Count - failures}/{checks.Count} checks passed");
return failures == 0 ? 0 : 1;

static void Validation()
{
    var arbitrary = new FlagEntry { Name = "FFlagMyUnlistedSwitch", Value = "true" };
    Check(!FlagCatalog.IsAllowed(arbitrary.Name), "fixture should be absent from informational catalog");
    Check(FlagValidator.Validate([arbitrary]).ContainsKey(arbitrary.Name), "unknown valid flag was filtered");
    Equal(JsonValueKind.True, FlagValidator.Value(arbitrary.Name, "TrUe").ValueKind);
    Equal(42, FlagValidator.Value("DFIntMyNumber", "+42").GetInt32());
    Equal("hello", FlagValidator.Value("FStringMyText", "hello").GetString());
    Equal(42, FlagValidator.Value("DFIntSomeFlagCount", "42").GetInt32());
    Equal(true, FlagValidator.Value("FFlagSomeStringName", "true").GetBoolean());
    Equal("value", FlagValidator.Value("FStringSomeFlagName", "value").GetString());
    Equal(2, FlagValidator.Value("FInt2026Option", "2").GetInt32());
    Throws<InvalidDataException>(() => FlagValidator.Value("bad name", "true"));
    Throws<InvalidDataException>(() => FlagValidator.Value("FFlagMySwitch", "yes"));
    Throws<InvalidDataException>(() => FlagValidator.Value("FIntMyNumber", "1.5"));
    Throws<InvalidDataException>(() => FlagValidator.Value("FIntMyNumber", "2147483648"));
    Throws<InvalidDataException>(() => FlagValidator.Validate([arbitrary, arbitrary.Copy()]));
    Check(FlagValidator.Validate([new FlagEntry { Name = "FFlagDisabled", Value = "false", Enabled = false }]).Count == 0,
        "disabled flag should be omitted");
    var imported = FlagValidator.Import("{\"FFlagMyUnlistedSwitch\":true,\"DFIntMyNumber\":2,\"FStringMyText\":\"x\"}");
    Equal(3, imported.Count);
    foreach (var json in new[]
    {
        "[1]", "null", "{", "{\"FFlagOne\":true,\"FFlagOne\":false}",
        "{\"FFlagOne\":null}", "{\"FFlagOne\":{}}", "{\"FFlagOne\":1}",
        "{\"DFIntOne\":1.5}", "{\"DFIntOne\":true}"
    }) ThrowsAny(() => FlagValidator.Import(json), $"invalid import accepted: {json}");
    Throws<InvalidDataException>(() => FlagValidator.ReadObject(new byte[FlagValidator.MaximumBytes + 1]));
}

static void Persistence()
{
    using var fixture = new Fixture();
    var store = new ProfileStore(fixture.Path("state"));
    var initial = store.Load();
    Equal(1, initial.Profiles.Count);
    var profile = initial.Profiles[0];
    profile.Name = "Custom";
    profile.Flags.Add(new FlagEntry { Name = "FFlagUnlisted", Value = "true", Enabled = false });
    initial.CustomRoots.Add(fixture.Path("custom"));
    initial.ApplyBeforeLaunch = false;
    store.Save(initial);
    var loaded = store.Load();
    Equal(profile.Id, loaded.SelectedProfileId);
    Equal("Custom", loaded.Profiles[0].Name);
    Equal("FFlagUnlisted", loaded.Profiles[0].Flags.Single().Name);
    Check(!loaded.Profiles[0].Flags.Single().Enabled, "enabled state was lost");
    Equal(fixture.Path("custom"), loaded.CustomRoots.Single());
    Check(!loaded.ApplyBeforeLaunch, "launch setting was lost");
    File.WriteAllText(store.StatePath, "{\"SchemaVersion\":2,\"Profiles\":[]}");
    Throws<InvalidDataException>(() => store.Load());
    const string emptyProfiles = "{\"SchemaVersion\":1,\"Profiles\":[]}";
    File.WriteAllText(store.StatePath, emptyProfiles);
    Throws<InvalidDataException>(() => store.Load());
    Equal(emptyProfiles, File.ReadAllText(store.StatePath));
    File.WriteAllText(store.StatePath, "{\"SchemaVersion\":1,\"Profiles\":[{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Flags\":[]},{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Flags\":[]}]}");
    Throws<InvalidDataException>(() => store.Load());
}

static void FileImportMerge()
{
    using var fixture = new Fixture();
    var profile = Profile("Merge", ("FFlagImportSwitch", "true"), ("FStringUnrelated", "keep"));
    profile.Flags[0].Enabled = false;
    var path = fixture.Path("flags.TXT");
    File.WriteAllText(path, """{"FFlagImportSwitch":"False","FLogImportLevel":"7","FStringImportValue":"null"}""");
    var plan = FlagFileImport.Read(path, profile.Flags);
    Equal(3, plan.ImportedCount);
    Equal(4, plan.MergedFlags.Count);
    Equal("False", plan.MergedFlags.Single(f => f.Name == "FFlagImportSwitch").Value);
    Check(plan.MergedFlags.Single(f => f.Name == "FFlagImportSwitch").Enabled, "import did not enable replaced flag");
    Equal("keep", plan.MergedFlags.Single(f => f.Name == "FStringUnrelated").Value);
    Equal("null", plan.MergedFlags.Single(f => f.Name == "FStringImportValue").Value);
    Equal(2, profile.Flags.Count);
    Equal("true", profile.Flags[0].Value);
    Check(!profile.Flags[0].Enabled, "planning mutated the input profile");
    var desired = FlagValidator.Validate(plan.MergedFlags);
    Equal(false, desired["FFlagImportSwitch"].GetBoolean());
    Equal(7, desired["FLogImportLevel"].GetInt32());
    Equal(JsonValueKind.String, desired["FStringImportValue"].ValueKind);
    Check(FlagFileImport.IsSupportedPath("flags.JSON"), "JSON extension was rejected");
    var stale = fixture.Installation("removed-client", ClientKind.Player);
    File.Delete(stale.ExecutablePath);
    Throws<IOException>(() => new SettingsApplier(fixture.Path("data")).Apply(stale, profile));
    Check(!Directory.Exists(Path.GetDirectoryName(stale.ConfigPath)), "apply recreated a stale installation's settings folder");
}

static void InvalidFileImport()
{
    using var fixture = new Fixture();
    var profile = Profile("Keep", ("FFlagOriginal", "true"));
    var store = new ProfileStore(fixture.Path("data"));
    var state = AppState.Create();
    state.Profiles[0] = profile;
    state.SelectedProfileId = profile.Id;
    store.Save(state);
    var original = File.ReadAllBytes(store.StatePath);
    foreach (var json in new[]
    {
        """{"FFlagOriginal":"False","FIntBroken":"NaN"}""",
        """{"FFlagOriginal":false,"FFlagOriginal":true}""",
        """[{"FFlagOriginal":false}]""",
        """{"FFlagOriginal":false,"FStringBroken":null}"""
    })
    {
        var path = fixture.Path("malformed.json");
        File.WriteAllText(path, json);
        ThrowsAny(() => FlagFileImport.Read(path, profile.Flags), "invalid file import accepted");
        Equal("true", profile.Flags.Single().Value);
        Check(original.SequenceEqual(File.ReadAllBytes(store.StatePath)), "invalid import changed persisted state");
    }
    var oversized = fixture.Path("oversized.txt");
    File.WriteAllBytes(oversized, new byte[FlagValidator.MaximumBytes + 1]);
    Throws<InvalidDataException>(() => FlagFileImport.Read(oversized, profile.Flags));
    Throws<InvalidDataException>(() => FlagFileImport.Read(fixture.Path("flags.exe"), profile.Flags));
}

static void SuppliedFileImport(string path)
{
    using var fixture = new Fixture();
    var plan = FlagFileImport.Read(path, []);
    var source = FlagValidator.ReadObject(File.ReadAllBytes(path));
    Equal(source.Count, plan.ImportedCount);
    foreach (var flag in plan.MergedFlags)
    {
        var value = source[flag.Name];
        Equal(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), flag.Value);
    }
    var profile = new FlagProfile { Name = "External sample fixture", Flags = new(plan.MergedFlags) };
    var installation = fixture.Installation("isolated-sample", ClientKind.Player);
    var applier = new SettingsApplier(fixture.Path("data"));
    var result = applier.Apply(installation, profile);
    Equal(source.Count, result.ManagedCount);
    var actual = ReadSettings(installation.ConfigPath);
    foreach (var (name, expected) in FlagValidator.Validate(plan.MergedFlags))
        Check(JsonElement.DeepEquals(expected, actual[name]), $"Value was not preserved for {name}");
    applier.Restore(installation);
    Check(!File.Exists(installation.ConfigPath), "fixture restore left settings behind");
    Console.WriteLine($"Verified {source.Count} supplied flags through isolated apply and restore.");
}

static void ApplyRestore()
{
    using var fixture = new Fixture();
    var installation = fixture.Installation("Player", ClientKind.Player);
    WriteSettings(installation.ConfigPath, "{\"FFlagOwned\":false,\"FFlagUnmanaged\":true,\"FIntExisting\":7}");
    var applier = new SettingsApplier(fixture.Path("app"));
    var first = applier.Apply(installation, Profile("One", ("FFlagOwned", "true"), ("FFlagNew", "true")));
    Check(first.BackupPath is not null && File.Exists(first.BackupPath), "first apply must back up existing bytes");
    Equal(2, first.ManagedCount);
    Equal(false, ReadSettings(first.BackupPath!)["FFlagOwned"].GetBoolean());
    var applied = ReadSettings(installation.ConfigPath);
    Check(applied["FFlagOwned"].GetBoolean() && applied["FFlagNew"].GetBoolean(), "managed values not applied");
    Check(applied["FFlagUnmanaged"].GetBoolean(), "unmanaged flag was lost");
    WriteSettings(installation.ConfigPath, "{\"FFlagOwned\":true,\"FFlagNew\":true,\"FFlagUnmanaged\":false,\"FIntExisting\":7,\"FStringAddedOutside\":\"keep\"}");
    var second = applier.Apply(installation, Profile("Two", ("FFlagNew", "false")));
    Equal(1, second.ManagedCount);
    var changed = ReadSettings(installation.ConfigPath);
    Check(!changed["FFlagOwned"].GetBoolean(), "removed managed flag did not regain original value");
    Check(!changed["FFlagUnmanaged"].GetBoolean(), "external unmanaged edit was lost");
    Equal("keep", changed["FStringAddedOutside"].GetString());
    applier.Restore(installation);
    var restored = ReadSettings(installation.ConfigPath);
    Check(!restored.ContainsKey("FFlagNew"), "new managed flag survived restore");
    Check(!restored["FFlagOwned"].GetBoolean(), "original managed value was not restored");
    Check(!restored["FFlagUnmanaged"].GetBoolean(), "unmanaged edit was lost during restore");
    Equal("keep", restored["FStringAddedOutside"].GetString());
}

static void ExternalConflict()
{
    using var fixture = new Fixture();
    var installation = fixture.Installation("Player", ClientKind.Player);
    WriteSettings(installation.ConfigPath, "{\"FFlagOwned\":false,\"FFlagOther\":true}");
    var applier = new SettingsApplier(fixture.Path("app"));
    applier.Apply(installation, Profile("One", ("FFlagOwned", "true")));
    const string edited = "{\"FFlagOwned\":false,\"FFlagOther\":false}";
    WriteSettings(installation.ConfigPath, edited);
    Throws<InvalidDataException>(() => applier.Apply(installation, Profile("Two", ("FFlagOwned", "true"))));
    Equal(edited, File.ReadAllText(installation.ConfigPath));
    Throws<InvalidDataException>(() => applier.Restore(installation));
    Equal(edited, File.ReadAllText(installation.ConfigPath));
}

static void MissingFileRestore()
{
    using var fixture = new Fixture();
    var installation = fixture.Installation("Player", ClientKind.Player);
    var applier = new SettingsApplier(fixture.Path("app"));
    var result = applier.Restore(installation);
    Equal(0, result.ManagedCount);
    Check(!File.Exists(installation.ConfigPath), "no-op restore created a file");
    applier.Apply(installation, Profile("One", ("FFlagOwned", "true")));
    Check(File.Exists(installation.ConfigPath), "apply did not create missing settings");
    applier.Restore(installation);
    Check(!File.Exists(installation.ConfigPath), "restore did not remove N strap-created empty settings");

    // A new ownership session adopts the current baseline, including an external empty file.
    WriteSettings(installation.ConfigPath, "{}");
    applier.Apply(installation, Profile("New session", ("FFlagOwned", "true")));
    applier.Restore(installation);
    Check(File.Exists(installation.ConfigPath), "new session deleted a preexisting empty settings file");
    Equal(0, ReadSettings(installation.ConfigPath).Count);

    // An externally added key must keep the file alive even when it was originally absent.
    applier.Apply(installation, Profile("One", ("FFlagOwned", "true")));
    WriteSettings(installation.ConfigPath, "{\"FFlagOwned\":true,\"FFlagExternal\":true}");
    applier.Restore(installation);
    var left = ReadSettings(installation.ConfigPath);
    Equal(1, left.Count);
    Check(left["FFlagExternal"].GetBoolean(), "externally added key was lost");
}

static void Recovery()
{
    using var fixture = new Fixture();
    var installation = fixture.Installation("Player", ClientKind.Player);
    var applier = new SettingsApplier(fixture.Path("app"));
    byte[] before = Encoding.UTF8.GetBytes("{\"FFlagOwned\":false}");
    byte[] after = Encoding.UTF8.GetBytes("{\"FFlagOwned\":true}");
    var next = new ApplyManifest
    {
        TargetPath = installation.ConfigPath, OriginalFileExisted = true,
        Managed = new() { ["FFlagOwned"] = new ManagedValue
            { Original = JsonSerializer.SerializeToElement(false), Applied = JsonSerializer.SerializeToElement(true) } }
    };
    string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation.ConfigPath.ToUpperInvariant())))[..24];
    string journalPath = Path.Combine(applier.RecoveryDirectory, key + ".pending.json");
    Directory.CreateDirectory(applier.RecoveryDirectory);
    var journal = new ApplyJournal
    {
        TargetPath = installation.ConfigPath, BeforeHash = Hash(before), AfterHash = Hash(after), Next = next
    };
    AtomicFile.WriteJson(journalPath, journal);
    Directory.CreateDirectory(Path.GetDirectoryName(installation.ConfigPath)!);
    File.WriteAllBytes(installation.ConfigPath, after);
    applier.Restore(installation);
    Check(!File.Exists(journalPath), "completed target write journal was not cleared");
    Check(!ReadSettings(installation.ConfigPath)["FFlagOwned"].GetBoolean(), "recovered ownership was not restored");

    // The target remained at the pre-write state: recovery drops the pending transaction.
    File.WriteAllBytes(installation.ConfigPath, before);
    File.Delete(Path.Combine(applier.RecoveryDirectory, key + ".json"));
    AtomicFile.WriteJson(journalPath, journal);
    applier.Restore(installation);
    Check(!File.Exists(journalPath), "uncommitted journal was not cleared");
    Check(!ReadSettings(installation.ConfigPath)["FFlagOwned"].GetBoolean(), "pre-write target changed unexpectedly");

    // A third state requires review and must keep both the file and recovery record.
    const string third = "{\"FFlagOwned\":false,\"FFlagExternal\":true}";
    WriteSettings(installation.ConfigPath, third);
    AtomicFile.WriteJson(journalPath, journal);
    Throws<InvalidDataException>(() => applier.Restore(installation));
    Check(File.Exists(journalPath), "ambiguous recovery record was deleted");
    Equal(third, File.ReadAllText(installation.ConfigPath));
}

static void NullAndNoOpRestore()
{
    using var fixture = new Fixture();
    var installation = fixture.Installation("Player", ClientKind.Player);
    const string original = "{  \"FFlagOwned\": null, \"External\": [1,2,3] }";
    WriteSettings(installation.ConfigPath, original);
    var applier = new SettingsApplier(fixture.Path("app"));
    applier.Restore(installation);
    Equal(original, File.ReadAllText(installation.ConfigPath));
    applier.Apply(installation, Profile("One", ("FFlagOwned", "true")));
    // New instance exercises deserialization of the original JSON null.
    new SettingsApplier(fixture.Path("app")).Restore(installation);
    var restored = ReadSettings(installation.ConfigPath);
    Check(restored.ContainsKey("FFlagOwned"), "original JSON null key was removed");
    Equal(JsonValueKind.Null, restored["FFlagOwned"].ValueKind);
    Equal(3, restored["External"].GetArrayLength());
}

static void Discovery()
{
    using var fixture = new Fixture();
    var root = fixture.Path("custom");
    var direct = Path.Combine(root, "version-player");
    var nested = Path.Combine(root, "Versions", "version-studio");
    Directory.CreateDirectory(direct);
    Directory.CreateDirectory(nested);
    File.WriteAllBytes(Path.Combine(direct, "RobloxPlayerBeta.exe"), []);
    File.WriteAllBytes(Path.Combine(nested, "RobloxStudioBeta.exe"), []);
    var found = InstallationDiscovery.Find([root, root]);
    Equal(1, found.Count(i => i.DirectoryPath == Path.GetFullPath(direct) && i.Kind == ClientKind.Player));
    Equal(1, found.Count(i => i.DirectoryPath == Path.GetFullPath(nested) && i.Kind == ClientKind.Studio));
}

static FlagProfile Profile(string name, params (string Name, string Value)[] flags)
{
    var profile = new FlagProfile { Name = name };
    foreach (var (flagName, value) in flags) profile.Flags.Add(new FlagEntry { Name = flagName, Value = value });
    return profile;
}

static Dictionary<string, JsonElement> ReadSettings(string path) => FlagValidator.ReadObject(File.ReadAllBytes(path));
static void WriteSettings(string path, string json)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, json);
}
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}; got {actual}.");
}
static void Throws<T>(Action run) where T : Exception
{
    try { run(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static void ThrowsAny(Action run, string message)
{
    try { run(); }
    catch (Exception e) when (e is JsonException or InvalidDataException) { return; }
    throw new Exception(message);
}

sealed class Fixture : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nstrap-checks-" + Guid.NewGuid().ToString("N"));
    public Fixture() => Directory.CreateDirectory(_root);
    public string Path(string relative) => System.IO.Path.Combine(_root, relative);
    public RobloxInstallation Installation(string name, ClientKind kind)
    {
        string directory = Path(name);
        Directory.CreateDirectory(directory);
        string exe = System.IO.Path.Combine(directory, kind == ClientKind.Player ? "RobloxPlayerBeta.exe" : "RobloxStudioBeta.exe");
        File.WriteAllBytes(exe, []);
        return new(directory, exe, kind, DateTime.UtcNow);
    }
    public void Dispose()
    {
        var full = System.IO.Path.GetFullPath(_root);
        var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath());
        if (!string.Equals(System.IO.Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            || !System.IO.Path.GetFileName(full).StartsWith("nstrap-checks-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a fixture outside the expected temporary directory.");
        Directory.Delete(full, recursive: true);
    }
}
