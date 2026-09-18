$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '..\RemoteAssist\Scripts\WindowsAdmin.ps1'
$tokens = $null; $errors = $null
$source = [IO.File]::ReadAllText((Resolve-Path $path), [Text.Encoding]::UTF8)
# The adapter uploads this UTF-8 source with a BOM for Windows PowerShell 5.1.
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors.Message -join "`n") }
# Load only pure policy functions, never the script entrypoint or functions touching the computer.
foreach ($name in @('Lines','Match-Rule','Profile-Reason')) {
    $function = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}
$options = [pscustomobject]@{StudentMasks='student*';ProtectedUsers='admin'}
function Profile { return @{Sid='S-1-5-21-1-2-3-1001';Login='DOMAIN\student01';Path='C:\Users\student01';Loaded=$false;Special=$false;Administrator=$false;LocalPathSafe=$true} }
$cases = 0
function Check($condition,$description) { if(-not $condition){throw $description};$script:cases++ }
Check ((Profile-Reason (Profile)) -eq '') 'Ordinary student eligible'
$p=Profile;$p.Loaded=$true;Check ((Profile-Reason $p) -ne '') 'Loaded profile protected'
$p=Profile;$p.Special=$true;Check ((Profile-Reason $p) -ne '') 'System profile protected'
$p=Profile;$p.Login='PC\admin';Check ((Profile-Reason $p) -ne '') 'admin protected'
$p=Profile;$p.Sid='S-1-5-21-1-2-3-500';Check ((Profile-Reason $p) -ne '') 'Renamed built-in administrator protected'
$p=Profile;$p.Administrator=$true;Check ((Profile-Reason $p) -ne '') 'Nested administrator protected'
$p=Profile;$p.Administrator=$null;Check ((Profile-Reason $p) -ne '') 'Unknown privileges protected'
$p=Profile;$p.LocalPathSafe=$false;Check ((Profile-Reason $p) -ne '') 'Unsafe profile root protected'
$p=Profile;$p.Login='DOMAIN\teacher01';Check ((Profile-Reason $p) -ne '') 'Teacher protected'
$options.StudentMasks='';Check ((Profile-Reason (Profile)) -ne '') 'Empty allowlist deletes nothing'
$options.StudentMasks='*';Check ((Profile-Reason (Profile)) -ne '') 'Universal allowlist deletes nothing'
# Run the actual installer branch against command doubles, never start a process or access an installer.
$switch = $ast.Find({param($node) $node -is [Management.Automation.Language.SwitchStatementAst]}, $true)
$installBody = ($switch.Clauses | Where-Object {$_.Item1.Value -eq 'Install'}).Item2.Extent.Text
$installerCase = [scriptblock]::Create("switch ('Install') { 'Install' $installBody }")
$taskDirectory = Join-Path $PSScriptRoot 'synthetic-job'
$script:starts=0; $script:hash='AAA'; $script:version='0'; $script:exitCode=0; $script:result=$null
function Get-FileHash { param($LiteralPath,$Algorithm) return @{Hash=$script:hash} }
function Test-Path { param($LiteralPath) return $true }
function Get-Item { param($LiteralPath) return @{VersionInfo=@{FileVersion=$script:version}} }
function Cancelled { return $false }
function Start-Process { param($FilePath,$ArgumentList,$WindowStyle,[switch]$PassThru,[switch]$Wait) $script:starts++; $script:installerArgs=$ArgumentList; $script:version='1.2'; return @{ExitCode=$script:exitCode} }
function Finish { param($state,$detail,$data) $script:result=$state }
$payload=@{Package=@{Os='Windows';Kind='MSI';Architecture='any';Sha256='BBB';Detection='C:\Test\app.exe';Version='1.2'}}
$thrown=$false;try{& $installerCase}catch{$thrown=$true};Check ($thrown -and $script:starts -eq 0) 'Hash mismatch prevents Windows installer start'
$payload.Package.Sha256='AAA';$script:version='0';$script:exitCode=3010;& $installerCase
Check ($script:result -eq 'RebootRequired' -and $script:installerArgs.Contains('/norestart') -and $script:installerArgs.Contains('REBOOT=ReallySuppress')) 'MSI suppresses restart and reports 3010'
$script:version='0';$script:exitCode=1618;& $installerCase;Check ($script:result -eq 'Deferred') 'Busy Windows Installer deferred'
$script:version='0';$script:exitCode=1641;& $installerCase;Check ($script:result -eq 'Uncertain') 'Unexpected installer restart stays unconfirmed'
$payload.Package.Kind='EXE';$payload.Package.SilentNoRestartVerified=$false;$script:version='0';$before=$script:starts;$thrown=$false;try{& $installerCase}catch{$thrown=$true};Check ($thrown -and $script:starts -eq $before) 'Unverified EXE arguments prevent execution'
"PASS: Windows helper syntax and $cases policy/installer checks; no system operations executed."
