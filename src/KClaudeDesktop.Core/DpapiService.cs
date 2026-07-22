using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace KClaudeDesktop.Core;

public static class DpapiService
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(SecureString value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new ArgumentException("Secret cannot be empty.", nameof(value));
        }

        var bstr = IntPtr.Zero;
        byte[]? plainBytes = null;
        try
        {
            bstr = Marshal.SecureStringToBSTR(value);
            plainBytes = new byte[value.Length * sizeof(char)];
            Marshal.Copy(bstr, plainBytes, 0, plainBytes.Length);
            var protectedBytes = ProtectBytes(plainBytes);
            return Convert.ToHexString(protectedBytes).ToLowerInvariant();
        }
        finally
        {
            if (plainBytes is not null)
            {
                Array.Clear(plainBytes);
            }
            if (bstr != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(bstr);
            }
        }
    }

    public static string Unprotect(string cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            throw new InvalidDataException("Encrypted Key file is empty.");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromHexString(cipherText.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Encrypted Key file is not valid DPAPI hex data.", exception);
        }

        var plainBytes = UnprotectBytes(protectedBytes);
        try
        {
            return Encoding.Unicode.GetString(plainBytes).TrimEnd('\0');
        }
        finally
        {
            Array.Clear(plainBytes);
            Array.Clear(protectedBytes);
        }
    }

    private static byte[] ProtectBytes(byte[] value)
    {
        var input = CreateBlob(value);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "KClaude Desktop Key",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI encryption failed.");
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    LocalFree(output.Data);
                }
            }
        }
        finally
        {
            FreeBlob(input, clear: true);
        }
    }

    private static byte[] UnprotectBytes(byte[] value)
    {
        var input = CreateBlob(value);
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI decryption failed.");
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    LocalFree(output.Data);
                }
            }
        }
        finally
        {
            FreeBlob(input, clear: false);
        }
    }

    private static DataBlob CreateBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob { Size = value.Length, Data = pointer };
    }

    private static void FreeBlob(DataBlob blob, bool clear)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }
        if (clear && blob.Size > 0)
        {
            var zeros = new byte[blob.Size];
            Marshal.Copy(zeros, 0, blob.Data, zeros.Length);
        }
        Marshal.FreeHGlobal(blob.Data);
    }
}
