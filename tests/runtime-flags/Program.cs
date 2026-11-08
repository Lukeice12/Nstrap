using Nstrap.Core;

var checks = new (string Name, Action Run)[]
{
    ("Typed bytes, no prefix inference, complete read-back", Success),
    ("Read-back mismatch preserves external bytes and rolls back prior write", ReadbackMismatch),
    ("Partial write preserves ambiguous bytes and rolls back prior write", PartialFailure),
    ("Throwing write rolls back known completed values", ThrowingFailure),
    ("External change to earlier write is preserved", ExternalChange),
    ("Whole-batch preflight rejects without memory I/O", Preflight),
    ("Incomplete snapshot prevents all writes", SnapshotFailure),
    ("Changed session prevents rollback into another process", ChangedSession),
    ("Change after snapshot prevents overwriting external bytes", SnapshotConflict)
};
int failed = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
Console.WriteLine($"{checks.Length - failed}/{checks.Length} runtime checks passed");
return failed == 0 ? 0 : 1;

static RuntimeFlagSession Session() => new("owned-fake-process/start-1", new string('A', 64), 32);
static RuntimeFlagMapDescriptor Map(params RuntimeFlagLocation[]? locations) => new(Session().ExecutableSha256, 32,
    locations is { Length: > 0 } ? locations : [new("DFIntNameDoesNotDetermineType", 4, RuntimeFlagScalarType.Boolean), new("Second", 8, RuntimeFlagScalarType.Int32)]);
static RuntimeFlagRequest[] Requests() => [new("DFIntNameDoesNotDetermineType", new RuntimeFlagValue.Boolean(true)), new("Second", new RuntimeFlagValue.Int32(0x12345678))];
static RuntimeFlagBatchResult Apply(Fake memory) => RuntimeFlags.Apply(memory, Session(), Map(), Requests());
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");
static void Success()
{
    var memory = new Fake();
    var result = Apply(memory);
    Check(result.PreflightPassed && result.AllWritesReadBack, "batch failed");
    Check(result.Flags.All(flag => flag.State == RuntimeFlagState.AppliedReadBack), "states incorrect");
    Equal((byte)1, memory.Bytes[4]);
    Check(memory.Bytes.AsSpan(8, 4).SequenceEqual(new byte[] { 0x78, 0x56, 0x34, 0x12 }), "not little endian");
    Check(result.Flags[1].OriginalBytes!.SequenceEqual(new byte[4]), "original bytes missing");
    Equal(2, memory.Writes);
}
static void ReadbackMismatch()
{
    var memory = new Fake { AfterWrite = (m, rva, _) => { if (rva == 8) m.Bytes[8] = 0xEE; } };
    var result = Apply(memory);
    Check(!result.AllWritesReadBack, "mismatch accepted");
    Equal(RuntimeFlagState.RolledBack, result.Flags[0].State);
    Equal(RuntimeFlagState.RollbackConflict, result.Flags[1].State);
    Equal((byte)0, memory.Bytes[4]);
    Equal((byte)0xEE, memory.Bytes[8]);
}
static void PartialFailure()
{
    var memory = new Fake { WriteOverride = (m, rva, bytes) =>
    {
        if (rva != 8) return null;
        bytes.AsSpan(0, 2).CopyTo(m.Bytes.AsSpan(8)); return 2;
    }};
    var result = Apply(memory);
    Equal(RuntimeFlagState.RolledBack, result.Flags[0].State);
    Equal(RuntimeFlagState.RollbackConflict, result.Flags[1].State);
    Equal(2, result.Flags[1].ReportedWrittenBytes);
    Check(memory.Bytes.AsSpan(8, 4).SequenceEqual(new byte[] { 0x78, 0x56, 0, 0 }), "ambiguous partial write was overwritten");
    Equal((byte)0, memory.Bytes[4]);
}
static void ThrowingFailure()
{
    var memory = new Fake { WriteOverride = (_, rva, _) => rva == 8 ? throw new IOException("fake write error") : null };
    var result = Apply(memory);
    Check(!result.AllWritesReadBack, "exception accepted");
    Check(result.Flags.All(flag => flag.State == RuntimeFlagState.RolledBack), "original values not retained/restored");
    Equal((byte)0, memory.Bytes[4]);
}
static void ExternalChange()
{
    var memory = new Fake { WriteOverride = (m, rva, _) =>
    {
        if (rva != 8) return null;
        m.Bytes[4] = 9; throw new IOException("fake later failure");
    }};
    var result = Apply(memory);
    Equal(RuntimeFlagState.RollbackConflict, result.Flags[0].State);
    Equal((byte)9, memory.Bytes[4]);
}
static void Preflight()
{
    var cases = new (RuntimeFlagMapDescriptor Map, RuntimeFlagSession Session, RuntimeFlagRequest[] Requests)[]
    {
        (Map() with { ExecutableSha256 = new string('B', 64) }, Session(), Requests()),
        (Map() with { ExecutableSha256 = "not-a-hash" }, Session(), Requests()),
        (Map() with { ModuleImageSize = 33 }, Session(), Requests()),
        (Map(), Session(), [.. Requests(), new("Unknown", new RuntimeFlagValue.Boolean(true))]),
        (Map(), Session(), [new("Second", new RuntimeFlagValue.String("unsupported"))]),
        (Map(new RuntimeFlagLocation("Text", 0, RuntimeFlagScalarType.String)), Session(), [new("Text", new RuntimeFlagValue.String("unsupported"))]),
        (Map(), Session(), [new("Second", new RuntimeFlagValue.Boolean(true))]),
        (Map(), Session(), [Requests()[0], Requests()[0]]),
        (Map(new RuntimeFlagLocation("DFIntNameDoesNotDetermineType", 32, RuntimeFlagScalarType.Boolean)), Session(), Requests()),
        (Map(new RuntimeFlagLocation("DFIntNameDoesNotDetermineType", 31, RuntimeFlagScalarType.Int32)), Session(), Requests()),
        (Map(new RuntimeFlagLocation("DFIntNameDoesNotDetermineType", 5, RuntimeFlagScalarType.Int32)), Session(), Requests()),
        (Map(new RuntimeFlagLocation("One", 8, RuntimeFlagScalarType.Int32), new("Other", 10, RuntimeFlagScalarType.Boolean)), Session(), Requests()),
        (Map(new RuntimeFlagLocation("Same", 0, RuntimeFlagScalarType.Boolean), new("Same", 1, RuntimeFlagScalarType.Boolean)), Session(), Requests()),
        (Map(new RuntimeFlagLocation("Bad", ulong.MaxValue, RuntimeFlagScalarType.Int32)), Session(), Requests()),
        (Map(), Session() with { ProcessSessionId = "other-lifetime" }, Requests())
    };
    foreach (var item in cases)
    {
        var memory = new Fake();
        var result = RuntimeFlags.Apply(memory, item.Session, item.Map, item.Requests);
        Check(!result.PreflightPassed && !result.AllWritesReadBack, "invalid preflight accepted");
        Equal(0, memory.Reads); Equal(0, memory.Writes);
    }
}
static void SnapshotFailure()
{
    var memory = new Fake { ReadOverride = (_, _, _) => 0 };
    var result = Apply(memory);
    Equal(RuntimeFlagState.SnapshotFailed, result.Flags[0].State);
    Equal(0, memory.Writes);
}
static void ChangedSession()
{
    var memory = new Fake { AfterWrite = (m, _, _) => m.CurrentIdentity = Session() with { ProcessSessionId = "new-process" } };
    var result = Apply(memory);
    Equal(RuntimeFlagState.SessionChanged, result.Flags[0].State);
    Equal(RuntimeFlagState.NotAttempted, result.Flags[1].State);
    Equal(1, memory.Writes);
}
static void SnapshotConflict()
{
    var memory = new Fake { AfterRead = (m, _, _) => { if (m.Reads == 2) m.Bytes[4] = 7; } };
    var result = Apply(memory);
    Equal(RuntimeFlagState.WriteFailed, result.Flags[0].State);
    Equal((byte)7, memory.Bytes[4]); Equal(0, memory.Writes);
}

sealed class Fake : IRuntimeFlagMemory
{
    public byte[] Bytes { get; } = new byte[32];
    public RuntimeFlagSession CurrentIdentity = new("owned-fake-process/start-1", new string('A', 64), 32);
    public RuntimeFlagSession Identity => CurrentIdentity;
    public int Reads, Writes;
    public Func<Fake, ulong, byte[], int?>? WriteOverride;
    public Func<Fake, ulong, int, int?>? ReadOverride;
    public Action<Fake, ulong, byte[]>? AfterWrite;
    public Action<Fake, ulong, int>? AfterRead;
    public int Read(ulong rva, Span<byte> destination)
    {
        Reads++;
        var count = ReadOverride?.Invoke(this, rva, destination.Length);
        if (count is { } value) return value;
        Bytes.AsSpan(checked((int)rva), destination.Length).CopyTo(destination);
        AfterRead?.Invoke(this, rva, destination.Length);
        return destination.Length;
    }
    public int Write(ulong rva, ReadOnlySpan<byte> source)
    {
        Writes++;
        byte[] copy = source.ToArray();
        var count = WriteOverride?.Invoke(this, rva, copy);
        if (count is { } value) return value;
        source.CopyTo(Bytes.AsSpan(checked((int)rva)));
        AfterWrite?.Invoke(this, rva, copy);
        return source.Length;
    }
}
