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
$ScriptPath = $MyInvocation.MyCommand.Path
$AppDir = Split-Path -Parent $ScriptPath
$DataDir = Join-Path $AppDir 'data'
$ConfigPath = Join-Path $DataDir 'config.json'
$StatePath = Join-Path $DataDir 'state.json'
$LogPath = Join-Path $DataDir 'foldernest.log'
$MaxLogBytes = 10MB

function Ensure-DataDir {
    try {
        if (-not (Test-Path -LiteralPath $DataDir)) { New-Item -ItemType Directory -Path $DataDir -Force | Out-Null }
        $probe = Join-Path $DataDir '.write-test'; Set-Content -LiteralPath $probe -Value 'ok' -Encoding ASCII; Remove-Item -LiteralPath $probe -Force
    } catch {
        $fallback = Join-Path (Join-Path $env:LOCALAPPDATA $AppName) ''
        if (-not (Test-Path -LiteralPath $fallback)) { New-Item -ItemType Directory -Path $fallback -Force | Out-Null }
        $script:DataDir = $fallback; $script:ConfigPath = Join-Path $fallback 'config.json'; $script:StatePath = Join-Path $fallback 'state.json'; $script:LogPath = Join-Path $fallback 'foldernest.log'
    }
}
function Save-JsonAtomic($Value, [string]$Path) { $tmp="$Path.$PID.tmp"; $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8; Move-Item -LiteralPath $tmp -Destination $Path -Force }
function Log([string]$Message) {
    Ensure-DataDir; Add-Content -LiteralPath $LogPath -Value ("{0:yyyy-MM-dd HH:mm:ss}`t{1}" -f (Get-Date),$Message) -Encoding UTF8
    if((Get-Item -LiteralPath $LogPath).Length -gt $MaxLogBytes){$lines=@(Get-Content -LiteralPath $LogPath -Encoding UTF8);$keep=[Math]::Max(1,[int]($lines.Count*.6));$lines[($lines.Count-$keep)..($lines.Count-1)]|Set-Content -LiteralPath $LogPath -Encoding UTF8}
}
function Load-Config {
    Ensure-DataDir
    if(Test-Path -LiteralPath $ConfigPath){$c=Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8|ConvertFrom-Json;if($c.PSObject.Properties.Name -contains 'Monitors'){return $c};if($c.PSObject.Properties.Name -contains 'Root' -and $c.Root){return [pscustomobject]@{Monitors=@([pscustomobject]@{Id=[guid]::NewGuid().ToString();Path=$c.Root;Days=[int]$c.Days;DeleteMode=$c.DeleteMode;Enabled=$true})}}}
    return [pscustomobject]@{Monitors=@()}
}
function Load-State { Ensure-DataDir; if(Test-Path -LiteralPath $StatePath){return Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8|ConvertFrom-Json};return [pscustomobject]@{LastScanDate='';Units=@()}}
function New-Unit([string]$Path,[bool]$Folder,[datetime]$Latest,[int64]$Size,[int]$Count){[pscustomobject]@{Path=$Path;IsFolder=$Folder;Latest=$Latest.ToUniversalTime().ToString('o');Size=$Size;Count=$Count}}
function Get-Units([string]$Root) {
    $out=New-Object System.Collections.ArrayList
    foreach($f in @(Get-ChildItem -LiteralPath $Root -File -ErrorAction Stop)){[void]$out.Add((New-Unit $f.FullName $false $f.LastWriteTimeUtc $f.Length 1))}
    foreach($d in @(Get-ChildItem -LiteralPath $Root -Directory -ErrorAction Stop)){$files=@(Get-ChildItem -LiteralPath $d.FullName -File -Recurse -ErrorAction SilentlyContinue);$latest=$d.LastWriteTimeUtc;if($files.Count -gt 0){$latest=($files|Measure-Object LastWriteTimeUtc -Maximum).Maximum};$size=[int64](($files|Measure-Object Length -Sum).Sum);if($null -eq $size){$size=0};[void]$out.Add((New-Unit $d.FullName $true $latest $size $files.Count))}
    return @($out)
}
function Find-Unit($State,[string]$MonitorId,[string]$Path){@($State.Units|Where-Object{$_.MonitorId -eq $MonitorId -and $_.Path -ieq $Path}|Select-Object -First 1)[0]}
function Scan-All([bool]$Notify) {
    $cfg=Load-Config;$state=Load-State;$new=New-Object System.Collections.ArrayList;$cutoff=[datetime]::UtcNow.AddDays(-1);$total=0
    foreach($m in @($cfg.Monitors|Where-Object Enabled)){
        if(-not(Test-Path -LiteralPath $m.Path -PathType Container)){Log "跳过监控目录（不存在）：$($m.Path)";continue};$cutoff=[datetime]::UtcNow.AddDays(-[int]$m.Days)
        foreach($u in Get-Units $m.Path){$old=Find-Unit $state $m.Id $u.Path;if($null -eq $old){$old=[pscustomobject]@{MonitorId=$m.Id;Path=$u.Path;IsFolder=$u.IsFolder;FirstSeen=[datetime]::UtcNow.ToString('o');Pending=$false}};$first=[datetime]::Parse($old.FirstSeen).ToUniversalTime();$latest=[datetime]::Parse($u.Latest).ToUniversalTime();$effective=if($latest -gt $first){$latest}else{$first};$old.IsFolder=$u.IsFolder;$old.Latest=$u.Latest;$old.Size=$u.Size;$old.Count=$u.Count;$old.Effective=$effective.ToString('o');if($effective -lt $cutoff){$old.Pending=$true};[void]$new.Add($old)}
    }
    $state.Units=@($new);$state.LastScanDate=(Get-Date).ToString('yyyy-MM-dd');Save-JsonAtomic $state $StatePath;$pending=@($state.Units|Where-Object Pending);$total=$pending.Count;Log "扫描完成：待处理 $total 项";if($Notify -and $total -gt 0){Show-Notification $total};return $total
}
function Show-Notification([int]$Count){$n=New-Object Windows.Forms.NotifyIcon;$n.Icon=[Drawing.SystemIcons]::Information;$n.Visible=$true;$n.BalloonTipTitle='FolderNest 到期提醒';$n.BalloonTipText="有 $Count 个文件或文件夹达到保留期限，请打开 FolderNest 进行筛选。";$n.ShowBalloonTip(10000);Start-Sleep -Seconds 3;$n.Dispose()}
function Remove-Record($Record,[string]$Mode){if(-not(Test-Path -LiteralPath $Record.Path)){Log "跳过（不存在）：$($Record.Path)";return $false};try{if($Mode -eq 'RecycleBin'){if($Record.IsFolder){[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($Record.Path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)}else{[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($Record.Path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)}}else{Remove-Item -LiteralPath $Record.Path -Recurse:([bool]$Record.IsFolder) -Force};Log "已处理 [$Mode]：$($Record.Path)";return $true}catch{Log "处理失败：$($Record.Path)；$($_.Exception.Message)";return $false}}
function Show-ReviewWindow {
    $state=Load-State;$pending=@($state.Units|Where-Object Pending);if($pending.Count -eq 0){[Windows.Forms.MessageBox]::Show('当前没有待处理项目。','FolderNest')|Out-Null;return}
    $form=New-Object Windows.Forms.Form;$form.Text='FolderNest 到期清单';$form.Width=980;$form.Height=600;$form.StartPosition='CenterScreen'
    $grid=New-Object Windows.Forms.DataGridView;$grid.Dock='Fill';$grid.AllowUserToAddRows=$false;$grid.AutoGenerateColumns=$false
    $check=New-Object Windows.Forms.DataGridViewCheckBoxColumn;$check.HeaderText='处理';$check.Width=55;$grid.Columns.Add($check)|Out-Null
    foreach($x in @(@('类型','Type',75),@('监控目录','Monitor',220),@('路径','Path',500),@('有效时间','Effective',145),@('大小','Size',95))){$col=New-Object Windows.Forms.DataGridViewTextBoxColumn;$col.HeaderText=$x[0];$col.Name=$x[1];$col.Width=$x[2];$grid.Columns.Add($col)|Out-Null}
    $cfg=Load-Config;foreach($p in $pending){$i=$grid.Rows.Add();$grid.Rows[$i].Cells[0].Value=$true;$m=@($cfg.Monitors|Where-Object Id -eq $p.MonitorId|Select-Object -First 1);$grid.Rows[$i].Cells['Type'].Value=if($p.IsFolder){'文件夹'}else{'文件'};$grid.Rows[$i].Cells['Monitor'].Value=if($m){$m.Path}else{'未知'};$grid.Rows[$i].Cells['Path'].Value=$p.Path;$grid.Rows[$i].Cells['Effective'].Value=([datetime]::Parse($p.Effective).ToLocalTime().ToString('yyyy-MM-dd HH:mm'));$grid.Rows[$i].Cells['Size'].Value=('{0:N1} MB' -f ($p.Size/1MB));$grid.Rows[$i].Tag=$p}
    $bar=New-Object Windows.Forms.FlowLayoutPanel;$bar.Dock='Bottom';$bar.Height=45;$bar.FlowDirection='RightToLeft';$form.Controls.Add($grid);$form.Controls.Add($bar);$mode=$null
    foreach($x in @(@('取消','Cancel'),@('永久删除','Permanent'),@('移入回收站','Recycle'),@('全不选','None'),@('全选','All'))){
        $b=New-Object Windows.Forms.Button; $b.Text=$x[0]; $b.Tag=$x[1]; $b.Width=110; $bar.Controls.Add($b)|Out-Null
        $b.Add_Click({
            param($sender,$event)
            switch($sender.Tag){
                'All' { $grid.Rows | ForEach-Object { $_.Cells[0].Value=$true } }
                'None' { $grid.Rows | ForEach-Object { $_.Cells[0].Value=$false } }
                'Cancel' { $form.Close() }
                'Recycle' { $script:ReviewMode='RecycleBin'; $form.Close() }
                'Permanent' {
                    if([Windows.Forms.MessageBox]::Show('永久删除选中项目？此操作不可恢复。','FolderNest',[Windows.Forms.MessageBoxButtons]::YesNo,[Windows.Forms.MessageBoxIcon]::Warning) -eq [Windows.Forms.DialogResult]::Yes){$script:ReviewMode='Permanent';$form.Close()}
                }
            }
        })
    }
    $script:ReviewMode=$null;$form.Add_FormClosed({if($script:ReviewMode){$chosen=@($grid.Rows|Where-Object{$_.Cells[0].Value}|ForEach-Object{$_.Tag});foreach($p in $chosen){if(Remove-Record $p $script:ReviewMode){$p.Pending=$false}};Save-JsonAtomic $state $StatePath;$script:ReviewMode=$null}});$form.ShowDialog()|Out-Null
}
function Show-MonitorDialog($Existing) {
    $f=New-Object Windows.Forms.Form;$f.Text=if($Existing){'编辑监控目录'}else{'添加监控目录'};$f.Width=560;$f.Height=240;$f.StartPosition='CenterParent';$table=New-Object Windows.Forms.TableLayoutPanel;$table.Dock='Fill';$table.ColumnCount=3;$table.RowCount=5;$table.Padding='10';$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Absolute,100)))|Out-Null;$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Percent,100)))|Out-Null;$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Absolute,90)))|Out-Null;$f.Controls.Add($table)
    $table.Controls.Add((New-Object Windows.Forms.Label -Property @{Text='目录';AutoSize=$true}),0,0);$path=New-Object Windows.Forms.TextBox;$path.Dock='Fill';if($Existing){$path.Text=$Existing.Path};$table.Controls.Add($path,1,0);$browse=New-Object Windows.Forms.Button;$browse.Text='浏览...';$table.Controls.Add($browse,2,0);$browse.Add_Click({$d=New-Object Windows.Forms.FolderBrowserDialog;if($d.ShowDialog() -eq [Windows.Forms.DialogResult]::OK){$path.Text=$d.SelectedPath}})
    $table.Controls.Add((New-Object Windows.Forms.Label -Property @{Text='保留天数';AutoSize=$true}),0,1);$days=New-Object Windows.Forms.NumericUpDown;$days.Minimum=1;$days.Maximum=36500;$days.Value=if($Existing){[decimal]$Existing.Days}else{30};$table.Controls.Add($days,1,1)
    $table.Controls.Add((New-Object Windows.Forms.Label -Property @{Text='删除模式';AutoSize=$true}),0,2);$mode=New-Object Windows.Forms.ComboBox;$mode.DropDownStyle='DropDownList';[void]$mode.Items.Add('RecycleBin');[void]$mode.Items.Add('Permanent');$mode.SelectedItem=if($Existing){$Existing.DeleteMode}else{'RecycleBin'};$table.Controls.Add($mode,1,2)
    $enabled=New-Object Windows.Forms.CheckBox;$enabled.Text='启用此监控目录';$enabled.Checked=if($Existing){[bool]$Existing.Enabled}else{$true};$table.Controls.Add($enabled,1,3)
    $ok=New-Object Windows.Forms.Button;$ok.Text='保存';$cancel=New-Object Windows.Forms.Button;$cancel.Text='取消';$table.Controls.Add($ok,1,4);$table.Controls.Add($cancel,2,4);$result=$null;$ok.Add_Click({if([string]::IsNullOrWhiteSpace($path.Text)-or-not(Test-Path -LiteralPath $path.Text -PathType Container)){[Windows.Forms.MessageBox]::Show('请选择存在的文件夹。','FolderNest')|Out-Null;return};$id=if($Existing){$Existing.Id}else{[guid]::NewGuid().ToString()};$result=[pscustomobject]@{Id=$id;Path=$path.Text.Trim();Days=[int]$days.Value;DeleteMode=[string]$mode.SelectedItem;Enabled=[bool]$enabled.Checked};$f.DialogResult=[Windows.Forms.DialogResult]::OK;$f.Close()});$cancel.Add_Click({$f.DialogResult=[Windows.Forms.DialogResult]::Cancel;$f.Close()});$f.ShowDialog()|Out-Null;return $result
}
function Show-MainWindow {
    $form=New-Object Windows.Forms.Form;$form.Text='FolderNest';$form.Width=920;$form.Height=560;$form.StartPosition='CenterScreen'
    $grid=New-Object Windows.Forms.DataGridView;$grid.Dock='Fill';$grid.AllowUserToAddRows=$false;$grid.AutoGenerateColumns=$false;$grid.SelectionMode='FullRowSelect';$grid.MultiSelect=$false
    foreach($x in @(@('状态','Enabled',65),@('监控目录','Path',520),@('天数','Days',65),@('删除模式','DeleteMode',110))){$col=New-Object Windows.Forms.DataGridViewTextBoxColumn;$col.HeaderText=$x[0];$col.Name=$x[1];$col.Width=$x[2];$grid.Columns.Add($col)|Out-Null}
    $top=New-Object Windows.Forms.FlowLayoutPanel;$top.Dock='Top';$top.Height=42;$top.Padding='5';$bottom=New-Object Windows.Forms.StatusStrip;$status=New-Object Windows.Forms.ToolStripStatusLabel;$status.Text='就绪';[void]$bottom.Items.Add($status);$form.Controls.Add($grid);$form.Controls.Add($top);$form.Controls.Add($bottom)
    $cfg=Load-Config
    function Refresh-Grid {$grid.Rows.Clear();foreach($m in @($cfg.Monitors)){$i=$grid.Rows.Add();$grid.Rows[$i].Cells['Enabled'].Value=if($m.Enabled){'启用'}else{'停用'};$grid.Rows[$i].Cells['Path'].Value=$m.Path;$grid.Rows[$i].Cells['Days'].Value=$m.Days;$grid.Rows[$i].Cells['DeleteMode'].Value=$m.DeleteMode;$grid.Rows[$i].Tag=$m};$status.Text="已配置 $(@($cfg.Monitors).Count) 个监控目录"}
    function Add-UiButton([string]$Text,[string]$Tag){$b=New-Object Windows.Forms.Button;$b.Text=$Text;$b.Tag=$Tag;$b.Width=105;$top.Controls.Add($b)|Out-Null;return $b}
    $add=Add-UiButton '添加目录' Add;$edit=Add-UiButton '编辑' Edit;$toggle=Add-UiButton '启用/停用' Toggle;$remove=Add-UiButton '删除监控' Remove;$scan=Add-UiButton '立即扫描' Scan;$review=Add-UiButton '到期清单' Review;$log=Add-UiButton '查看日志' Log;$install=Add-UiButton '安装定时任务' Install;$uninstall=Add-UiButton '卸载定时任务' Uninstall;$exit=Add-UiButton '退出' Exit
    $add.Add_Click({$m=Show-MonitorDialog $null;if($m){$cfg.Monitors=@($cfg.Monitors)+$m;Save-JsonAtomic $cfg $ConfigPath;Refresh-Grid}});$edit.Add_Click({if($grid.SelectedRows.Count){$m=Show-MonitorDialog $grid.SelectedRows[0].Tag;if($m){$old=$grid.SelectedRows[0].Tag;$idx=[array]::IndexOf(@($cfg.Monitors),$old);$cfg.Monitors[$idx]=$m;Save-JsonAtomic $cfg $ConfigPath;Refresh-Grid}}});$toggle.Add_Click({if($grid.SelectedRows.Count){$m=$grid.SelectedRows[0].Tag;$m.Enabled=-not [bool]$m.Enabled;Save-JsonAtomic $cfg $ConfigPath;Refresh-Grid}});$remove.Add_Click({if($grid.SelectedRows.Count){$m=$grid.SelectedRows[0].Tag;if([Windows.Forms.MessageBox]::Show("仅删除监控设置，不删除目录内容：`n$($m.Path)",'FolderNest',[Windows.Forms.MessageBoxButtons]::YesNo)-eq [Windows.Forms.DialogResult]::Yes){$cfg.Monitors=@($cfg.Monitors|Where-Object Id -ne $m.Id);$s=Load-State;$s.Units=@($s.Units|Where-Object MonitorId -ne $m.Id);Save-JsonAtomic $cfg $ConfigPath;Save-JsonAtomic $s $StatePath;Refresh-Grid}}});$scan.Add_Click({$n=Scan-All $false;$status.Text="扫描完成，待处理 $n 项"});$review.Add_Click({Show-ReviewWindow;Refresh-Grid});$log.Add_Click({if(Test-Path -LiteralPath $LogPath){Start-Process notepad.exe -ArgumentList "`"$LogPath`""}});$install.Add_Click({Install-Tasks;$status.Text='已安装任务'});$uninstall.Add_Click({Uninstall-Tasks;$status.Text='已卸载任务'});$exit.Add_Click({$form.Close()});Refresh-Grid;$form.ShowDialog()|Out-Null
}
function Install-Tasks {$tr="powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$ScriptPath`" -Run";schtasks.exe /Create /TN "$AppName Daily" /TR $tr /SC DAILY /ST 02:00 /F|Out-Null;schtasks.exe /Create /TN "$AppName Logon" /TR $tr /SC ONLOGON /F|Out-Null;Log '已安装任务计划'}
function Uninstall-Tasks {schtasks.exe /Delete /TN "$AppName Daily" /F 2>$null;schtasks.exe /Delete /TN "$AppName Logon" /F 2>$null;Log '已卸载任务计划'}

if($Install){Install-Tasks;exit};if($Uninstall){Uninstall-Tasks;exit};if($Run){[void](Scan-All $true);exit};if($Review){Show-ReviewWindow;exit};if($Settings){Show-MainWindow;exit};Show-MainWindow
