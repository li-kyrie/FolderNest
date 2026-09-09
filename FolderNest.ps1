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
$IconPath = Join-Path $AppDir 'FolderNest.ico'

function Set-FormIcon($Form) {
    if(Test-Path -LiteralPath $IconPath){try{$Form.Icon=New-Object System.Drawing.Icon($IconPath)}catch{}}
}

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
function Ensure-MonitorProperties($Monitor) {
    if(-not ($Monitor.PSObject.Properties.Name -contains 'Scheduled')) { Add-Member -InputObject $Monitor -NotePropertyName Scheduled -NotePropertyValue $true }
    if(-not ($Monitor.PSObject.Properties.Name -contains 'Enabled')) { Add-Member -InputObject $Monitor -NotePropertyName Enabled -NotePropertyValue $true }
    if(-not ($Monitor.PSObject.Properties.Name -contains 'Days')){Add-Member -InputObject $Monitor -NotePropertyName Days -NotePropertyValue 30}else{$daysValue=0;if(-not [int]::TryParse([string]$Monitor.Days,[ref]$daysValue) -or $daysValue -lt 1 -or $daysValue -gt 36500){$Monitor.Days=30}else{$Monitor.Days=$daysValue}}
    if(-not ($Monitor.PSObject.Properties.Name -contains 'DeleteMode')){Add-Member -InputObject $Monitor -NotePropertyName DeleteMode -NotePropertyValue 'RecycleBin'}elseif(@('RecycleBin','Permanent') -notcontains [string]$Monitor.DeleteMode){$Monitor.DeleteMode='RecycleBin'}
    return $Monitor
}
function Load-Config {
    Ensure-DataDir
    if(Test-Path -LiteralPath $ConfigPath){
        $c=Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8|ConvertFrom-Json
        if($c.PSObject.Properties.Name -contains 'Monitors'){
            foreach($m in @($c.Monitors)){Ensure-MonitorProperties $m|Out-Null}
            return $c
        }
        if($c.PSObject.Properties.Name -contains 'Root' -and $c.Root){return [pscustomobject]@{Monitors=@([pscustomobject]@{Id=[guid]::NewGuid().ToString();Path=$c.Root;Days=[int]$c.Days;DeleteMode=$c.DeleteMode;Enabled=$true;Scheduled=$true})}}
    }
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
function Scan-All([bool]$Notify,[bool]$ScheduledOnly=$false,[string[]]$MonitorIds=$null) {
    $cfg=Load-Config;$state=Load-State;$new=New-Object System.Collections.ArrayList;$cutoff=[datetime]::UtcNow.AddDays(-1);$total=0
    $monitors=@($cfg.Monitors|Where-Object{$_.Enabled -and (-not $ScheduledOnly -or $_.Scheduled -ne $false) -and ($null -eq $MonitorIds -or $MonitorIds -contains $_.Id)})
    foreach($m in $monitors){
        if(-not(Test-Path -LiteralPath $m.Path -PathType Container)){Log "跳过监控目录（不存在）：$($m.Path)";continue};$cutoff=[datetime]::UtcNow.AddDays(-[int]$m.Days)
        foreach($u in Get-Units $m.Path){$old=Find-Unit $state $m.Id $u.Path;if($null -eq $old){$old=[pscustomobject]@{MonitorId=$m.Id;Path=$u.Path;IsFolder=$u.IsFolder;FirstSeen=[datetime]::UtcNow.ToString('o');Latest='';Size=0;Count=0;Effective='';Pending=$false}}else{foreach($p in @(@('Latest',''),@('Size',0),@('Count',0),@('Effective',''))){if(-not ($old.PSObject.Properties.Name -contains $p[0])){Add-Member -InputObject $old -NotePropertyName $p[0] -NotePropertyValue $p[1]}}};$first=[datetime]::Parse($old.FirstSeen).ToUniversalTime();$latest=[datetime]::Parse($u.Latest).ToUniversalTime();$effective=if($latest -gt $first){$latest}else{$first};$old.IsFolder=$u.IsFolder;$old.Latest=$u.Latest;$old.Size=$u.Size;$old.Count=$u.Count;$old.Effective=$effective.ToString('o');if($effective -lt $cutoff){$old.Pending=$true};[void]$new.Add($old)}
    }
    if($ScheduledOnly -or $null -ne $MonitorIds){$processedIds=@($monitors|ForEach-Object Id);$configuredIds=@($cfg.Monitors|ForEach-Object Id);foreach($old in @($state.Units|Where-Object{$_.MonitorId -in $configuredIds -and $_.MonitorId -notin $processedIds})){[void]$new.Add($old)}}
    $state.Units=@($new);$state.LastScanDate=(Get-Date).ToString('yyyy-MM-dd');Save-JsonAtomic $state $StatePath;$pending=@($state.Units|Where-Object Pending);$total=$pending.Count;Log "扫描完成：待处理 $total 项";if($Notify -and $total -gt 0){Show-Notification $total};return $total
}
function Show-Notification([int]$Count){$n=New-Object Windows.Forms.NotifyIcon;$n.Icon=[Drawing.SystemIcons]::Information;$n.Visible=$true;$n.BalloonTipTitle='FolderNest 到期提醒';$n.BalloonTipText="有 $Count 个文件或文件夹达到保留期限，请打开 FolderNest 进行筛选。";$n.ShowBalloonTip(10000);Start-Sleep -Seconds 3;$n.Dispose()}
function Test-TasksInstalled {
    $daily=$false;$logon=$false
    & schtasks.exe /Query /TN "$AppName Daily" 2>$null | Out-Null;if($LASTEXITCODE -eq 0){$daily=$true}
    & schtasks.exe /Query /TN "$AppName Logon" 2>$null | Out-Null;if($LASTEXITCODE -eq 0){$logon=$true}
    return ($daily -and $logon)
}
function Remove-Record($Record,[string]$Mode){if(-not(Test-Path -LiteralPath $Record.Path)){Log "跳过（不存在）：$($Record.Path)";return $false};try{if($Mode -eq 'RecycleBin'){if($Record.IsFolder){[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($Record.Path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)}else{[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($Record.Path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)}}else{Remove-Item -LiteralPath $Record.Path -Recurse:([bool]$Record.IsFolder) -Force};Log "已处理 [$Mode]：$($Record.Path)";return $true}catch{Log "处理失败：$($Record.Path)；$($_.Exception.Message)";return $false}}
function Show-ReviewWindow {
    $state=Load-State;$pending=@($state.Units|Where-Object Pending);if($pending.Count -eq 0){[Windows.Forms.MessageBox]::Show('当前没有待处理项目。','FolderNest')|Out-Null;return}
    $form=New-Object Windows.Forms.Form;$form.Text='FolderNest 到期清单';$form.Width=980;$form.Height=600;$form.StartPosition='CenterScreen';Set-FormIcon $form
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
    $f=New-Object Windows.Forms.Form;$f.Text=if($Existing){'编辑监控目录'}else{'添加监控目录'};$f.Width=560;$f.Height=240;$f.StartPosition='CenterParent';Set-FormIcon $f;$table=New-Object Windows.Forms.TableLayoutPanel;$table.Dock='Fill';$table.ColumnCount=3;$table.RowCount=5;$table.Padding=[Windows.Forms.Padding]::new(10);$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Absolute,100)))|Out-Null;$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Percent,100)))|Out-Null;$table.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Absolute,90)))|Out-Null;$f.Controls.Add($table)
    $table.Controls.Add((New-Object Windows.Forms.Label -Property @{Text='目录';AutoSize=$true}),0,0);$path=New-Object Windows.Forms.TextBox;$path.Dock='Fill';if($Existing){$path.Text=$Existing.Path};$table.Controls.Add($path,1,0);$browse=New-Object Windows.Forms.Button;$browse.Text='浏览...';$table.Controls.Add($browse,2,0);$browse.Add_Click({$d=New-Object Windows.Forms.FolderBrowserDialog;if($d.ShowDialog() -eq [Windows.Forms.DialogResult]::OK){$path.Text=$d.SelectedPath}})
    $table.Controls.Add((New-Object Windows.Forms.Label -Property @{Text='保留天数';AutoSize=$true}),0,1);$days=New-Object Windows.Forms.NumericUpDown;$days.Minimum=1;$days.Maximum=36500;$days.Value=if($Existing){[decimal]$Existing.Days}else{30};$table.Controls.Add($days,1,1)
    $table.Controls.Add((New-Object Windows.Forms.Label -Property @{Text='删除模式';AutoSize=$true}),0,2);$mode=New-Object Windows.Forms.ComboBox;$mode.DropDownStyle='DropDownList';[void]$mode.Items.Add('RecycleBin');[void]$mode.Items.Add('Permanent');$mode.SelectedItem=if($Existing){$Existing.DeleteMode}else{'RecycleBin'};$table.Controls.Add($mode,1,2)
    $enabled=New-Object Windows.Forms.CheckBox;$enabled.Text='启用此监控目录';$enabled.AutoSize=$true;$enabled.Checked=if($Existing){[bool]$Existing.Enabled}else{$true};$table.Controls.Add($enabled,1,3)
    $ok=New-Object Windows.Forms.Button;$ok.Text='保存';$cancel=New-Object Windows.Forms.Button;$cancel.Text='取消';$table.Controls.Add($ok,1,4);$table.Controls.Add($cancel,2,4);$resultHolder=[pscustomobject]@{Value=$null};$ok.Add_Click({if([string]::IsNullOrWhiteSpace($path.Text)-or-not(Test-Path -LiteralPath $path.Text -PathType Container)){[Windows.Forms.MessageBox]::Show('请选择存在的文件夹。','FolderNest')|Out-Null;return};$id=if($Existing){$Existing.Id}else{[guid]::NewGuid().ToString()};$resultHolder.Value=[pscustomobject]@{Id=$id;Path=$path.Text.Trim();Days=[int]$days.Value;DeleteMode=[string]$mode.SelectedItem;Enabled=[bool]$enabled.Checked;Scheduled=if($Existing){$Existing.Scheduled -ne $false}else{$true}};$f.DialogResult=[Windows.Forms.DialogResult]::OK;$f.Close()});$cancel.Add_Click({$f.DialogResult=[Windows.Forms.DialogResult]::Cancel;$f.Close()});$f.ShowDialog()|Out-Null;return $resultHolder.Value
}
function Show-MainWindow {
    $form=New-Object Windows.Forms.Form;$form.Text='FolderNest';$form.Width=1040;$form.Height=600;$form.StartPosition='CenterScreen';$form.ShowInTaskbar=$true;Set-FormIcon $form
    $grid=New-Object Windows.Forms.DataGridView;$grid.Dock='Fill';$grid.AllowUserToAddRows=$false;$grid.AutoGenerateColumns=$false;$grid.SelectionMode='FullRowSelect';$grid.MultiSelect=$true;$grid.EditMode='EditOnKeystrokeOrF2';$grid.RowHeadersVisible=$false
    $enabledCol=New-Object Windows.Forms.DataGridViewComboBoxColumn;$enabledCol.HeaderText='状态';$enabledCol.Name='Enabled';$enabledCol.Width=75;[void]$enabledCol.Items.Add('启用');[void]$enabledCol.Items.Add('停用');$grid.Columns.Add($enabledCol)|Out-Null
    $pathCol=New-Object Windows.Forms.DataGridViewTextBoxColumn;$pathCol.HeaderText='监控目录';$pathCol.Name='Path';$pathCol.Width=570;$grid.Columns.Add($pathCol)|Out-Null
    $daysCol=New-Object Windows.Forms.DataGridViewTextBoxColumn;$daysCol.HeaderText='天数';$daysCol.Name='Days';$daysCol.Width=70;$grid.Columns.Add($daysCol)|Out-Null
    $modeCol=New-Object Windows.Forms.DataGridViewComboBoxColumn;$modeCol.HeaderText='删除模式';$modeCol.Name='DeleteMode';$modeCol.Width=115;[void]$modeCol.Items.Add('RecycleBin');[void]$modeCol.Items.Add('Permanent');$grid.Columns.Add($modeCol)|Out-Null
    $scheduledCol=New-Object Windows.Forms.DataGridViewTextBoxColumn;$scheduledCol.HeaderText='定时任务';$scheduledCol.Name='Scheduled';$scheduledCol.Width=85;$scheduledCol.ReadOnly=$true;$grid.Columns.Add($scheduledCol)|Out-Null
    $top=New-Object Windows.Forms.FlowLayoutPanel;$top.Dock='Top';$top.AutoSize=$true;$top.AutoSizeMode='GrowAndShrink';$top.Padding=[Windows.Forms.Padding]::new(5);$top.FlowDirection='LeftToRight';$top.WrapContents=$true;$top.AutoScroll=$false
    $bottom=New-Object Windows.Forms.StatusStrip;$status=New-Object Windows.Forms.ToolStripStatusLabel;$status.Text='就绪';[void]$bottom.Items.Add($status);$form.Controls.Add($grid);$form.Controls.Add($top);$form.Controls.Add($bottom)
    $cfg=Load-Config
    function Update-Status([string]$Message=''){$taskText=if((Test-TasksInstalled)){'已部署'}else{'未部署'};$base="已配置 $(@($cfg.Monitors).Count) 个监控目录；定时任务：$taskText";if($Message){$status.Text="$Message；$base"}else{$status.Text=$base}}
    function Get-SelectedRows {@($grid.SelectedRows|Sort-Object RowIndex)}
    function Get-SelectedIds {@(Get-SelectedRows|ForEach-Object{$_.Tag.Id})}
    function Focus-Grid([string[]]$Ids=$null){$grid.Focus();if($Ids){foreach($row in $grid.Rows){if($Ids -contains $row.Tag.Id){$row.Selected=$true}}}}
    function Refresh-Grid([string[]]$KeepIds=$null){$grid.Rows.Clear();foreach($m in @($cfg.Monitors)){$i=$grid.Rows.Add();$grid.Rows[$i].Cells['Enabled'].Value=if($m.Enabled){'启用'}else{'停用'};$grid.Rows[$i].Cells['Path'].Value=$m.Path;$grid.Rows[$i].Cells['Days'].Value=$m.Days;$grid.Rows[$i].Cells['DeleteMode'].Value=$m.DeleteMode;$grid.Rows[$i].Cells['Scheduled'].Value=if($m.Scheduled -ne $false){'已选'}else{'未选'};$grid.Rows[$i].Tag=$m};if($KeepIds){Focus-Grid $KeepIds};Update-Status}
    function Add-UiButton([string]$Text,[string]$Tag){$b=New-Object Windows.Forms.Button;$b.Text=$Text;$b.Tag=$Tag;$b.Width=105;$b.Height=28;$top.Controls.Add($b)|Out-Null;return $b}
    $add=Add-UiButton '添加目录' Add;$edit=Add-UiButton '编辑' Edit;$toggle=Add-UiButton '启用/停用' Toggle;$remove=Add-UiButton '删除监控' Remove;$scanSelected=Add-UiButton '扫描选中' ScanSelected;$scan=Add-UiButton '立即扫描' Scan;$review=Add-UiButton '到期清单' Review;$log=Add-UiButton '查看日志' Log;$install=Add-UiButton '安装定时任务' Install;$uninstall=Add-UiButton '卸载定时任务' Uninstall;$exit=Add-UiButton '退出' Exit
    $grid.Add_CellValidating({param($sender,$e);if($e.RowIndex -lt 0){return};$name=$grid.Columns[$e.ColumnIndex].Name;$value=[string]$e.FormattedValue;if($name -eq 'Path' -and -not(Test-Path -LiteralPath $value.Trim() -PathType Container)){[Windows.Forms.MessageBox]::Show('路径无效，请选择存在的文件夹。','FolderNest')|Out-Null;$e.Cancel=$true};if($name -eq 'Days'){$n=0;if(-not [int]::TryParse($value,[ref]$n) -or $n -lt 1 -or $n -gt 36500){[Windows.Forms.MessageBox]::Show('天数必须是 1 到 36500 之间的整数。','FolderNest')|Out-Null;$e.Cancel=$true}}})
    $grid.Add_CellEndEdit({param($sender,$e);if($e.RowIndex -lt 0){return};$m=$grid.Rows[$e.RowIndex].Tag;$name=$grid.Columns[$e.ColumnIndex].Name;switch($name){'Enabled'{$m.Enabled=([string]$grid.Rows[$e.RowIndex].Cells[$e.ColumnIndex].Value -eq '启用')};'Path'{$m.Path=([string]$grid.Rows[$e.RowIndex].Cells[$e.ColumnIndex].Value).Trim()};'Days'{$m.Days=[int]$grid.Rows[$e.RowIndex].Cells[$e.ColumnIndex].Value};'DeleteMode'{$m.DeleteMode=[string]$grid.Rows[$e.RowIndex].Cells[$e.ColumnIndex].Value}};try{Save-JsonAtomic $cfg $ConfigPath;Update-Status '配置已保存'}catch{[Windows.Forms.MessageBox]::Show("保存配置失败：$($_.Exception.Message)",'FolderNest')|Out-Null};Focus-Grid @($m.Id)})
    $grid.Add_KeyDown({param($sender,$e);if($grid.CurrentCell -and $grid.Columns[$grid.CurrentCell.ColumnIndex].Name -eq 'Days' -and ($e.KeyCode -eq [Windows.Forms.Keys]::Up -or $e.KeyCode -eq [Windows.Forms.Keys]::Down)){$n=0;if(-not [int]::TryParse([string]$grid.CurrentCell.Value,[ref]$n)){$n=1};if($e.KeyCode -eq [Windows.Forms.Keys]::Up){$n=[Math]::Min(36500,$n+1)}else{$n=[Math]::Max(1,$n-1)};$grid.CurrentCell.Value=$n;$e.Handled=$true;$e.SuppressKeyPress=$true}})
    $grid.Add_EditingControlShowing({param($sender,$e);if($grid.CurrentCell -and $grid.Columns[$grid.CurrentCell.ColumnIndex].Name -eq 'Days' -and -not $e.Control.Tag){$e.Control.Tag='FolderNestDaysEditor';$e.Control.Add_KeyDown({param($control,$keyEvent);if($keyEvent.KeyCode -eq [Windows.Forms.Keys]::Up -or $keyEvent.KeyCode -eq [Windows.Forms.Keys]::Down){$n=0;if(-not [int]::TryParse([string]$control.Text,[ref]$n)){$n=1};if($keyEvent.KeyCode -eq [Windows.Forms.Keys]::Up){$n=[Math]::Min(36500,$n+1)}else{$n=[Math]::Max(1,$n-1)};$control.Text=[string]$n;$control.SelectionStart=$control.Text.Length;$keyEvent.Handled=$true;$keyEvent.SuppressKeyPress=$true}})}})
    $dragStart=-1;$dragging=$false;$grid.Add_MouseDown({param($sender,$e);if($e.Button -eq [Windows.Forms.MouseButtons]::Left){$hit=$grid.HitTest($e.X,$e.Y);if($hit.RowIndex -ge 0){$dragStart=$hit.RowIndex;$dragging=$true;if(-not ([Windows.Forms.Control]::ModifierKeys -band [Windows.Forms.Keys]::Control) -and -not ([Windows.Forms.Control]::ModifierKeys -band [Windows.Forms.Keys]::Shift)){$grid.ClearSelection();$grid.Rows[$dragStart].Selected=$true}}}});$grid.Add_MouseMove({param($sender,$e);if($dragging -and ($e.Button -band [Windows.Forms.MouseButtons]::Left)){$hit=$grid.HitTest($e.X,$e.Y);if($hit.RowIndex -ge 0){$a=[Math]::Min($dragStart,$hit.RowIndex);$b=[Math]::Max($dragStart,$hit.RowIndex);$grid.ClearSelection();for($i=$a;$i -le $b;$i++){$grid.Rows[$i].Selected=$true}}}});$grid.Add_MouseUp({$dragging=$false;$dragStart=-1})
    $add.Add_Click({$m=Show-MonitorDialog $null;if($m){$cfg.Monitors=@($cfg.Monitors)+$m;Save-JsonAtomic $cfg $ConfigPath;Refresh-Grid @($m.Id);Focus-Grid @($m.Id)}})
    $edit.Add_Click({$rows=Get-SelectedRows;if($rows.Count -eq 1){$m=Show-MonitorDialog $rows[0].Tag;if($m){$old=$rows[0].Tag;$idx=[array]::IndexOf(@($cfg.Monitors),$old);$cfg.Monitors[$idx]=$m;Save-JsonAtomic $cfg $ConfigPath;Refresh-Grid @($m.Id);Focus-Grid @($m.Id)}}else{[Windows.Forms.MessageBox]::Show('编辑功能一次只能选择一个目录。','FolderNest')|Out-Null;Focus-Grid}})
    $toggle.Add_Click({$ids=Get-SelectedIds;if($ids.Count){foreach($row in Get-SelectedRows){$row.Tag.Enabled=-not [bool]$row.Tag.Enabled};Save-JsonAtomic $cfg $ConfigPath;Refresh-Grid $ids;Focus-Grid $ids}})
    $remove.Add_Click({$rows=Get-SelectedRows;if($rows.Count){$paths=($rows|ForEach-Object{$_.Tag.Path})-join "`n";if([Windows.Forms.MessageBox]::Show("仅删除监控设置，不删除目录内容：`n$paths",'FolderNest',[Windows.Forms.MessageBoxButtons]::YesNo)-eq [Windows.Forms.DialogResult]::Yes){$ids=@($rows|ForEach-Object{$_.Tag.Id});$cfg.Monitors=@($cfg.Monitors|Where-Object{$ids -notcontains $_.Id});$s=Load-State;$s.Units=@($s.Units|Where-Object{$ids -notcontains $_.MonitorId});Save-JsonAtomic $cfg $ConfigPath;Save-JsonAtomic $s $StatePath;Refresh-Grid;Focus-Grid}}})
    $scanSelected.Add_Click({$ids=Get-SelectedIds;if($ids.Count){$n=Scan-All $false $false $ids;Update-Status "选中目录扫描完成，待处理 $n 项";Focus-Grid $ids}else{[Windows.Forms.MessageBox]::Show('请先选择要扫描的目录。','FolderNest')|Out-Null;Focus-Grid}})
    $scan.Add_Click({$n=Scan-All $false;Update-Status "扫描完成，待处理 $n 项";Focus-Grid (Get-SelectedIds)})
    $review.Add_Click({Show-ReviewWindow;Refresh-Grid (Get-SelectedIds);Focus-Grid (Get-SelectedIds)})
    $log.Add_Click({if(Test-Path -LiteralPath $LogPath){Start-Process notepad.exe -ArgumentList "`"$LogPath`""};Focus-Grid})
    $install.Add_Click({$ok=Install-Tasks;if($ok){Refresh-Grid (Get-SelectedIds);Update-Status '定时任务已安装'}else{Update-Status '未安装定时任务'};Focus-Grid (Get-SelectedIds)})
    $uninstall.Add_Click({Uninstall-Tasks;Update-Status '定时任务已卸载';Focus-Grid (Get-SelectedIds)})
    $exit.Add_Click({$form.Close()});Refresh-Grid;$form.ShowDialog()|Out-Null
}
function Show-TaskInstallDialog($Monitors) {
    $f=New-Object Windows.Forms.Form;$f.Text='选择定时任务监控目录';$f.Width=620;$f.Height=360;$f.StartPosition='CenterParent';Set-FormIcon $f
    $layout=New-Object Windows.Forms.TableLayoutPanel;$layout.Dock='Fill';$layout.Padding=[Windows.Forms.Padding]::new(10);$layout.RowCount=3;$layout.ColumnCount=1;$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle([Windows.Forms.SizeType]::Absolute,32)))|Out-Null;$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle([Windows.Forms.SizeType]::Percent,100)))|Out-Null;$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle([Windows.Forms.SizeType]::Absolute,42)))|Out-Null;$f.Controls.Add($layout)
    $label=New-Object Windows.Forms.Label;$label.Text='勾选需要由每日 02:00 / 登录任务自动扫描的目录：';$label.AutoSize=$true;$layout.Controls.Add($label,0,0)
    $list=New-Object Windows.Forms.CheckedListBox;$list.Dock='Fill';$list.CheckOnClick=$true;foreach($m in $Monitors){$idx=$list.Items.Add("$($m.Path)（$($m.Days) 天，$($m.DeleteMode)）");$list.SetItemChecked($idx,($m.Scheduled -ne $false))};$layout.Controls.Add($list,0,1)
    $buttons=New-Object Windows.Forms.FlowLayoutPanel;$buttons.Dock='Fill';$buttons.FlowDirection='RightToLeft';$ok=New-Object Windows.Forms.Button;$ok.Text='安装';$cancel=New-Object Windows.Forms.Button;$cancel.Text='取消';$buttons.Controls.Add($ok);$buttons.Controls.Add($cancel);$layout.Controls.Add($buttons,0,2);$selected=[pscustomobject]@{Ids=@();Accepted=$false}
    $ok.Add_Click({$selected.Ids=@(for($i=0;$i -lt $list.Items.Count;$i++){if($list.GetItemChecked($i)){$Monitors[$i].Id}});$selected.Accepted=$true;$f.Close()});$cancel.Add_Click({$f.Close()});$f.ShowDialog()|Out-Null;return $selected
}
function Install-Tasks {
    $cfg=Load-Config;$monitors=@($cfg.Monitors|Where-Object Enabled)
    if($monitors.Count -eq 0){Log '未安装定时任务：没有启用的监控目录';[Windows.Forms.MessageBox]::Show('没有可安装的监控目录。请先添加并启用目录。','FolderNest')|Out-Null;return $false}
    $choice=Show-TaskInstallDialog $monitors;if(-not $choice.Accepted){Log '取消安装定时任务';return $false}
    if($choice.Ids.Count -eq 0){Log '未安装定时任务：没有选择监控目录';[Windows.Forms.MessageBox]::Show('未选择任何监控目录，未安装定时任务。','FolderNest')|Out-Null;return $false}
    foreach($m in $monitors){$m.Scheduled=($choice.Ids -contains $m.Id)};Save-JsonAtomic $cfg $ConfigPath
    $tr="powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$ScriptPath`" -Run"
    $okDaily=$false;$okLogon=$false;& schtasks.exe /Create /TN "$AppName Daily" /TR $tr /SC DAILY /ST 02:00 /F|Out-Null;if($LASTEXITCODE -eq 0){$okDaily=$true};& schtasks.exe /Create /TN "$AppName Logon" /TR $tr /SC ONLOGON /F|Out-Null;if($LASTEXITCODE -eq 0){$okLogon=$true}
    $names=($monitors|Where-Object Scheduled|ForEach-Object Path)-join '; ';Log "安装定时任务：目录 [$names]；每日02:00=$okDaily；登录补扫=$okLogon"
    if($okDaily -and $okLogon){return $true};[Windows.Forms.MessageBox]::Show('定时任务安装失败，请查看日志。','FolderNest')|Out-Null;return $false
}
function Uninstall-Tasks {
    foreach($taskName in @("$AppName Daily", "$AppName Logon")){
        # schtasks returns an error when a task does not exist. Check first so
        # uninstall remains repeatable and does not trip ErrorActionPreference=Stop.
        & cmd.exe /d /c "schtasks.exe /Query /TN `"$taskName`" >nul 2>&1"
        if($LASTEXITCODE -eq 0){
            & cmd.exe /d /c "schtasks.exe /Delete /TN `"$taskName`" /F >nul 2>&1"
            if($LASTEXITCODE -ne 0){Log "卸载任务失败：$taskName"}
        }
    }
    Log '已卸载任务计划'
}

if($Install){[void](Install-Tasks);exit};if($Uninstall){Uninstall-Tasks;exit};if($Run){[void](Scan-All $true $true);exit};if($Review){Show-ReviewWindow;exit};if($Settings){Show-MainWindow;exit};Show-MainWindow
