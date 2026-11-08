using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal enum RuntimeEvidenceKind { ConsumerInstruction, CalibratedRegistryScalar, ConsumerInstructionAndCalibratedRegistryScalar }

/// <summary>Only reviewed embedded metadata is loaded. No external address map or heap discovery.</summary>
internal sealed class RuntimeFlagMap
{
    private readonly Dictionary<string, JsonElement> evidence;
    private readonly Dictionary<string, string> exclusions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeEvidenceKind> kinds = new(StringComparer.Ordinal);
    internal RuntimeFlagMapDescriptor Descriptor { get; }
    internal string Version { get; }
    internal static RuntimeFlagMap Load()
    {
        using Stream stream = typeof(RuntimeFlagMap).Assembly.GetManifestResourceStream("Nstrap.RuntimeFlags.ReviewedMap.json")
            ?? throw new IOException("The embedded reviewed runtime map is missing.");
        using var parsed = JsonDocument.Parse(stream);
        return new(parsed.RootElement.Clone());
    }
    // Internal to permit malformed-resource tests. No app argument/profile can supply a map.
    internal RuntimeFlagMap(JsonElement root)
    {
        if (root.GetProperty("SchemaVersion").GetInt32() != 2) throw new IOException("Unsupported runtime map schema.");
        var executable = root.GetProperty("Executable");
        Version = executable.GetProperty("Version").GetString()!;
        var locations = root.GetProperty("CoreEntries").EnumerateArray().Select(entry => new RuntimeFlagLocation(
            entry.GetProperty("Name").GetString()!, entry.GetProperty("Rva").GetUInt64(), ReadType(entry.GetProperty("Type")))).ToArray();
        Descriptor = new(executable.GetProperty("Sha256").GetString()!, executable.GetProperty("ImageSize").GetUInt64(), locations);
        if (!ValidHash(Descriptor.ExecutableSha256) || Descriptor.ModuleImageSize == 0 || locations.Length == 0
            || locations.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != locations.Length)
            throw new IOException("Invalid pinned build or duplicate runtime locations.");
        var review = root.GetProperty("Review");
        string artifact = review.GetProperty("ArtifactId").GetString()!;
        foreach (string field in new[] { "RegistryMapSha256", "RegistryEvidenceSha256", "PriorConsumerMapSha256", "ConsumerAnomalySha256" })
            if (!ValidHash(review.GetProperty(field).GetString())) throw new IOException("Missing reviewed source-artifact hash.");
        if (artifact != "registry-stage2:" + review.GetProperty("RegistryEvidenceSha256").GetString()) throw new IOException("Registry artifact identity disagrees.");
        var groups = root.GetProperty("CalibrationGroups").EnumerateArray().ToDictionary(group => group.GetProperty("Id").GetString()!, StringComparer.Ordinal);
        foreach (var group in groups.Values) ValidateCalibration(group);
        evidence = root.GetProperty("Entries").EnumerateArray().ToDictionary(entry => entry.GetProperty("Name").GetString()!, StringComparer.Ordinal);
        if (evidence.Count != locations.Length) throw new IOException("Evidence and scalar counts disagree.");
        ulong end = 0;
        foreach (var location in locations.OrderBy(item => item.RelativeVirtualAddress))
        {
            if (string.IsNullOrWhiteSpace(location.Name) || !evidence.TryGetValue(location.Name, out var entry)) throw new IOException("Scalar evidence is missing.");
            int width = location.Type == RuntimeFlagScalarType.Boolean ? 1 : 4;
            if (entry.GetProperty("Rva").GetUInt64() != location.RelativeVirtualAddress || ReadType(entry.GetProperty("Type")) != location.Type
                || entry.GetProperty("Width").GetInt32() != width || entry.GetProperty("ByteOrder").GetString() != "little-endian")
                throw new IOException("Scalar type, width or RVA disagrees with its proof.");
            Range(location.RelativeVirtualAddress, width);
            if (location.RelativeVirtualAddress < end || (width == 4 && location.RelativeVirtualAddress % 4 != 0)) throw new IOException("Scalar ranges overlap or are unaligned.");
            end = location.RelativeVirtualAddress + (ulong)width;
            ValidateName(entry.GetProperty("StaticEvidence"));
            var kind = entry.GetProperty("EvidenceKind").GetString() switch
            {
                "ConsumerInstruction" => RuntimeEvidenceKind.ConsumerInstruction,
                "CalibratedRegistryScalar" => RuntimeEvidenceKind.CalibratedRegistryScalar,
                "ConsumerInstructionAndCalibratedRegistryScalar" => RuntimeEvidenceKind.ConsumerInstructionAndCalibratedRegistryScalar,
                _ => throw new IOException("Unknown runtime evidence kind.")
            };
            kinds.Add(location.Name, kind);
            if (kind != RuntimeEvidenceKind.CalibratedRegistryScalar) ValidateConsumer(entry.GetProperty("ConsumerEvidence"), location.RelativeVirtualAddress, width);
            else if (entry.TryGetProperty("ConsumerEvidence", out var extraConsumer) && extraConsumer.ValueKind != JsonValueKind.Null)
                throw new IOException("Registry-only evidence carries an undeclared consumer.");
            if (kind != RuntimeEvidenceKind.ConsumerInstruction)
            {
                var proof = entry.GetProperty("RegistryEvidence");
                if (!proof.GetProperty("Approved").GetBoolean() || !proof.GetProperty("KeyDescriptorScalarChainVerified").GetBoolean()
                    || !proof.GetProperty("ModuleAnchorsValidated").GetBoolean() || proof.GetProperty("ArtifactId").GetString() != artifact
                    || proof.GetProperty("ScalarRva").GetUInt64() != location.RelativeVirtualAddress
                    || proof.GetProperty("RegistryKey").GetString() != entry.GetProperty("StaticEvidence").GetProperty("NameInImage").GetString()
                    || !groups.TryGetValue(proof.GetProperty("CalibrationId").GetString()!, out var group)
                    || ReadType(group.GetProperty("Type")) != location.Type || group.GetProperty("Width").GetInt32() != width)
                    throw new IOException("Unapproved or inconsistent calibrated registry proof.");
                var names = group.GetProperty("Calibrators").EnumerateArray().Select(item => item.GetProperty("Name").GetString()!).ToHashSet(StringComparer.Ordinal);
                if (!names.SetEquals(proof.GetProperty("CalibrationNames").EnumerateArray().Select(item => item.GetString()!)))
                    throw new IOException("Registry proof does not retain all independent calibrators.");
                Range(proof.GetProperty("PointerSlotRva").GetUInt64(), 8);
                if (proof.GetProperty("NodeDescriptorOffset").GetInt32() != 48 || proof.GetProperty("DescriptorStoragePointerOffset").GetInt32() != 32)
                    throw new IOException("Registry discovery-chain layout differs from the reviewed proof.");
            }
            else if (!entry.TryGetProperty("ConsumerReviewArtifactId", out var reviewId)
                || reviewId.GetString() != "registry-anomaly:" + review.GetProperty("ConsumerAnomalySha256").GetString()
                || (entry.TryGetProperty("RegistryEvidence", out var forbidden) && forbidden.ValueKind != JsonValueKind.Null))
                throw new IOException("Consumer-only entry lacks review or declares registry fallback.");
        }
        foreach (var entry in root.GetProperty("IneligibleEntries").EnumerateArray())
        {
            string name = entry.GetProperty("Name").GetString()!, code = entry.GetProperty("Code").GetString()!, reason = entry.GetProperty("Reason").GetString()!;
            if (evidence.ContainsKey(name) || string.IsNullOrWhiteSpace(reason) || code is not ("RegistryKeyNotFound" or "UnregisteredDescriptor" or "WidthConflict" or "UnsupportedString" or "UnsupportedChannel"))
                throw new IOException("Invalid or contradictory runtime exclusion.");
            exclusions.Add(name, code + ": " + reason);
        }
    }
    internal bool TryRequest(FlagEntry entry, out RuntimeFlagRequest? request, out string reason)
    {
        request = null;
        var location = Descriptor.Locations.SingleOrDefault(item => item.Name == entry.Name);
        if (location is null) { reason = exclusions.GetValueOrDefault(entry.Name) ?? "No reviewed runtime mapping for this exact build. Settings-file value is retained."; return false; }
        if (location.Type == RuntimeFlagScalarType.Boolean && bool.TryParse(entry.Value, out bool boolean)) request = new(entry.Name, new RuntimeFlagValue.Boolean(boolean));
        else if (location.Type == RuntimeFlagScalarType.Int32 && int.TryParse(entry.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int integer)) request = new(entry.Name, new RuntimeFlagValue.Int32(integer));
        reason = request is null ? "Unsupported runtime value or scalar type." : "";
        return request is not null;
    }
    internal string Constraint(string name) => evidence.TryGetValue(name, out var entry) && entry.TryGetProperty("ConstraintNote", out var note) ? note.GetString() ?? "" : "";
    internal string Verify(IRuntimeFlagMemory memory, ulong moduleBase, string name)
    {
        var session = memory.Identity;
        if (session.ModuleImageSize != Descriptor.ModuleImageSize || !session.ExecutableSha256.Equals(Descriptor.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Runtime map does not match this executable build.");
        VerifyName(memory, moduleBase, name);
        var entry = evidence[name]; var kind = kinds[name]; string proof = kind.ToString();
        if (kind != RuntimeEvidenceKind.CalibratedRegistryScalar)
        {
            var consumer = entry.GetProperty("ConsumerEvidence");
            byte[] expected = Convert.FromHexString(consumer.GetProperty("ExpectedBytesHex").GetString()!);
            byte[]? actual = null;
            try { actual = RuntimeFlagOwnership.Read(memory, consumer.GetProperty("Rva").GetUInt64(), expected.Length); }
            catch (Exception error) when (kind == RuntimeEvidenceKind.ConsumerInstructionAndCalibratedRegistryScalar && error is IOException or Win32Exception)
            { proof = "CalibratedRegistryScalar (consumer code unreadable)"; }
            // Readable contradiction must never become a registry fallback.
            if (actual is not null && !actual.SequenceEqual(expected)) throw new IOException("Readable consumer bytes contradict the reviewed map; registry fallback refused.");
        }
        if (entry.GetProperty("Width").GetInt32() == 1 && RuntimeFlagOwnership.Read(memory, entry.GetProperty("Rva").GetUInt64(), 1)[0] > 1)
            throw new IOException("Live Boolean storage is outside the reviewed 0/1 domain.");
        if (memory.Identity != session) throw new IOException("Process lifetime changed during verification.");
        return proof;
    }
    internal void VerifyName(IRuntimeFlagMemory memory, ulong moduleBase, string name)
    {
        var anchors = evidence[name].GetProperty("StaticEvidence");
        byte[] expected = Convert.FromHexString(anchors.GetProperty("ExpectedNameBytesHex").GetString()!);
        ulong stringRva = anchors.GetProperty("NameStringRva").GetUInt64();
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(RuntimeFlagOwnership.Read(memory, anchors.GetProperty("NamePointerRva").GetUInt64(), 8));
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(RuntimeFlagOwnership.Read(memory, anchors.GetProperty("NameLengthRva").GetUInt64(), 8));
        if (pointer != checked(moduleBase + stringRva) || length != anchors.GetProperty("ExpectedNameLength").GetUInt64()
            || !RuntimeFlagOwnership.Read(memory, stringRva, expected.Length).SequenceEqual(expected))
            throw new IOException("Live name, pointer or length anchor does not match the reviewed map.");
    }
    private void ValidateCalibration(JsonElement group)
    {
        var type = ReadType(group.GetProperty("Type")); int width = type == RuntimeFlagScalarType.Boolean ? 1 : 4;
        var rtti = group.GetProperty("Rtti");
        if (!group.GetProperty("Approved").GetBoolean() || group.GetProperty("Width").GetInt32() != width || !rtti.GetProperty("validated").GetBoolean()
            || string.IsNullOrWhiteSpace(rtti.GetProperty("decorated_type").GetString()) || rtti.GetProperty("object_offset").GetInt32() != 0)
            throw new IOException("Unapproved calibrated internal type.");
        Range(rtti.GetProperty("vtable_rva").GetUInt64(), 8); Range(rtti.GetProperty("locator_rva").GetUInt64(), 24); Range(rtti.GetProperty("type_descriptor_rva").GetUInt64(), 16);
        var calibrators = group.GetProperty("Calibrators").EnumerateArray().ToArray();
        if (calibrators.Length < 2 || calibrators.Select(item => item.GetProperty("Name").GetString()).Distinct().Count() != calibrators.Length) throw new IOException("Internal type needs independent unique calibrators.");
        foreach (var item in calibrators)
        {
            if (ReadType(item.GetProperty("Type")) != type || item.GetProperty("Width").GetInt32() != width) throw new IOException("Contradictory independent width calibration.");
            Range(item.GetProperty("Rva").GetUInt64(), width); ValidateConsumer(item.GetProperty("ConsumerEvidence"), item.GetProperty("Rva").GetUInt64(), width);
        }
    }
    private void ValidateConsumer(JsonElement consumer, ulong scalar, int width)
    {
        byte[] bytes = Convert.FromHexString(consumer.GetProperty("ExpectedBytesHex").GetString()!);
        if (bytes.Length is < 1 or > 32 || consumer.GetProperty("Size").GetInt32() != bytes.Length || consumer.GetProperty("ScalarTargetRva").GetUInt64() != scalar
            || consumer.GetProperty("OperandWidth").GetInt32() != width) throw new IOException("Consumer proof disagrees with scalar width or address.");
        Range(consumer.GetProperty("Rva").GetUInt64(), bytes.Length);
    }
    private void ValidateName(JsonElement anchors)
    {
        string name = anchors.GetProperty("NameInImage").GetString()!;
        byte[] expected = Convert.FromHexString(anchors.GetProperty("ExpectedNameBytesHex").GetString()!);
        int length = anchors.GetProperty("ExpectedNameLength").GetInt32();
        if (length is < 1 or > 1024 || name.Any(character => character > 127) || name.Length != length || !expected.SequenceEqual(Encoding.ASCII.GetBytes(name + '\0')))
            throw new IOException("Invalid exact module name evidence.");
        Range(anchors.GetProperty("NameStringRva").GetUInt64(), expected.Length); Range(anchors.GetProperty("NamePointerRva").GetUInt64(), 8); Range(anchors.GetProperty("NameLengthRva").GetUInt64(), 8);
    }
    private void Range(ulong rva, int count)
    { if (count <= 0 || Descriptor.ModuleImageSize < (ulong)count || rva > Descriptor.ModuleImageSize - (ulong)count) throw new IOException("Evidence range is outside the pinned module."); }
    private static RuntimeFlagScalarType ReadType(JsonElement value) => value.GetString() switch { "Boolean" => RuntimeFlagScalarType.Boolean, "Int32" => RuntimeFlagScalarType.Int32, _ => throw new IOException("Unsupported reviewed scalar type.") };
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
