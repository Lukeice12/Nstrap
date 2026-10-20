using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nstrap.App.Services;

internal static class AppearanceIcon
{
    public const string DefaultIconUri = "pack://application:,,,/Assets/NstrapIcon.png";

    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MinDimension = 16;
    private const int MaxDimension = 2048;
    private const int MaxFrames = 32;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];
    private const long MaxTotalPixels = 16L * 1024 * 1024;
    private const string InvalidIconMessage = "Choose a valid PNG or ICO image between 16 and 2048 pixels wide and high, up to 4 MB.";

    public static ImageSource Load(string dataDirectory, string? storedFileName)
    {
        if (storedFileName is null)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(DefaultIconUri, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        if (!IsManagedName(storedFileName))
            throw new InvalidDataException("The saved icon name is invalid.");

        string iconsDirectory = GetIconsDirectory(dataDirectory);
        RejectRedirectedDirectory(iconsDirectory);
        string path = Path.Combine(iconsDirectory, storedFileName);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The saved icon cannot be a redirected file.");
        byte[] bytes = ReadBounded(path);
        return Decode(bytes, Path.GetExtension(storedFileName));
    }

    public static string Import(string dataDirectory, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".ico"))
            throw new InvalidDataException(InvalidIconMessage);

        byte[] bytes = ReadBounded(sourcePath);
        _ = Decode(bytes, extension);

        string iconsDirectory = GetIconsDirectory(dataDirectory);
        Directory.CreateDirectory(iconsDirectory);
        RejectRedirectedDirectory(iconsDirectory);

        string fileName = $"icon-{Guid.NewGuid():N}{extension}";
        string targetPath = Path.Combine(iconsDirectory, fileName);
        string temporaryPath = targetPath + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, targetPath);
            return fileName;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void RemoveManaged(string dataDirectory, string? storedFileName)
    {
        try
        {
            RemoveManagedCore(dataDirectory, storedFileName);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("The saved icon could not be removed.", ex);
        }
    }

    private static void RemoveManagedCore(string dataDirectory, string? storedFileName)
    {
        if (storedFileName is null) return;
        if (!IsManagedName(storedFileName))
            throw new IOException("The saved icon name is invalid.");

        string iconsDirectory = GetIconsDirectory(dataDirectory);
        FileAttributes folderAttributes;
        try
        {
            folderAttributes = File.GetAttributes(iconsDirectory);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        if ((folderAttributes & FileAttributes.Directory) == 0 || (folderAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The icon folder is not a regular folder.");

        string path = Path.Combine(iconsDirectory, storedFileName);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The saved icon is not a regular file.");
        File.Delete(path);
    }

    private static string GetIconsDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(Path.GetFullPath(dataDirectory), "icons");
    }

    private static void RejectRedirectedDirectory(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The icon folder cannot be a redirected folder.");
    }

    private static bool IsManagedName(string fileName)
    {
        if (fileName.Length != 41 || !fileName.StartsWith("icon-", StringComparison.Ordinal))
            return false;
        string extension = Path.GetExtension(fileName);
        if (extension is not (".png" or ".ico")) return false;
        return Guid.TryParseExact(fileName.AsSpan(5, 32), "N", out _);
    }

    private static byte[] ReadBounded(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 or > MaxFileBytes)
            throw new InvalidDataException(InvalidIconMessage);
        using var memory = new MemoryStream((int)input.Length);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if (memory.Length + count > MaxFileBytes)
                throw new InvalidDataException(InvalidIconMessage);
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    private static ImageSource Decode(byte[] bytes, string extension)
    {
        Preflight(bytes, extension);

        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count is < 1 or > MaxFrames)
                throw new InvalidDataException(InvalidIconMessage);

            BitmapFrame? selected = null;
            long selectedArea = -1;
            long totalArea = 0;
            byte[] pixels = [];
            foreach (BitmapFrame frame in decoder.Frames)
            {
                int width = frame.PixelWidth;
                int height = frame.PixelHeight;
                if (width is < MinDimension or > MaxDimension || height is < MinDimension or > MaxDimension)
                    throw new InvalidDataException(InvalidIconMessage);
                totalArea += (long)width * height;
                if (totalArea > MaxTotalPixels)
                    throw new InvalidDataException(InvalidIconMessage);

                // Force every frame to decode while the input stream is open.
                var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                int stride = checked(width * 4);
                int requiredBytes = checked(stride * height);
                if (pixels.Length < requiredBytes) pixels = new byte[requiredBytes];
                bitmap.CopyPixels(pixels, stride, 0);

                long area = (long)width * height;
                if (area > selectedArea)
                {
                    selected = frame;
                    selectedArea = area;
                }
            }

            selected!.Freeze();
            return selected;
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or COMException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException(InvalidIconMessage, ex);
        }
    }

    private static void Preflight(byte[] bytes, string extension)
    {
        if (extension == ".png")
        {
            _ = PngDimensions(bytes);
            return;
        }
        if (extension != ".ico" || bytes.Length < 6
            || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0)
            throw new InvalidDataException(InvalidIconMessage);

        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        if (count is < 1 or > MaxFrames || bytes.Length < 6 + 16 * count)
            throw new InvalidDataException(InvalidIconMessage);

        int directoryEnd = 6 + 16 * count;
        long totalArea = 0;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = bytes.AsSpan(6 + 16 * index, 16);
            int width = entry[0] == 0 ? 256 : entry[0];
            int height = entry[1] == 0 ? 256 : entry[1];
            CheckDimensions(width, height);
            totalArea += (long)width * height;
            if (totalArea > MaxTotalPixels)
                throw new InvalidDataException(InvalidIconMessage);

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]);
            if (offset < directoryEnd || size < 40 || offset > bytes.Length || size > bytes.Length - offset)
                throw new InvalidDataException(InvalidIconMessage);

            ReadOnlySpan<byte> image = bytes.AsSpan((int)offset, (int)size);
            if (image.StartsWith(PngSignature))
            {
                var png = PngDimensions(image);
                if (png.Width != width || png.Height != height)
                    throw new InvalidDataException(InvalidIconMessage);
            }
            else
            {
                uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(image[..4]);
                if (headerSize < 40 || headerSize > image.Length)
                    throw new InvalidDataException(InvalidIconMessage);
                int dibWidth = BinaryPrimitives.ReadInt32LittleEndian(image[4..8]);
                int dibHeight = BinaryPrimitives.ReadInt32LittleEndian(image[8..12]);
                uint compression = BinaryPrimitives.ReadUInt32LittleEndian(image[16..20]);
                if (dibWidth != width || (dibHeight != height && dibHeight != 2 * height)
                    || compression is not (0 or 3 or 6))
                    throw new InvalidDataException(InvalidIconMessage);
            }
        }
    }

    private static (int Width, int Height) PngDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || !bytes.StartsWith(PngSignature)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13
            || !bytes[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException(InvalidIconMessage);

        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        if (width is < MinDimension or > MaxDimension || height is < MinDimension or > MaxDimension)
            throw new InvalidDataException(InvalidIconMessage);
        return ((int)width, (int)height);
    }

    private static void CheckDimensions(int width, int height)
    {
        if (width is < MinDimension or > MaxDimension || height is < MinDimension or > MaxDimension)
            throw new InvalidDataException(InvalidIconMessage);
    }
}
