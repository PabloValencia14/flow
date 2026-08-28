$ErrorActionPreference = 'Stop'

# Reads the machine-scoped FlowHub token from a trusted remote command through
# stdin and stores it locally in Windows Credential Manager. The token is never
# written to a file or echoed.
$inputText = [Console]::In.ReadToEnd()
$tokenMatch = [regex]::Match($inputText, '(?m)^\s*([A-Za-z0-9+/]{32,}={0,2})\s*$')
if (-not $tokenMatch.Success) {
    throw 'No se recibió un token FlowHub válido por la entrada estándar.'
}
$token = $tokenMatch.Groups[1].Value

$source = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class FlowHubTokenWriter
{
    public static void Write(string target, string value)
    {
        var targetPointer = Marshal.StringToCoTaskMemUni(target);
        var userPointer = Marshal.StringToCoTaskMemUni("Flow");
        var secretBytes = Encoding.UTF8.GetBytes(value);
        var secretPointer = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = 1,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = 2,
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
'@
if (-not ('FlowHubTokenWriter' -as [type])) {
    Add-Type -TypeDefinition $source
}

[FlowHubTokenWriter]::Write('Flow/FlowHubAppToken', $token)
Write-Output ([pscustomobject]@{ stored = $true; length = $token.Length } | ConvertTo-Json -Compress)
$token = $null
