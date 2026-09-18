# Updates only this machine's local inventory test instance. Does not touch other containers.
[CmdletBinding()]
param(
    [string]$AppPath = 'D:\RemoteAssist-inventory-build\RemoteDesk-Inventory-v1.1-win-x64\RemoteAssist.exe'
)
$ErrorActionPreference = 'Stop'
$container = 'remoteassist-inventory-local'
$image = 'remoteassist-inventory:1.1'
$root = 'D:\RemoteAssist-inventory-local'
$baseUri = 'http://127.0.0.1:5187'
function Docker-Checked {
    param([string[]]$Arguments)
    $result = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('Docker failed: ' + ($Arguments -join ' ')) }
    return $result
}
function Normalize-Record($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [string] -or $value -is [ValueType]) { return $value }
    if ($value -is [System.Collections.IEnumerable] -and $value -isnot [System.Collections.IDictionary]) {
        $array = @(foreach ($entry in $value) { Normalize-Record $entry })
        return ,$array
    }
    $normalized = [ordered]@{}
    foreach ($property in ($value.PSObject.Properties | Sort-Object Name)) {
        if ($property.Name -in @('kind','phoneNumber','parentAssetId')) { continue }
        $normalized[$property.Name] = Normalize-Record $property.Value
    }
    return $normalized
}
function Record-Signature($snapshot) {
    # Compare complete pre-existing records; only these new optional fields may appear.
    $records = foreach ($collection in 'rooms','assets','cartridges','photos','audits','history') {
        foreach ($record in ($snapshot.$collection | Sort-Object id)) {
            $fields = Normalize-Record $record
            $collection + ':' + ($fields | ConvertTo-Json -Depth 60 -Compress)
        }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($records -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($bytes)) } finally { $sha.Dispose() }
}
try {
    if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) { throw 'New application executable not found.' }
    $oldContainer = (Docker-Checked -Arguments @('inspect',$container) | ConvertFrom-Json)[0]
    $null = Docker-Checked -Arguments @('image','inspect',$image)
    foreach ($mapping in @(@('/data','data'), @('/backups','backups'), @('/data/config','config'))) {
        $mount = @($oldContainer.Mounts | Where-Object Destination -eq $mapping[0])
        if ($mount.Count -ne 1 -or $mount[0].Type -ne 'bind' -or
            [IO.Path]::GetFullPath($mount[0].Source) -ne [IO.Path]::GetFullPath((Join-Path $root $mapping[1]))) {
            throw ('Unexpected mount; stopped without changing Docker: ' + $mapping[0])
        }
    }
    # Never terminate forcefully: cancel the update if the application cannot close normally.
    foreach ($process in (Get-Process RemoteAssist -ErrorAction SilentlyContinue)) {
        if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000)) { throw 'Close RemoteAssist, save your edits, then run this script again.' }
    }
    $password = (Get-Content -Raw -LiteralPath (Join-Path $root 'config\owner-password.txt')).Trim()
    $login = Invoke-RestMethod -Method Post -Uri "$baseUri/api/login" -ContentType 'application/json' -Body (@{username='owner';password=$password} | ConvertTo-Json -Compress)
    $password = $null
    $headers = @{Authorization='Bearer ' + $login.token}
    $before = Invoke-RestMethod -Uri "$baseUri/api/snapshot" -Headers $headers
    $signature = Record-Signature $before
    $backup = Invoke-RestMethod -Method Post -Uri "$baseUri/api/backups" -Headers $headers
    if ($backup.lastError -or -not $backup.lastSuccessUtc) { throw 'Backup failed; update cancelled.' }
    Write-Host ('Backup completed. Equipment records: ' + @($before.assets).Count)
    if ($oldContainer.Config.Image -ne $image) {
        $rollback = $container + '-v1-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        $failedName = $container + '-failed-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        $renamed = $false
        $null = Docker-Checked -Arguments @('stop','--time','20',$container)
        try {
            $null = Docker-Checked -Arguments @('rename',$container,$rollback)
            $renamed = $true
            $null = Docker-Checked -Arguments @('run','-d','--name',$container,'--read-only','--tmpfs','/tmp:rw,noexec,nosuid,size=128m',
                '--cap-drop','ALL','--security-opt','no-new-privileges:true','-p','127.0.0.1:5187:8080',
                '--mount',"type=bind,source=$root\data,target=/data",'--mount',"type=bind,source=$root\backups,target=/backups",
                '--mount',"type=bind,source=$root\config,target=/data/config,readonly",'-e','ASPNETCORE_URLS=http://+:8080',
                '-e','INVENTORY_ALLOW_HTTP=true','-e','INVENTORY_OWNER_USERNAME=owner','-e','INVENTORY_OWNER_PASSWORD_FILE=/data/config/owner-password.txt',$image)
            $after = $null
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                try { $after = Invoke-RestMethod -Uri "$baseUri/api/snapshot" -Headers $headers -TimeoutSec 2; break }
                catch { Start-Sleep -Milliseconds 500 }
            }
            if ($null -eq $after -or $before.instanceId -ne $after.instanceId -or $signature -ne (Record-Signature $after)) {
                throw 'Server did not pass the record-preservation check.'
            }
            Write-Host ('Updated and verified. Old container retained (stopped): ' + $rollback)
        }
        catch {
            if ($renamed) {
                $names = Docker-Checked -Arguments @('ps','-a','--format','{{.Names}}')
                if ($names -contains $container) {
                    $null = Docker-Checked -Arguments @('stop','--time','20',$container)
                    $null = Docker-Checked -Arguments @('rename',$container,$failedName)
                }
                $null = Docker-Checked -Arguments @('rename',$rollback,$container)
            }
            $null = Docker-Checked -Arguments @('start',$container)
            throw
        }
    }
    Start-Process -FilePath $AppPath
    Write-Host 'Inventory 1.1 is ready. The application has been opened.'
}
catch {
    Write-Host ('Update stopped: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'No dataset or container was deleted.'
    exit 1
}
