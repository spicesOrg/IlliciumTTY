using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace IlliciumTTY.Services;

internal static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IlliciumTTY.ConnectionSecret.v1");

    public static string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return DpapiPrefix + Convert.ToBase64String(ProtectWithDpapi(bytes));
            }
            catch
            {
                // Keep configuration saving functional even if DPAPI is unavailable.
            }
        }

        return PlainPrefix + Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            if (protectedValue.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                {
                    return null;
                }

                var bytes = Convert.FromBase64String(protectedValue[DpapiPrefix.Length..]);
                return Encoding.UTF8.GetString(UnprotectWithDpapi(bytes));
            }

            if (protectedValue.StartsWith(PlainPrefix, StringComparison.Ordinal))
            {
                var bytes = Convert.FromBase64String(protectedValue[PlainPrefix.Length..]);
                return Encoding.UTF8.GetString(bytes);
            }
        }
        catch
        {
            return null;
        }

        return protectedValue;
    }

    private static byte[] ProtectWithDpapi(byte[] bytes)
    {
        var input = CreateBlob(bytes);
        var entropy = CreateBlob(Entropy);
        var output = default(DataBlob);

        try
        {
            if (!CryptProtectData(
                    ref input,
                    "IlliciumTTY",
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyAndFreeBlob(output);
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    private static byte[] UnprotectWithDpapi(byte[] bytes)
    {
        var input = CreateBlob(bytes);
        var entropy = CreateBlob(Entropy);
        var output = default(DataBlob);
        var description = IntPtr.Zero;

        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    out description,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyAndFreeBlob(output);
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return default;
        }

        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob
        {
            DataLength = bytes.Length,
            DataPointer = pointer
        };
    }

    private static byte[] CopyAndFreeBlob(DataBlob blob)
    {
        try
        {
            if (blob.DataLength <= 0 || blob.DataPointer == IntPtr.Zero)
            {
                return [];
            }

            var bytes = new byte[blob.DataLength];
            Marshal.Copy(blob.DataPointer, bytes, 0, blob.DataLength);
            return bytes;
        }
        finally
        {
            if (blob.DataPointer != IntPtr.Zero)
            {
                LocalFree(blob.DataPointer);
            }
        }
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.DataPointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.DataPointer);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int DataLength;
        public IntPtr DataPointer;
    }
}