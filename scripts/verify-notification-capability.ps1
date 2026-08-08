$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class PackageIdentityProbe
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        StringBuilder packageFullName);

    public static string GetPackageFullName()
    {
        int length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (length <= 0)
        {
            return null;
        }

        var value = new StringBuilder(length);
        result = GetCurrentPackageFullName(ref length, value);
        return result == 0 ? value.ToString() : null;
    }
}
'@

$apiType = [Type]::GetType(
    'Windows.Foundation.Metadata.ApiInformation, Windows, ContentType=WindowsRuntime')
$apiPresent = $false
if ($null -ne $apiType) {
    $isTypePresent = $apiType.GetMethod(
        'IsTypePresent',
        [type[]]@([string]))
    if ($null -ne $isTypePresent) {
        $apiPresent = $isTypePresent.Invoke(
            $null,
            @('Windows.UI.Notifications.Management.UserNotificationListener'))
    }
}

$packageFullName = [PackageIdentityProbe]::GetPackageFullName()
$manifestPath = Join-Path $PSScriptRoot '..\packaging\Package.appxmanifest.template'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$capabilityNode = $manifest.Package.Capabilities.ChildNodes |
    Where-Object {
        $_.LocalName -eq 'Capability' -and
        $_.NamespaceURI -eq 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3' -and
        $_.Attributes['Name'].Value -eq 'userNotificationListener'
    } |
    Select-Object -First 1

[PSCustomObject]@{
    ApiPresent = [bool]$apiPresent
    CurrentProcessHasPackageIdentity = -not [string]::IsNullOrWhiteSpace(
        $packageFullName)
    CurrentPackageFullName = $packageFullName
    ManifestDeclaresUserNotificationListener = $null -ne $capabilityNode
    ManifestTemplate = [IO.Path]::GetFullPath($manifestPath)
} | ConvertTo-Json
