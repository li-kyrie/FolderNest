param(
    [switch]$Run,
    [switch]$Review,
    [switch]$Settings,
    [switch]$Install,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, Microsoft.VisualBasic
[System.Windows.Forms.Application]::EnableVisualStyles()

$AppName = 'FolderNest'
$AppDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# Portable-first: data lives beside the program, so the whole folder can be
# moved to another drive. If that location is read-only, fall back to LocalAppData.
$DefaultDataDir = Join-Path $AppDir 'data'
$ConfigPath = Join-Path $DefaultDataDir 'config.json'
$StatePath = Join-Path $DefaultDataDir 'state.json'
$LogPath = Join-Path $DefaultDataDir 'foldernest.log'
$MaxLogBytes = 10MB

function Ensure-DataDir {
    $cfgDir = Split-Path -Parent $ConfigPath
    try {
        if (-not (Test-Path -LiteralPath $cfgDir)) { New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null }
        $probe = Join-Path $cfgDir '.write-test'; Set-Content -LiteralPath $probe -Value 'ok' -Encoding ASCII; Remove-Item -LiteralPath $probe -Force
    } catch {
        $script:ConfigPath = Join-Path (Join-Path $env:LOCALAPPDATA $AppName) 'config.json'
        $script:StatePath = Join-Path (Join-Path $env:LOCALAPPDATA $AppName) 'state.json'
        $script:LogPath = Join-Path (Join-Path $env:LOCALAPPDATA $AppName) 'foldernest.log'
        $cfgDir = Split-Path -Parent $ConfigPath
        if (-not (Test-Path -LiteralPath $cfgDir)) { New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null }
    }
}
function Save-JsonAtomic($Value, [string]$Path) {
    $tmp = "$Path.$PID.tmp"
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}
function Log([string]$Message) {
    Ensure-DataDir
    Add-Content -LiteralPath $LogPath -Value ("{0:yyyy-MM-dd HH:mm:ss}`t{1}" -f (Get-Date), $Message) -Encoding UTF8
    if ((Get-Item -LiteralPath $LogPath).Length -gt $MaxLogBytes) {
        $lines = @(Get-Content -LiteralPath $LogPath -Encoding UTF8)
        $keep = [Math]::Max(1, [int]($lines.Count * .6))
        $lines[($lines.Count - $keep)..($lines.Count - 1)] | Set-Content -LiteralPath $LogPath -Encoding UTF8
    }
}
function Load-Config {
    Ensure-DataDir
    if (Test-Path -LiteralPath $ConfigPath) { return Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    return [pscustomobject]@{ Root=''; Days=30; DeleteMode='RecycleBin'; ScanHour=2; ScanMinute=0 }
}
function Load-State {
    Ensure-DataDir
    if (Test-Path -LiteralPath $StatePath) { return Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    return [pscustomobject]@{ LastScanDate=''; Units=@() }
}
function New-Unit([string]$Path, [bool]$Folder, [datetime]$Latest, [int64]$Size, [int]$Count) {
    [pscustomobject]@{ Path=$Path; IsFolder=$Folder; Latest=$Latest.ToUniversalTime().ToString('o'); Size=$Size; Count=$Count }
}
function Get-Units([string]$Root) {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($f in @(Get-ChildItem -LiteralPath $Root -File -ErrorAction Stop)) {
        [void]$list.Add((New-Unit $f.FullName $false $f.LastWriteTimeUtc $f.Length 1))
    }
    foreach ($d in @(Get-ChildItem -LiteralPath $Root -Directory -ErrorAction Stop)) {
        $files = @(Get-ChildItem -LiteralPath $d.FullName -File -Recurse -ErrorAction SilentlyContinue)
        $latest = $d.LastWriteTimeUtc
        if ($files.Count -gt 0) { $latest = ($files | Measure-Object LastWriteTimeUtc -Maximum).Maximum }
        $size = [int64](($files | Measure-Object Length -Sum).Sum); if ($null -eq $size) { $size = 0 }
        [void]$list.Add((New-Unit $d.FullName $true $latest $size $files.Count))
    }
    return @($list)
}
function Find-StateUnit($State, [string]$Path) { @($State.Units | Where-Object { $_.Path -ieq $Path } | Select-Object -First 1)[0] }
function Scan([bool]$Notify) {
    $cfg = Load-Config
    if ([string]::IsNullOrWhiteSpace($cfg.Root) -or -not (Test-Path -LiteralPath $cfg.Root -PathType Container)) { Log '扫描跳过：未配置有效监控目录'; return 0 }
    $state = Load-State; $newUnits = New-Object System.Collections.Generic.List[object]; $cutoff = [datetime]::UtcNow.AddDays(-[int]$cfg.Days)
    foreach ($u in Get-Units $cfg.Root) {
        $old = Find-StateUnit $state $u.Path
        if ($null -eq $old) { $old = [pscustomobject]@{ Path=$u.Path; FirstSeen=[datetime]::UtcNow.ToString('o'); Pending=$false; IsFolder=$u.IsFolder } }
        $first = [datetime]::Parse($old.FirstSeen).ToUniversalTime(); $latest = [datetime]::Parse($u.Latest).ToUniversalTime()
        $effective = if ($latest -gt $first) { $latest } else { $first }
        $old.IsFolder = $u.IsFolder; $old.Latest = $u.Latest; $old.Size = $u.Size; $old.Count = $u.Count; $old.Effective = $effective.ToString('o')
        if ($effective -lt $cutoff) { $old.Pending = $true }
        [void]$newUnits.Add($old)
    }
    $state.Units = @($newUnits); $state.LastScanDate = (Get-Date).ToString('yyyy-MM-dd'); Save-JsonAtomic $state $StatePath
    $pending = @($state.Units | Where-Object Pending); Log ("扫描完成：待处理 {0} 项" -f $pending.Count)
    if ($Notify -and $pending.Count -gt 0) { Show-Notice $pending.Count }
    return $pending.Count
}
function Remove-ItemSafe($Record, [string]$Mode) {
    if (-not (Test-Path -LiteralPath $Record.Path)) { Log "跳过（不存在）：$($Record.Path)"; return $false }
    try {
        if ($Mode -eq 'RecycleBin') {
            if ($Record.IsFolder) { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($Record.Path, 'OnlyErrorDialogs', 'SendToRecycleBin') }
            else { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($Record.Path, 'OnlyErrorDialogs', 'SendToRecycleBin') }
        } else { Remove-Item -LiteralPath $Record.Path -Recurse:([bool]$Record.IsFolder) -Force }
        Log "已处理 [$Mode]：$($Record.Path)"; return $true
    } catch { Log "处理失败：$($Record.Path)；$($_.Exception.Message)"; return $false }
}
function Show-Notice([int]$Count) {
    $notify = New-Object System.Windows.Forms.NotifyIcon
    $notify.Icon = [System.Drawing.SystemIcons]::Information; $notify.Visible = $true
    $notify.BalloonTipTitle = 'FolderNest 到期提醒'; $notify.BalloonTipText = "有 $Count 个文件或文件夹达到保留期限，请打开 FolderNest 进行筛选。"
    $notify.ShowBalloonTip(10000); Start-Sleep -Seconds 3; $notify.Dispose()
}
function Review-Window {
    $state=Load-State; $pending=@($state.Units|Where-Object Pending); if($pending.Count -eq 0){[Windows.Forms.MessageBox]::Show('当前没有待处理项目。','FolderNest')|Out-Null;return}
    $form=New-Object Windows.Forms.Form; $form.Text='FolderNest 到期清单';$form.Width=900;$form.Height=560;$form.StartPosition='CenterScreen'
    $grid=New-Object Windows.Forms.DataGridView;$grid.Dock='Fill';$grid.AllowUserToAddRows=$false;$grid.AutoGenerateColumns=$false;$grid.SelectionMode='FullRowSelect';$grid.MultiSelect=$true
    $c=New-Object Windows.Forms.DataGridViewCheckBoxColumn;$c.HeaderText='处理';$c.Width=55;$grid.Columns.Add($c)|Out-Null
    foreach($s in @(@('类型','Type',75),@('路径','Path',500),@('有效时间','Effective',145),@('大小','Size',100))){$col=New-Object Windows.Forms.DataGridViewTextBoxColumn;$col.HeaderText=$s[0];$col.Name=$s[1];$col.Width=$s[2];$grid.Columns.Add($col)|Out-Null}
    foreach($p in $pending){$i=$grid.Rows.Add();$grid.Rows[$i].Cells[0].Value=$true;$grid.Rows[$i].Cells['Type'].Value=if($p.IsFolder){'文件夹'}else{'文件'};$grid.Rows[$i].Cells['Path'].Value=$p.Path;$grid.Rows[$i].Cells['Effective'].Value=([datetime]::Parse($p.Effective).ToLocalTime().ToString('yyyy-MM-dd HH:mm'));$grid.Rows[$i].Cells['Size'].Value=('{0:N1} MB' -f ($p.Size/1MB));$grid.Rows[$i].Tag=$p}
    $bar=New-Object Windows.Forms.FlowLayoutPanel;$bar.Dock='Bottom';$bar.Height=45;$bar.FlowDirection='RightToLeft';$form.Controls.Add($grid);$form.Controls.Add($bar)
    foreach($x in @(@('取消','Cancel'),@('永久删除','Permanent'),@('移入回收站','Recycle'),@('全不选','None'),@('全选','All'))){$b=New-Object Windows.Forms.Button;$b.Text=$x[0];$b.Tag=$x[1];$b.Width=105;$bar.Controls.Add($b);$b.Add_Click({switch($this.Tag){'All'{$grid.Rows|%{$_.Cells[0].Value=$true}}'None'{$grid.Rows|%{$_.Cells[0].Value=$false}}'Cancel'{$form.Close()}'Recycle'{$mode='RecycleBin';$form.Close()}'Permanent'{if([Windows.Forms.MessageBox]::Show('永久删除选中项目？此操作不可恢复。','FolderNest',[Windows.Forms.MessageBoxButtons]::YesNo,[Windows.Forms.MessageBoxIcon]::Warning)-eq 'Yes'){$mode='Permanent';$form.Close()}}}})}
    $form.Add_FormClosed({ if($mode){$chosen=@($grid.Rows|Where-Object{$_.Cells[0].Value}|ForEach-Object{$_.Tag});foreach($p in $chosen){if(Remove-ItemSafe $p $mode){$p.Pending=$false}};$state.Units=@($state.Units);Save-JsonAtomic $state $StatePath} });$form.ShowDialog()|Out-Null
}
function Show-Settings {
    $cfg=Load-Config;$root=Read-Host "监控总目录（当前：$($cfg.Root)）";if($root){$cfg.Root=$root};$days=Read-Host "保留天数（当前：$($cfg.Days)）";if($days -match '^\d+$'){$cfg.Days=[int]$days};$mode=Read-Host "删除模式 RecycleBin/Permanent（当前：$($cfg.DeleteMode)）";if($mode -in @('RecycleBin','Permanent')){$cfg.DeleteMode=$mode};Save-JsonAtomic $cfg $ConfigPath;Log '配置已更新'
}
function Install-Tasks {
    $taskRun="powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$($MyInvocation.MyCommand.Path)`" -Run"
    schtasks.exe /Create /TN "$AppName Daily" /TR $taskRun /SC DAILY /ST 02:00 /F | Out-Null
    schtasks.exe /Create /TN "$AppName Logon" /TR $taskRun /SC ONLOGON /F | Out-Null
    Write-Host "已安装每日 02:00 扫描和登录补扫任务。"
}
function Uninstall-Tasks { schtasks.exe /Delete /TN "$AppName Daily" /F 2>$null; schtasks.exe /Delete /TN "$AppName Logon" /F 2>$null; Write-Host '已卸载任务。' }

if($Install){Show-Settings;Install-Tasks;exit};if($Uninstall){Uninstall-Tasks;exit};if($Settings){Show-Settings;exit};if($Review){Review-Window;exit};if($Run){Scan $true;exit};Review-Window
