function Initialize-O2PEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [string]$PublicUiOrigin = "http://27.147.159.194:3051"
    )

    $envFile = Join-Path $RootPath ".env"
    if (Test-Path $envFile) {
        foreach ($line in Get-Content $envFile) {
            if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith("#")) {
                continue
            }

            $parts = $line -split '=', 2
            if ($parts.Length -eq 2) {
                [Environment]::SetEnvironmentVariable($parts[0], $parts[1])
            }
        }
    }

    $settingsPath = Join-Path $RootPath "src\O2P.Api\appsettings.Development.json"
    $settings = @{}
    if (Test-Path $settingsPath) {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    }

    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production")
    [Environment]::SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production")

    if (-not [Environment]::GetEnvironmentVariable("ConnectionStrings__MetadataDb")) {
        $metadataDb = [Environment]::GetEnvironmentVariable("O2P_METADATA_DB")
        $metadataUser = [Environment]::GetEnvironmentVariable("O2P_METADATA_USER")
        $metadataPassword = [Environment]::GetEnvironmentVariable("O2P_METADATA_PASSWORD")
        if ($metadataDb -and $metadataUser -and $metadataPassword) {
            $connString = "Host=application.naasbd.com;Port=7936;Database=$metadataDb;Username=$metadataUser;Password=$metadataPassword"
            [Environment]::SetEnvironmentVariable("ConnectionStrings__MetadataDb", $connString)
        }
        elseif ($settings.ConnectionStrings.MetadataDb) {
            [Environment]::SetEnvironmentVariable("ConnectionStrings__MetadataDb", $settings.ConnectionStrings.MetadataDb)
        }
    }

    if (-not [Environment]::GetEnvironmentVariable("JwtSettings__Secret")) {
        $jwtSecret = [Environment]::GetEnvironmentVariable("O2P_JWT_SECRET")
        if (-not $jwtSecret) {
            $jwtSecret = $settings.JwtSettings.Secret
        }
        if ($jwtSecret) {
            [Environment]::SetEnvironmentVariable("JwtSettings__Secret", $jwtSecret)
        }
    }

    if (-not [Environment]::GetEnvironmentVariable("JwtSettings__Issuer") -and $settings.JwtSettings.Issuer) {
        [Environment]::SetEnvironmentVariable("JwtSettings__Issuer", $settings.JwtSettings.Issuer)
    }
    if (-not [Environment]::GetEnvironmentVariable("JwtSettings__Audience") -and $settings.JwtSettings.Audience) {
        [Environment]::SetEnvironmentVariable("JwtSettings__Audience", $settings.JwtSettings.Audience)
    }
    if (-not [Environment]::GetEnvironmentVariable("JwtSettings__ExpiryMinutes")) {
        [Environment]::SetEnvironmentVariable("JwtSettings__ExpiryMinutes", "10080")
    }

    if (-not [Environment]::GetEnvironmentVariable("BootstrapAdmin__Username")) {
        [Environment]::SetEnvironmentVariable("BootstrapAdmin__Username", "admin")
    }
    if (-not [Environment]::GetEnvironmentVariable("BootstrapAdmin__Email")) {
        [Environment]::SetEnvironmentVariable("BootstrapAdmin__Email", "admin@o2p.internal")
    }
    if (-not [Environment]::GetEnvironmentVariable("BootstrapAdmin__Password")) {
        $adminPassword = [Environment]::GetEnvironmentVariable("O2P_ADMIN_PASSWORD")
        if (-not $adminPassword) {
            $adminPassword = $settings.BootstrapAdmin.Password
        }
        if ($adminPassword) {
            [Environment]::SetEnvironmentVariable("BootstrapAdmin__Password", $adminPassword)
        }
    }

    [Environment]::SetEnvironmentVariable("Security__RequireHttps", "false")
    [Environment]::SetEnvironmentVariable("Security__Cors__AllowedOrigins__0", "http://localhost:3000")
    [Environment]::SetEnvironmentVariable("Security__Cors__AllowedOrigins__1", "http://127.0.0.1:3000")
    [Environment]::SetEnvironmentVariable("Security__Cors__AllowedOrigins__2", "http://localhost:3051")
    [Environment]::SetEnvironmentVariable("Security__Cors__AllowedOrigins__3", "http://127.0.0.1:3051")
    [Environment]::SetEnvironmentVariable("Security__Cors__AllowedOrigins__4", $PublicUiOrigin)
}
