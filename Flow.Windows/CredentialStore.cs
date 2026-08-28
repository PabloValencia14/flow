using System.Runtime.InteropServices;
using System.Text;

namespace Flow.Windows;

public static class CredentialStore
{
    private const uint GenericCredentialType = 1;
    private const uint PersistLocalMachine = 2;

    public static string? Read(string target)
    {
        if (!CredRead(target, GenericCredentialType, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            // cmdkey puede guardar el valor como UTF-16LE. Si se interpreta como
            // UTF-8 aparecen NUL entre caracteres y la cabecera Bearer se invalida.
            // El saneado solo ocurre en memoria; no se escribe una copia nueva.
            var value = bytes.Length >= 2 && bytes[1] == 0
                ? Encoding.Unicode.GetString(bytes)
                : Encoding.UTF8.GetString(bytes);
            return value.Trim('\0', ' ', '\t', '\r', '\n');
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string target, string value)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("El destino de la credencial no puede estar vacío.", nameof(target));
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("La credencial no puede estar vacía.", nameof(value));

        var targetPointer = Marshal.StringToCoTaskMemUni(target);
        var userPointer = Marshal.StringToCoTaskMemUni("Flow");
        var secretBytes = Encoding.UTF8.GetBytes(value.Trim());
        var secretPointer = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new CREDENTIAL
            {
                Type = GenericCredentialType,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = PersistLocalMachine,
                UserName = userPointer
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"Windows no pudo guardar la credencial ({Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(secretPointer);
            Marshal.FreeCoTaskMem(userPointer);
            Marshal.FreeCoTaskMem(targetPointer);
        }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, GenericCredentialType, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 116) throw new InvalidOperationException($"Windows no pudo eliminar la credencial ({error}).");
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(nint credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }
}
