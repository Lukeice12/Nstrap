using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace Nstrap.App.Services;

internal static class RuntimeReceiptProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Length; internal nint Data; }
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nstrap.App.RuntimeFlags.Ownership.v1");
    internal static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    internal static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static unsafe byte[] Transform(byte[] bytes, bool protect)
    {
        fixed (byte* data = bytes)
        fixed (byte* entropy = Entropy)
        {
            var input = new Blob { Length = bytes.Length, Data = (nint)data };
            var optionalEntropy = new Blob { Length = Entropy.Length, Data = (nint)entropy };
            Blob output = default;
            bool success = protect ? CryptProtectData(ref input, "Nstrap runtime flag ownership", ref optionalEntropy, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, ref optionalEntropy, 0, 0, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Receipt protection failed.");
            try { byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { if (output.Data != 0) LocalFree(output.Data); }
        }
    }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
