using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Nstrap.App.Services;

/// <summary>
/// Personalizes only the reserved native FileDescription value. All lengths,
/// headers, resources and bundled payload bytes retain their original offsets.
/// This deliberately is not a general PE resource editor.
/// </summary>
internal static class ExecutableDescription
{
    private const int ValueWords = 65;
    private const int ValueBytes = ValueWords * 2;

    public static string Read(string path) => FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";

    public static string CopyWithName(FileStream source, string destination, string name)
    {
        string expected = HashWithName(source, name);
        File.Copy(source.Name, destination, false);
        string actual = Apply(destination, name);
        if (actual != expected) throw new IOException("The personalized executable failed verification.");
        return actual;
    }

    public static string Apply(string path, string name)
    {
        UpdateHandoff.RequireRegularPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long offset = FindValue(file);
        byte[] value = Encode(name);
        string expected = HashWithValue(file, offset, value);
        file.Position = offset;
        file.Write(value);
        file.Flush(flushToDisk: true);
        file.Position = 0;
        string actual = Convert.ToHexString(SHA256.HashData(file));
        if (actual != expected) throw new IOException("The executable changed outside its process-name field.");
        return actual;
    }

    public static void RequirePersonalizedCopy(string source, string target, string name,
        string sourceHash, string targetHash)
    {
        UpdateHandoff.RequireRegularPath(source);
        UpdateHandoff.RequireRegularPath(target);
        using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var replacement = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(original)) != sourceHash
            || Convert.ToHexString(SHA256.HashData(replacement)) != targetHash
            || HashWithName(original, name) != targetHash)
            throw new IOException("The process-name repair must change only the reserved file description.");
    }

    private static string HashWithName(FileStream file, string name) => HashWithValue(file, FindValue(file), Encode(name));

    private static byte[] Encode(string name)
    {
        if (AppIdentity.NormalizeProcessName(name) != name) throw new ArgumentException("Invalid process name.");
        var value = new byte[ValueBytes];
        Encoding.Unicode.GetBytes(name, value);
        return value;
    }

    private static string HashWithValue(FileStream file, long offset, byte[] value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        file.Position = 0;
        var buffer = new byte[128 * 1024];
        long position = 0;
        int count;
        while ((count = file.Read(buffer)) != 0)
        {
            long first = Math.Max(position, offset), last = Math.Min(position + count, offset + value.Length);
            if (first < last)
                value.AsSpan((int)(first - offset), (int)(last - first)).CopyTo(buffer.AsSpan((int)(first - position)));
            hash.AppendData(buffer.AsSpan(0, count));
            position += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static long FindValue(FileStream file)
    {
        file.Position = 0;
        using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
        var headers = pe.PEHeaders;
        var optional = headers.PEHeader ?? throw InvalidLayout();
        // A signed release must be personalized before signing, never modified
        // here. Managed assembly metadata and embedded dependencies are untouched.
        if (headers.CoffHeader.Machine != Machine.Amd64 || optional.Magic != PEMagic.PE32Plus
            || headers.CorHeader is not null || optional.CertificateTableDirectory.Size != 0
            || optional.CertificateTableDirectory.RelativeVirtualAddress != 0)
            throw new IOException("This executable does not support process-name customization. Use the unsigned portable release.");
        var resource = optional.ResourceTableDirectory;
        if (resource.Size < 16) throw InvalidLayout();
        var sections = headers.SectionHeaders.Where(section =>
            resource.RelativeVirtualAddress >= section.VirtualAddress
            && (long)resource.RelativeVirtualAddress + resource.Size <= (long)section.VirtualAddress + section.SizeOfRawData).ToArray();
        if (sections.Length != 1 || sections[0].Name != ".rsrc"
            || (sections[0].SectionCharacteristics & SectionCharacteristics.MemExecute) != 0)
            throw InvalidLayout();
        long resourceOffset = (long)sections[0].PointerToRawData + resource.RelativeVirtualAddress - sections[0].VirtualAddress;
        if (resourceOffset < optional.SizeOfHeaders || resourceOffset + resource.Size > file.Length
            || (resourceOffset & 3) != 0) throw InvalidLayout();
        var resources = new ResourceData(file, resourceOffset, resource.Size);
        // Icons can make the native resource section larger than 1 MiB. Read
        // only bounded directory tables and the version record we will edit;
        // unrelated resource payloads neither set a size limit nor get loaded.
        // The release compiler emits one RT_VERSION (#16), resource #1, with
        // one language. Reject additional variants rather than patch only one.
        int type = DirectoryEntry(resources, 0, 16, requireSingle: false, isDirectory: true);
        int version = DirectoryEntry(resources, type, 1, requireSingle: true, isDirectory: true);
        int entry = DirectoryEntry(resources, version, null, requireSingle: true, isDirectory: false);
        byte[] data = resources.Read(entry, 16);
        long versionOffset = (long)U32(data, 0) - resource.RelativeVirtualAddress;
        uint size = U32(data, 4);
        if (versionOffset < 0 || (versionOffset & 3) != 0 || size is < 6 or > ushort.MaxValue) throw InvalidLayout();
        byte[] bytes = resources.Read(versionOffset, (int)size);
        var root = Node.Read(bytes, 0, bytes.Length);
        if (root.Key != "VS_VERSION_INFO" || root.Type != 0 || root.ValueLength != 52
            || U32(bytes, root.ValueOffset) != 0xFEEF04BD) throw InvalidLayout();
        var strings = SingleChild(bytes, root, "StringFileInfo");
        if (strings.Type != 1 || strings.ValueLength != 0) throw InvalidLayout();
        var tables = Children(bytes, strings);
        if (tables.Count != 1 || tables[0].Key != "000004b0" || tables[0].ValueLength != 0) throw InvalidLayout();
        var description = SingleChild(bytes, tables[0], "FileDescription");
        var product = SingleChild(bytes, tables[0], "ProductName");
        if (description.Type != 1 || description.ValueLength != ValueWords
            || description.ValueOffset + ValueBytes != description.End
            || TextValue(bytes, product) != "N strap") throw InvalidLayout();
        string oldName = TextValue(bytes, description);
        if (AppIdentity.NormalizeProcessName(oldName) != oldName) throw InvalidLayout();
        int nullStart = description.ValueOffset + oldName.Length * 2;
        if (bytes.AsSpan(nullStart, description.End - nullStart).ContainsAnyExcept((byte)0)) throw InvalidLayout();
        return resourceOffset + versionOffset + description.ValueOffset;
    }

    private sealed record ResourceData(FileStream File, long Offset, int Length)
    {
        internal byte[] Read(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > Length || count > Length - offset) throw InvalidLayout();
            var bytes = new byte[count];
            File.Position = Offset + offset;
            File.ReadExactly(bytes);
            return bytes;
        }
    }

    private static int DirectoryEntry(ResourceData resources, int offset, uint? id, bool requireSingle, bool isDirectory)
    {
        byte[] header = resources.Read(offset, 16);
        int named = U16(header, 12), count = named + U16(header, 14);
        if (count is < 1 or > 256 || (requireSingle && (named != 0 || count != 1))) throw InvalidLayout();
        byte[] bytes = resources.Read((long)offset + 16, count * 8);
        int result = -1;
        for (int i = 0; i < count; i++)
        {
            int entry = i * 8;
            uint name = U32(bytes, entry), value = U32(bytes, entry + 4);
            if (id is not null && name != id) continue;
            if (result != -1 || ((value & 0x80000000) != 0) != isDirectory || (name & 0x80000000) != 0)
                throw InvalidLayout();
            result = (int)(value & 0x7fffffff);
        }
        if (result < 0) throw InvalidLayout();
        return result;
    }

    private static Node SingleChild(byte[] bytes, Node parent, string key)
    {
        var matches = Children(bytes, parent).Where(child => child.Key == key).ToArray();
        return matches.Length == 1 ? matches[0] : throw InvalidLayout();
    }

    private static List<Node> Children(byte[] bytes, Node parent)
    {
        var children = new List<Node>();
        int position = Align(parent.ValueOffset + parent.ValueLength * (parent.Type == 1 ? 2 : 1));
        while (position < parent.End)
        {
            if (children.Count >= 32) throw InvalidLayout();
            var child = Node.Read(bytes, position, parent.End);
            children.Add(child);
            position = Align(child.End);
        }
        return children;
    }

    private static string TextValue(byte[] bytes, Node node)
    {
        if (node.Type != 1 || node.ValueLength < 1) throw InvalidLayout();
        int end = node.ValueOffset;
        while (end + 2 <= node.End && U16(bytes, end) != 0) end += 2;
        if (end >= node.ValueOffset + node.ValueLength * 2) throw InvalidLayout();
        return Encoding.Unicode.GetString(bytes, node.ValueOffset, end - node.ValueOffset);
    }

    private sealed record Node(string Key, int Type, int ValueLength, int ValueOffset, int End)
    {
        internal static Node Read(byte[] bytes, int start, int limit)
        {
            RequireRange(bytes, start, 6);
            int end = start + U16(bytes, start), length = U16(bytes, start + 2), type = U16(bytes, start + 4);
            if (end > limit || end < start + 8 || type > 1) throw InvalidLayout();
            int keyEnd = start + 6;
            while (keyEnd + 2 <= end && U16(bytes, keyEnd) != 0) keyEnd += 2;
            if (keyEnd + 2 > end) throw InvalidLayout();
            int value = Align(keyEnd + 2);
            if ((long)value + length * (type == 1 ? 2 : 1) > end) throw InvalidLayout();
            return new(Encoding.Unicode.GetString(bytes, start + 6, keyEnd - start - 6), type, length, value, end);
        }
    }

    private static int Align(int value) => checked((value + 3) & ~3);
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void RequireRange(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || (long)offset + count > bytes.Length) throw InvalidLayout();
    }
    private static IOException InvalidLayout() => new("The executable has no supported process-name field. Install the latest portable release before applying this name.");
}
