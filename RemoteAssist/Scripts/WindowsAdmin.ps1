param([Parameter(Mandatory=$true)][string]$RequestPath)
$ErrorActionPreference = 'Stop'
$taskDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($RequestPath))
$jobRoot = [IO.Path]::GetFullPath('C:\ProgramData\RemoteDesk\Jobs') + '\'
if (-not $taskDirectory.StartsWith($jobRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($taskDirectory) -notmatch '^[a-f0-9]{32}$') { throw 'Invalid job directory' }
$request = Get-Content -LiteralPath $RequestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$payload = $request.Payload
$options = $request.Options
$script:completed = $false
function Finish([string]$state, [string]$detail, $data = $null) {
    $json = @{ State=$state; Detail=$detail; Data=$data } | ConvertTo-Json -Depth 14 -Compress
    [IO.File]::WriteAllText((Join-Path $taskDirectory 'result.tmp'), $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath (Join-Path $taskDirectory 'result.tmp') -Destination (Join-Path $taskDirectory 'result.json') -Force
    $script:completed = $true
}
function Cancelled { return Test-Path -LiteralPath (Join-Path $taskDirectory 'cancel.flag') }
function Lines($value) { return @(([string]$value -split '[\r\n;]+' | ForEach-Object { $_.Trim() }) | Where-Object { $_ }) }
function Norm($value) { return ([string]$value).Trim().Trim('{','}').ToUpperInvariant() }
function Verify-Identity {
    $uuid = (Get-CimInstance Win32_ComputerSystemProduct).UUID
    $serial = (Get-CimInstance Win32_BIOS).SerialNumber
    $matches = $false
    if ($request.ExpectedUuid) { if ((Norm $uuid) -ne (Norm $request.ExpectedUuid)) { throw 'Аппаратный UUID изменился.' }; $matches = $true }
    if ($request.ExpectedSerial) { if ((Norm $serial) -ne (Norm $request.ExpectedSerial)) { throw 'Серийный номер изменился.' }; $matches = $true }
    if (-not $matches) { throw 'Нет подтвержденного аппаратного ID.' }
}
function Get-AdminStatus([string]$sid, [string]$account) {
    try {
        Add-Type -AssemblyName System.DirectoryServices.AccountManagement
        $local = Get-CimInstance Win32_UserAccount -Filter "SID='$sid'" | Select-Object -First 1
        $contextType = [System.DirectoryServices.AccountManagement.ContextType]::Domain
        $contextName = ($account -split '\\')[0]
        if ($local -and $local.LocalAccount) { $contextType = [System.DirectoryServices.AccountManagement.ContextType]::Machine; $contextName = $env:COMPUTERNAME }
        $context = [System.DirectoryServices.AccountManagement.PrincipalContext]::new($contextType, $contextName)
        try {
            $user = [System.DirectoryServices.AccountManagement.UserPrincipal]::FindByIdentity($context, [System.DirectoryServices.AccountManagement.IdentityType]::Sid, $sid)
            if (-not $user) { return $null }
            try {
                $tokenSids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                [void]$tokenSids.Add($sid)
                $groups = $user.GetAuthorizationGroups()
                try { foreach ($group in $groups) { if ($group.Sid) { [void]$tokenSids.Add($group.Sid.Value) }; $group.Dispose() } } finally { $groups.Dispose() }
                $adminGroup = Get-CimInstance Win32_Group -Filter "SID='S-1-5-32-544'"
                if (-not $adminGroup) { return $null }
                foreach ($member in @(Get-CimAssociatedInstance -InputObject $adminGroup -Association Win32_GroupUser)) {
                    if (-not $member.SID) { return $null }
                    if ($tokenSids.Contains([string]$member.SID)) { return $true }
                }
                return $false
            } finally { $user.Dispose() }
        } finally { $context.Dispose() }
    } catch { return $null }
}
function Get-ProfileRecord($profile) {
    $login = ''
    try { $login = ([Security.Principal.SecurityIdentifier]::new([string]$profile.SID)).Translate([Security.Principal.NTAccount]).Value } catch { }
    $root = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList').ProfilesDirectory)
    $safe = $false
    try {
        $path = [IO.Path]::GetFullPath([string]$profile.LocalPath)
        $safe = $path.StartsWith(([IO.Path]::GetFullPath($root).TrimEnd('\')+'\'), [StringComparison]::OrdinalIgnoreCase) -and ($path -match '^[A-Za-z]:\\')
        if (Test-Path -LiteralPath $path) { if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { $safe = $false } }
        else { $safe = $false }
    } catch { $safe = $false }
    $admin = $null
    if ($login -and -not $profile.Special -and -not $profile.Loaded) { $admin = Get-AdminStatus $profile.SID $login }
    return @{ Sid=[string]$profile.SID; Login=$login; Path=[string]$profile.LocalPath; Loaded=[bool]$profile.Loaded; Special=[bool]$profile.Special; Administrator=$admin; LocalPathSafe=$safe; Reason='' }
}
function Profile-Reason($p) {
    $name = ($p.Login -split '\\')[-1] -split '@' | Select-Object -First 1
    if ($p.Special -or $p.Sid -match '-500$' -or $p.Sid -in @('S-1-5-18','S-1-5-19','S-1-5-20')) { return 'Системный профиль / встроенный администратор' }
    foreach ($rule in @('admin','administrator','администратор') + @(Lines $options.ProtectedUsers)) { if ((Match-Rule $name $rule) -or (Match-Rule $p.Login $rule)) { return 'Защищенная учетная запись' } }
    if ($p.Loaded) { return 'Профиль используется' }
    if ($null -eq $p.Administrator) { return 'Права владельца не подтверждены' }
    if ($p.Administrator) { return 'Администратор' }
    if (-not $p.LocalPathSafe) { return 'Небезопасный путь профиля' }
    foreach ($rule in @(Lines $options.StudentMasks)) { if ($rule -match '[^*?]' -and ((Match-Rule $p.Login $rule) -or (Match-Rule $name $rule))) { return '' } }
    return 'Не соответствует списку студентов'
}
function Match-Rule([string]$name, [string]$rule) { return [regex]::IsMatch($name, '^'+[regex]::Escape($rule).Replace('\*','.*')+'$', [Text.RegularExpressions.RegexOptions]::IgnoreCase) }
function Init-Wts {
    if ('DeskWts' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class DeskWts {
 [StructLayout(LayoutKind.Sequential)] struct SI { public int Id; public IntPtr Name; public int State; }
 [DllImport("wtsapi32.dll")] static extern bool WTSEnumerateSessions(IntPtr server,int reserved,int version,out IntPtr pointer,out int count);
 [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode)] static extern bool WTSQuerySessionInformation(IntPtr server,int session,int cls,out IntPtr pointer,out int count);
 [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WTSSendMessage(IntPtr server,int session,string title,int titleBytes,string message,int messageBytes,int style,int timeout,out int response,bool wait);
 [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr pointer);
 public class Session { public int Id; public string User; public int State; }
 static string Get(int id,int cls) { IntPtr p;int n;if(!WTSQuerySessionInformation(IntPtr.Zero,id,cls,out p,out n))throw new Exception("WTS: не удалось подтвердить сеанс");try{return Marshal.PtrToStringUni(p)??"";}finally{WTSFreeMemory(p);} }
 public static Session[] Sessions(){ IntPtr p;int n;if(!WTSEnumerateSessions(IntPtr.Zero,0,1,out p,out n))throw new Exception("WTS: перечисление сеансов недоступно");var r=new List<Session>();try{for(int i=0;i<n;i++){var s=(SI)Marshal.PtrToStructure(IntPtr.Add(p,i*Marshal.SizeOf(typeof(SI))),typeof(SI));var u=Get(s.Id,5);if(u.Length>0)r.Add(new Session{Id=s.Id,User=Get(s.Id,7)+"\\"+u,State=s.State});}}finally{WTSFreeMemory(p);}return r.ToArray();}
 public static int Message(int id,string text,int style,int timeout,bool wait){int result; if(!WTSSendMessage(IntPtr.Zero,id,"Remote Desk",22,text,text.Length*2,style,timeout,out result,wait))throw new Exception("WTS: уведомление не доставлено");return result;}
}
'@
}
function Sessions-Key($sessions) { return ((@($sessions) | Sort-Object Id | ForEach-Object { "$($_.Id):$($_.User):$($_.State)" }) -join ';') }
function Wait-Warning {
    Init-Wts
    $sessions = @([DeskWts]::Sessions())
    foreach ($s in $sessions) { if ($s.State -ne 0) { throw 'Есть отключенный/неактивный пользовательский сеанс: действие отложено.' }; [void][DeskWts]::Message($s.Id, 'Через 120 секунд компьютер будет выключен или перезагружен в Astra. Сохраните работу.', 0, 120, $false) }
    for ($i=0; $i -lt 120; $i++) { if (Cancelled) { throw 'Отменено до изменения системы.' }; Start-Sleep -Seconds 1 }
    if ((Sessions-Key $sessions) -ne (Sessions-Key @([DeskWts]::Sessions()))) { throw 'Состав сеансов изменился; действие отложено.' }
}
try {
    if ($request.Operation -ne 'Probe') { Verify-Identity }
    switch ($request.Operation) {
        'Probe' {
            $os = Get-CimInstance Win32_OperatingSystem
            $system = Get-CimInstance Win32_ComputerSystem
            $product = Get-CimInstance Win32_ComputerSystemProduct
            $bios = Get-CimInstance Win32_BIOS
            $services = @(foreach ($name in @(Lines $options.WindowsServices)) { $s = Get-Service -Name $name -ErrorAction SilentlyContinue; @{Name=$name; State= $(if($s){[string]$s.Status}else{'NotInstalled'})} })
            $processes = @(); if ($payload.Details) { $processes = @(Get-CimInstance Win32_Process | Where-Object { $_.CreationDate } | ForEach-Object {
                $owner=''; try { $o=Invoke-CimMethod -InputObject $_ -MethodName GetOwner; if($o.ReturnValue -eq 0){$owner="$($o.Domain)\$($o.User)"} } catch {}
                @{Id=[int]$_.ProcessId;Name=[string]$_.Name;Path=[string]$_.ExecutablePath;User=$owner;Session=[string]$_.SessionId;Created=$_.CreationDate.ToUniversalTime().ToString('o')}
            }) }
            $physical = @(); try { $physical = @(Get-PhysicalDisk -ErrorAction Stop) } catch {}
            $disks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
                $logical = $_; $health = 'Нет данных'; $states = @()
                try {
                    foreach($partition in @(Get-CimAssociatedInstance -InputObject $logical -Association Win32_LogicalDiskToPartition)) {
                        foreach($drive in @(Get-CimAssociatedInstance -InputObject $partition -Association Win32_DiskDriveToDiskPartition)) {
                            if($drive.SerialNumber) {
                                $matched = @($physical | Where-Object { $_.SerialNumber -and $_.SerialNumber.Trim() -eq $drive.SerialNumber.Trim() })
                                if($matched.Count -eq 1){$states += [string]$matched[0].HealthStatus}
                            }
                        }
                    }
                    if($states.Count){$health='Windows Storage: '+(($states | Sort-Object -Unique) -join ', ')}
                } catch {}
                @{Name=$logical.DeviceID; Size=[long]$logical.Size; Free=[long]$logical.FreeSpace; Health=$health}
            })
            $data = @{Os='Windows';Uuid=$product.UUID;Serial=$bios.SerialNumber;HostName=$env:COMPUTERNAME;Version=$os.Caption+' '+$os.Version;Architecture=$(if($os.OSArchitecture -like '64*'){'x64'}else{'x86'});BootId=$os.LastBootUpTime.ToUniversalTime().ToString('o');User=[string]$system.UserName;Reachable=$true;State='Управление доступно';Disks=$disks;Services=$services;Processes=$processes;Hardware=@{Manufacturer=$system.Manufacturer;Model=$system.Model;MemoryBytes=[string]$system.TotalPhysicalMemory;CPU=((Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name) -join ', ')};Diagnostics=@{WMI='Доступен';SMB='Доступен';PowerShell='Доступен';GRUB='Обычная перезагрузка возвращает в Astra; проверьте тестовым переключением'}}
            Finish 'Succeeded' 'Состояние Windows получено' $data
        }
        'Profiles' { $records = @(Get-CimInstance Win32_UserProfile | ForEach-Object { $p=Get-ProfileRecord $_; $p.Reason=Profile-Reason $p; $p }); Finish 'Succeeded' 'Профили проверены' $records }
        'Cleanup' {
            $results = @(); $failed = $false
            foreach ($expected in @($payload.Profiles)) {
                if (Cancelled) { $failed=$true; $results += 'Отмена: оставшиеся профили пропущены'; break }
                if ($expected.Sid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$') { $failed=$true; $results += 'Некорректный SID'; continue }
                $profile = Get-CimInstance Win32_UserProfile -Filter "SID='$($expected.Sid)'" | Select-Object -First 1
                if (-not $profile) { $results += "$($expected.Sid): уже отсутствует"; continue }
                $current = Get-ProfileRecord $profile; $reason=Profile-Reason $current
                if ($reason -or $current.Path -ne $expected.Path -or $current.Login -ne $expected.Login) { $failed=$true; $results += "$($expected.Sid): пропущен — $reason / проверка идентичности"; continue }
                try {
                    # Re-read immediately before the provider operation. Never delete directories or registry trees manually.
                    $profile = Get-CimInstance Win32_UserProfile -Filter "SID='$($expected.Sid)'"
                    if ($profile.Loaded -or $profile.Special -or $profile.LocalPath -ne $current.Path) { throw 'Профиль изменился' }
                    $profile | Remove-CimInstance
                    if ((Get-CimInstance Win32_UserProfile -Filter "SID='$($expected.Sid)'") -or (Test-Path -LiteralPath $current.Path) -or (Test-Path -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$($expected.Sid)")) { throw 'Провайдер не подтвердил полное удаление профиля' }
                    $results += "$($expected.Sid): профиль удален"
                } catch { $failed=$true; $results += "$($expected.Sid): $($_.Exception.Message)" }
            }
            Finish $(if($failed){'Deferred'}else{'Succeeded'}) ($results -join "`n")
        }
        'Power' {
            if ($payload.Action -notin @('Shutdown','RestartAstra')) { throw 'Windows поддерживает выключение или перезагрузку в Astra.' }
            Wait-Warning; Verify-Identity
            $exe = Join-Path $env:SystemRoot 'System32\shutdown.exe'
            # /soft allows applications to veto instead of destroying unsaved data. No /f and no nonzero /t (which implies /f).
            $mode = if ($payload.Action -eq 'Shutdown') { '/s' } else { '/r' }
            $p = Start-Process -FilePath $exe -ArgumentList @($mode,'/soft','/t','0') -WindowStyle Hidden -PassThru -Wait
            if ($p.ExitCode -ne 0) { throw 'Windows отклонила выключение/перезагрузку.' }
            Finish 'Uncertain' 'Запрос питания принят. Фактическое завершение и загрузка требуют проверки.'
        }
        'Install' {
            $pkg=$payload.Package
            if ($pkg.Os -ne 'Windows' -or $pkg.Kind -notin @('MSI','EXE')) { throw 'Пакет не для Windows' }
            if ($pkg.Kind -eq 'EXE' -and (-not $pkg.SilentNoRestartVerified -or -not $pkg.Arguments)) { throw 'Нет проверенных параметров EXE' }
            $file=Join-Path $taskDirectory ('package.'+$pkg.Kind.ToLowerInvariant())
            if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $pkg.Sha256) { throw 'SHA-256 пакета не совпал' }
            $arch=if([Environment]::Is64BitOperatingSystem){'x64'}else{'x86'}
            if($pkg.Architecture -notin @('any',$arch) -and -not ($pkg.Architecture -eq 'x86' -and $arch -eq 'x64')) { throw 'Архитектура пакета не подходит' }
            if($pkg.Detection -notmatch '^[A-Za-z]:\\' -or $pkg.Detection.Contains('"')) { throw 'Нужен полный локальный путь EXE для проверки версии' }
            if ((Test-Path -LiteralPath $pkg.Detection) -and (Get-Item -LiteralPath $pkg.Detection).VersionInfo.FileVersion -eq $pkg.Version) { Finish 'Succeeded' 'Нужная версия уже установлена'; break }
            if(Cancelled){throw 'Отменено до установки'}
            if ($pkg.Kind -eq 'MSI') { $installer=Join-Path $env:SystemRoot 'System32\msiexec.exe'; $argsLine='/i "'+$file+'" /qn /norestart REBOOT=ReallySuppress /L*v "'+(Join-Path $taskDirectory 'installer.log')+'"' }
            else { $installer=$file; $argsLine=[string]$pkg.Arguments }
            $p=Start-Process -FilePath $installer -ArgumentList $argsLine -WindowStyle Hidden -PassThru -Wait
            if($p.ExitCode -eq 1618){Finish 'Deferred' 'Другая установка уже выполняется'; break}
            if($p.ExitCode -eq 1641){Finish 'Uncertain' 'Установщик инициировал перезагрузку; проверьте ОС и версию'; break}
            if($p.ExitCode -notin @(0,3010)){throw "Установщик завершился с кодом $($p.ExitCode)"}
            if(-not (Test-Path -LiteralPath $pkg.Detection) -or (Get-Item -LiteralPath $pkg.Detection).VersionInfo.FileVersion -ne $pkg.Version){Finish 'Uncertain' 'Установщик завершен, ожидаемая версия не подтверждена'; break}
            Finish $(if($p.ExitCode -eq 3010){'RebootRequired'}else{'Succeeded'}) ('Версия подтверждена: '+$pkg.Version)
        }
        'RestartApplication' {
            Init-Wts; $app=$payload.Application; $expected=$payload.Process
            if($app.Os -ne 'Windows' -or $app.Path -ne $expected.Path -or $app.Path -notmatch '^[A-Za-z]:\\'){throw 'Приложение не соответствует разрешенному пути'}
            $process=Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$expected.Id)"
            if(-not $process -or $process.ExecutablePath -ne $expected.Path -or $process.CreationDate.ToUniversalTime().ToString('o') -ne $expected.Created){throw 'Процесс изменился'}
            $owner=Invoke-CimMethod -InputObject $process -MethodName GetOwner
            if($owner.ReturnValue -ne 0 -or "$($owner.Domain)\$($owner.User)" -ne $expected.User){throw 'Владелец изменился'}
            $session=@([DeskWts]::Sessions() | Where-Object { $_.Id -eq [int]$expected.Session -and $_.User -eq $expected.User -and $_.State -eq 0 })
            if($session.Count -ne 1){throw 'Активный сеанс не подтвержден'}
            $answer=[DeskWts]::Message([int]$expected.Session,'Администратор предлагает перезапустить приложение. Сохраните работу. Через 15 секунд после обычного закрытия зависший процесс может быть завершен, несохраненные данные будут потеряны. Разрешить?',0x104,120,$true)
            if($answer -ne 6){Finish 'Deferred' 'Пользователь не разрешил перезапуск'; break}
            if(Cancelled){throw 'Отменено'}
            $again=Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$expected.Id)"
            if(-not $again -or $again.CreationDate.ToUniversalTime().ToString('o') -ne $expected.Created -or $again.ExecutablePath -ne $app.Path){throw 'Процесс изменился после согласия'}
            $taskName='RemoteDesk-'+[IO.Path]::GetFileName($taskDirectory)
            $taskService=New-Object -ComObject Schedule.Service; $taskService.Connect(); $root=$taskService.GetFolder('\'); $task=$taskService.NewTask(0)
            $task.Principal.UserId=$expected.User; $task.Principal.LogonType=3; $task.Principal.RunLevel=0
            $task.Settings.ExecutionTimeLimit='PT0S'; $task.Settings.AllowDemandStart=$true; $task.Settings.DisallowStartIfOnBatteries=$false; $task.Settings.StopIfGoingOnBatteries=$false
            $action=$task.Actions.Create(0); $action.Path=$app.Path; $action.Arguments=[string]$app.Arguments; $action.WorkingDirectory=[IO.Path]::GetDirectoryName($app.Path)
            $registered=$root.RegisterTaskDefinition($taskName,$task,6,$expected.User,$null,3,$null)
            try {
                & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID ([string]$expected.Id) 2>$null | Out-Null
                Start-Sleep -Seconds 15
                $remaining=Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$expected.Id)"
                if($remaining){if($remaining.CreationDate.ToUniversalTime().ToString('o') -ne $expected.Created){throw 'PID повторно использован'}; $r=Invoke-CimMethod -InputObject $remaining -MethodName Terminate; if($r.ReturnValue -ne 0){throw 'Процесс не завершен'}}
                if(@([DeskWts]::Sessions() | Where-Object {$_.Id -eq [int]$expected.Session -and $_.User -eq $expected.User -and $_.State -eq 0}).Count -ne 1){throw 'Сеанс изменился; повторный запуск отменен'}
                [void]$registered.RunEx($null,4,[int]$expected.Session,$null)
                Start-Sleep -Seconds 3
                $new=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq $app.Path -and $_.SessionId -eq [int]$expected.Session})
                if($new.Count -eq 0){Finish 'Uncertain' 'Команда запуска отправлена, новый процесс не подтвержден'}else{Finish 'Succeeded' 'Приложение перезапущено в исходном сеансе'}
            } finally { $root.DeleteTask($taskName,0) }
        }
        default { throw 'Неизвестная операция' }
    }
} catch { if(-not $script:completed){Finish $(if($request.Operation -in @('Power','RestartApplication')){'Deferred'}else{'Failed'}) $_.Exception.Message} }
