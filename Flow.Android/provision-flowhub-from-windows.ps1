param(
    [Parameter(Mandatory = $true)]
    [string]$ServerUrl,
    [string]$DeviceSerial = "97c290d2"
)

$ErrorActionPreference = "Stop"

# One-shot provisioning for the debug APK on the known test tablet. The token
# is read from Windows Credential Manager and streamed to the app-private
# staging file; it never appears in command arguments, logs, or repository files.
$flowDeviceSerial = $DeviceSerial
$flowPackage = "com.pablo.flow"
$flowStagingFile = "no_backup/.flow-provisioning-flowhub"
$flowParsedUrl = [Uri]$ServerUrl.Trim()
$flowIsTailscaleHttp = $false
if ($flowParsedUrl.Scheme -eq "http") {
    try {
        $flowAddress = [System.Net.IPAddress]::Parse($flowParsedUrl.Host).GetAddressBytes()
        $flowIsTailscaleHttp = $flowAddress.Length -eq 4 -and $flowAddress[0] -eq 100 -and $flowAddress[1] -ge 64 -and $flowAddress[1] -le 127
    } catch { $flowIsTailscaleHttp = $false }
}
if (($flowParsedUrl.Scheme -ne "https" -and -not $flowIsTailscaleHttp) -or [string]::IsNullOrWhiteSpace($flowParsedUrl.Host)) {
    throw "La URL de FlowHub debe ser HTTPS o una IP Tailscale 100.64.0.0/10."
}

$flowAdbCommand = Get-Command adb.exe -ErrorAction Stop
$flowAdbPath = $flowAdbCommand.Source
$flowDeviceList = & $flowAdbPath devices
if (-not ($flowDeviceList | Select-String -Pattern "^$flowDeviceSerial\s+device\s*$")) {
    throw "La tablet Flow ($flowDeviceSerial) no está conectada o no está autorizada por ADB."
}

$flowCredentialSource = @'
#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class FlowHubCredentialReader
{
    public static string? Read(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = bytes.Length >= 2 && bytes[1] == 0
                ? Encoding.Unicode.GetString(bytes)
                : Encoding.UTF8.GetString(bytes);
            return value.Trim('\0', ' ', '\t', '\r', '\n');
        }
        finally { CredFree(pointer); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credential);

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
if (-not ("FlowHubCredentialReader" -as [type])) {
    Add-Type -TypeDefinition $flowCredentialSource
}

$flowSecret = [FlowHubCredentialReader]::Read("Flow/FlowHubAppToken")
if ([string]::IsNullOrWhiteSpace($flowSecret)) {
    throw "No se encontró Flow/FlowHubAppToken en el Administrador de credenciales de Windows."
}
if ($flowSecret -notmatch '^[\x21-\x7E]+$') {
    throw "El token de FlowHub contiene caracteres no válidos."
}

try {
    & $flowAdbPath -s $flowDeviceSerial shell run-as $flowPackage rm -f $flowStagingFile | Out-Null

    $flowStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $flowStartInfo.FileName = $flowAdbPath
    foreach ($flowArgument in @(
        "-s", $flowDeviceSerial, "shell", "run-as", $flowPackage, "sh", "-c",
        ('"cat > ' + $flowStagingFile + '"')
    )) {
        [void]$flowStartInfo.ArgumentList.Add($flowArgument)
    }
    $flowStartInfo.UseShellExecute = $false
    $flowStartInfo.CreateNoWindow = $true
    $flowStartInfo.RedirectStandardInput = $true
    $flowStartInfo.RedirectStandardOutput = $true
    $flowStartInfo.RedirectStandardError = $true
    $flowProcess = [System.Diagnostics.Process]::new()
    $flowProcess.StartInfo = $flowStartInfo
    [void]$flowProcess.Start()
    $flowProcess.StandardInput.Write($flowParsedUrl.AbsoluteUri)
    $flowProcess.StandardInput.Write("`n")
    $flowProcess.StandardInput.Write($flowSecret)
    $flowProcess.StandardInput.Close()
    $flowProcess.WaitForExit()
    $flowError = $flowProcess.StandardError.ReadToEnd()
    if ($flowProcess.ExitCode -ne 0) {
        throw "No se pudo transferir la configuración a la tablet: $flowError"
    }

    & $flowAdbPath -s $flowDeviceSerial shell am force-stop $flowPackage | Out-Null
    & $flowAdbPath -s $flowDeviceSerial shell am start -W -n "$flowPackage/.MainActivity" | Out-Null
    Write-Host "FlowHub configurado en Flow Android y el token se ha guardado en Android Keystore."
}
finally {
    $flowSecret = $null
}
