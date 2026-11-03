using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FolderNest.Core;
using FolderNest.Infrastructure;
using Microsoft.Win32;

namespace FolderNest.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private bool reviewing;
    public MainWindow(MainViewModel vm)
    {
        this.vm=vm;
        Resources["EnabledOptions"]=new[]{new KeyValuePair<string,bool>("启用",true),new("停用",false)};
        Resources["DeletionOptions"]=new[]{new KeyValuePair<string,DeletionMode>("回收站",DeletionMode.RecycleBin),new("永久删除",DeletionMode.Permanent)};
        Resources["ScheduleOptions"]=new[]{new KeyValuePair<string,bool>("参与",true),new("不参与",false)};
        InitializeComponent();DataContext=vm;RubberBandSelection.Attach(MonitorGrid);
        Closing+=OnClosing;
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        var app=(App)Application.Current;
        if(app.Exiting)return;
        e.Cancel=true;
        if(vm.IsBusy){vm.Status="正在处理文件，请稍后关闭。";return;}
        if(vm.HasTasks){Hide();vm.Status="已收起到托盘；自动检查仍在运行。";}
        else Dispatcher.InvokeAsync(app.ExitApplication);
    }
    private List<MonitorRow> Selection()=>MonitorGrid.SelectedItems.Cast<MonitorRow>().ToList();
    private void PreserveSelection(IReadOnlyList<MonitorRow> selected,object? current)
    {
        MonitorGrid.SelectedItems.Clear();foreach(var row in selected.Where(vm.Monitors.Contains))MonitorGrid.SelectedItems.Add(row);
        MonitorGrid.CurrentItem=current is MonitorRow old && selected.Contains(old) && vm.Monitors.Contains(old)?old:selected.FirstOrDefault(vm.Monitors.Contains);
        MonitorGrid.Focus();
    }
    private static bool HasValidationError(DependencyObject obj)
    {
        if(Validation.GetHasError(obj))return true;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(obj);i++)if(HasValidationError(VisualTreeHelper.GetChild(obj,i)))return true;
        return false;
    }
    private void CommitEdits()
    {
        if(!MonitorGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!MonitorGrid.CommitEdit(DataGridEditingUnit.Row,true)||
           HasValidationError(MonitorGrid)||vm.Monitors.Any(r=>r.HasErrors))
            throw new ArgumentException("存在未保存的无效修改，请修正红框内的值（路径编辑可用 Esc 撤销）。");
    }
    private void Run(Action action)
    {
        var rows=Selection();var current=MonitorGrid.CurrentItem;
        try{CommitEdits();action();}
        catch(Exception ex){vm.Status=ex.Message;MessageBox.Show(this,ex.Message,"FolderNest");}
        finally{PreserveSelection(rows,current);}
    }
    private void Add_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        var dialog=new MonitorDialog(vm){Owner=this};
        if(dialog.ShowDialog()==true && dialog.Result is {} result)
        {
            var row=vm.Add(result);Dispatcher.InvokeAsync(()=>PreserveSelection([row],row));
        }
    });
    private void Toggle_Click(object sender,RoutedEventArgs e)=>Run(()=>vm.Toggle(Selection()));
    private void Remove_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        var rows=Selection();if(rows.Count==0)throw new ArgumentException("请先选择监控目录。");
        var index=MonitorGrid.SelectedIndex;
        if(MessageBox.Show(this,$"移除 {rows.Count} 个监控和计时记录？实际文件不会被删除。","删除监控",MessageBoxButton.YesNo)==MessageBoxResult.Yes)
        {
            vm.Remove(rows);
            if(vm.Monitors.Count>0){var next=vm.Monitors[Math.Clamp(index,0,vm.Monitors.Count-1)];Dispatcher.InvokeAsync(()=>PreserveSelection([next],next));}
        }
    });
    private async Task ScanAsync(bool selected)
    {
        var rows=Selection();var current=MonitorGrid.CurrentItem;
        try
        {
            CommitEdits();var result=await vm.ScanAsync(selected?rows:null);
            if(result.Errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,result.Errors),"扫描遇到问题");
        }
        catch(Exception ex){vm.Status=ex.Message;MessageBox.Show(this,ex.Message,"FolderNest");}
        finally{PreserveSelection(rows,current);}
    }
    private async void ScanSelected_Click(object sender,RoutedEventArgs e)=>await ScanAsync(true);
    private async void ScanAll_Click(object sender,RoutedEventArgs e)=>await ScanAsync(false);
    private async void Review_Click(object sender,RoutedEventArgs e)=>await OpenReviewAsync();
    public async Task OpenReviewAsync(bool scheduledOnly=false)
    {
        if(reviewing)return;
        reviewing=true;var rows=Selection();var current=MonitorGrid.CurrentItem;
        try
        {
            CommitEdits();var result=await vm.ScanAsync(scope:scheduledOnly?ScanScope.Scheduled:ScanScope.AllEnabled);
            if(result.Errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,result.Errors),"部分目录不可访问，未列入清单");
            if(result.DueItems.Count==0){vm.Status="当前没有到期项目。";return;}
            new ReviewWindow(vm,result.DueItems){Owner=this,Icon=Icon}.ShowDialog();
        }
        catch(Exception ex){vm.Status=ex.Message;MessageBox.Show(this,ex.Message,"FolderNest");}
        finally{reviewing=false;PreserveSelection(rows,current);}
    }
    private void Deploy_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(vm.Monitors.All(r=>!r.Enabled))throw new ArgumentException("没有目录待安装：请先添加并启用监控目录。");
        new ScheduleDialog(vm){Owner=this,Icon=Icon}.ShowDialog();
    });
    private void Uninstall_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(MessageBox.Show(this,"卸载全部自动任务？监控设置和计时记录会保留。","卸载自动任务",MessageBoxButton.YesNo)==MessageBoxResult.Yes)vm.Uninstall();
    });
    private void Settings_Click(object sender,RoutedEventArgs e)=>Run(()=>new SettingsWindow(vm){Owner=this,Icon=Icon}.ShowDialog());
    private void Import_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        var picker=new OpenFileDialog{Title="导入 1.x 的 config.json（不导入旧到期清单）",Filter="JSON 配置|*.json"};
        if(picker.ShowDialog(this)!=true)return;
        var monitors=LegacyImporter.Read(picker.FileName);
        var errors=new List<string>();var count=0;
        foreach(var m in monitors)try{vm.Add(m);count++;}catch(Exception ex){errors.Add(m.Path+"："+ex.Message);}
        MessageBox.Show(this,$"已导入 {count} 个目录。首次扫描重新开始计时；自动任务需重新部署。"+(errors.Count>0?"\n"+string.Join("\n",errors):""),"导入结果");
    });
    private void Log_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(File.Exists(vm.Paths.LogPath))Process.Start(new ProcessStartInfo("notepad.exe"){UseShellExecute=true,ArgumentList={vm.Paths.LogPath}});
        else vm.Status="暂时没有日志。";
    });
    private void Tasks_Click(object sender,RoutedEventArgs e)=>Run(vm.RefreshTaskStatus);
    private void Exit_Click(object sender,RoutedEventArgs e)=>((App)Application.Current).ExitApplication();
    private void OnValidationError(object sender,ValidationErrorEventArgs e)
    {
        if(e.Action==ValidationErrorEventAction.Added)vm.Status=$"修改未保存：{e.Error.ErrorContent}";
    }
}
