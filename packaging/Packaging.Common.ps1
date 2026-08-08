function Get-RepositoryRoot {
    return [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot ".."))
}

function Get-RepositoryDotnet {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    $dotnetPath = Join-Path $RepositoryRoot ".dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
        throw "Repository-local dotnet was not found: $dotnetPath"
    }

    return $dotnetPath
}

function Assert-PathWithinRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string] $CandidatePath
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar

    if (($candidate -eq $root) -or
        (-not $candidate.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase))) {
        throw "Path must be a repository child: $candidate"
    }

    return $candidate
}

function Reset-RepositoryDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string] $DirectoryPath
    )

    $resolved = Assert-PathWithinRepository `
        -RepositoryRoot $RepositoryRoot `
        -CandidatePath $DirectoryPath
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    return $resolved
}

function Resolve-WindowsSdkTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [ValidateSet("makeappx.exe", "signtool.exe")]
        [string] $ToolName,

        [string] $BuildToolsVersion = "10.0.28000.2526",

        [switch] $Restore
    )

    $packageRoot = Join-Path $RepositoryRoot (
        ".nuget\packages\microsoft.windows.sdk.buildtools\" +
        $BuildToolsVersion)

    if ((-not (Test-Path `
            -LiteralPath $packageRoot `
            -PathType Container)) -and $Restore) {
        $dotnetPath = Get-RepositoryDotnet -RepositoryRoot $RepositoryRoot
        $toolProject = Join-Path $RepositoryRoot (
            "packaging\tools\WindowsSdkBuildTools.csproj")
        $packagesPath = Join-Path $RepositoryRoot ".nuget\packages"
        $nugetConfig = Join-Path $RepositoryRoot "NuGet.Config"

        & $dotnetPath restore $toolProject `
            --configfile $nugetConfig `
            --packages $packagesPath `
            --verbosity minimal |
            Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restore Microsoft.Windows.SDK.BuildTools."
        }
    }

    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw (
            "Windows SDK build tools are missing. Run the packaging " +
            "command without -SkipRestore first.")
    }

    $candidates = @(
        Get-ChildItem `
            -LiteralPath $packageRoot `
            -Recurse `
            -File `
            -Filter $ToolName `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.FullName -match '[\\/]x64[\\/]'
            } |
            Sort-Object FullName -Descending
    )
    if ($candidates.Count -eq 0) {
        throw "$ToolName was not found in $packageRoot."
    }

    return $candidates[0].FullName
}

function Get-CodeSigningCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Thumbprint
    )

    $normalizedThumbprint = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalizedThumbprint -notmatch '^[0-9A-F]{40}$') {
        throw "Certificate thumbprint must contain 40 hexadecimal characters."
    }

    $certificatePath = "Cert:\CurrentUser\My\$normalizedThumbprint"
    $certificate = Get-Item `
        -LiteralPath $certificatePath `
        -ErrorAction SilentlyContinue
    if ($null -eq $certificate) {
        throw "Code-signing certificate was not found: $normalizedThumbprint"
    }

    if (-not $certificate.HasPrivateKey) {
        throw "The selected certificate does not have an accessible private key."
    }

    $now = Get-Date
    if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -le $now) {
        throw "The selected certificate is not currently valid."
    }

    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    $hasCodeSigningEku = @(
        $certificate.EnhancedKeyUsageList |
            Where-Object {
                $objectId = $_.ObjectId
                $objectIdValue = if ($objectId -is [string]) {
                    $objectId
                }
                else {
                    $objectId.Value
                }

                $objectIdValue -eq $codeSigningOid
            }
    ).Count -gt 0
    if (-not $hasCodeSigningEku) {
        throw "The selected certificate is not valid for code signing."
    }

    return $certificate
}

function Get-SignToolArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Thumbprint,

        [string] $TimestampUrl,

        [switch] $SkipTimestamp,

        [Parameter(Mandatory = $true)]
        [string] $FilePath
    )

    $arguments = @(
        "sign",
        "/fd",
        "SHA256",
        "/sha1",
        $Thumbprint,
        "/s",
        "My"
    )
    if (-not $SkipTimestamp) {
        if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
            throw "TimestampUrl is required unless -SkipTimestamp is used."
        }

        $arguments += @("/tr", $TimestampUrl, "/td", "SHA256")
    }

    $arguments += $FilePath
    return $arguments
}
