using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderNest.Core;
using Microsoft.Win32;

namespace FolderNest.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    public MainWindow(MainViewModel vm)
    {
        this.vm=vm;
        Resources["EnabledOptions"]=new[]{new KeyValuePair<string,bool>("启用",true),new("停用",false)};
        Resources["DeletionOptions"]=new[]{new KeyValuePair<string,DeletionMode>("回收站",DeletionMode.RecycleBin),new("永久删除",DeletionMode.Permanent)};
        InitializeComponent(); DataContext=vm;
        RubberBandSelection.Attach(MonitorGrid);
        Loaded+=async (_,_)=>await TaskStatusAsync();
    }
    private List<MonitorRow> Selection()=>MonitorGrid.SelectedItems.Cast<MonitorRow>().ToList();
    private void PreserveSelection(IReadOnlyList<MonitorRow> selected)
    {
        // Keep row instances; avoid rebuilding ItemsSource and moving the current row to index zero.
        var previousCurrent=MonitorGrid.CurrentItem as MonitorRow;
        MonitorGrid.SelectedItems.Clear(); foreach(var row in selected.Where(vm.Monitors.Contains)) MonitorGrid.SelectedItems.Add(row);
        var current=previousCurrent is not null && selected.Contains(previousCurrent) && vm.Monitors.Contains(previousCurrent)
            ? previousCurrent : selected.FirstOrDefault(vm.Monitors.Contains);
        if(current is not null) MonitorGrid.CurrentItem=current;
        MonitorGrid.Focus();
    }
    private void Run(Action action)
    {
        try { action(); } catch(Exception ex) { vm.Status=ex.Message; MessageBox.Show(this,ex.Message,"FolderNest"); }
    }
    private void Add_Click(object sender,RoutedEventArgs e) => Run(()=>
    {
        var picker=new OpenFolderDialog { Title="选择监控目录", Multiselect=false };
        if(picker.ShowDialog(this)==true) PreserveSelection([vm.Add(picker.FolderName)]);
    });
    private void Toggle_Click(object sender,RoutedEventArgs e) => Run(()=>{var rows=Selection(); vm.Toggle(rows); PreserveSelection(rows);});
    private void Remove_Click(object sender,RoutedEventArgs e) => Run(()=>
    {
        var rows=Selection(); if(rows.Count==0) throw new ArgumentException("请先选择监控目录。");
        if(MessageBox.Show(this,$"删除 {rows.Count} 个监控配置和计时记录？实际文件不会被删除。","删除监控",MessageBoxButton.YesNo)==MessageBoxResult.Yes) vm.Remove(rows);
        MonitorGrid.Focus();
    });
    private async Task ScanAsync(bool selected)
    {
        var rows=Selection();
        try
        {
            var result=await vm.ScanAsync(selected ? rows : null);
            if(result.Errors.Count>0) MessageBox.Show(this,string.Join(Environment.NewLine,result.Errors),"扫描遇到问题");
        }
        catch(Exception ex) { vm.Status=ex.Message; MessageBox.Show(this,ex.Message,"FolderNest"); }
        PreserveSelection(rows);
    }
    private async void ScanSelected_Click(object sender,RoutedEventArgs e) => await ScanAsync(true);
    private async void ScanAll_Click(object sender,RoutedEventArgs e) => await ScanAsync(false);
    private void Review_Click(object sender,RoutedEventArgs e) => Run(()=>
    {
        var due=vm.GetDue(); if(due.Count==0) { vm.Status="当前没有到期项目。"; return; }
        var review=new Window { Title="FolderNest · 到期清单预览",Owner=this,Width=900,Height=450,Icon=Icon,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var panel=new DockPanel(); var note=new TextBlock { Text="清理执行模块将在后续阶段接入。本窗口仅展示实时计算的到期项目。",Margin=new Thickness(10) };
        DockPanel.SetDock(note,Dock.Top); panel.Children.Add(note);
        panel.Children.Add(new DataGrid { IsReadOnly=true,AutoGenerateColumns=true,ItemsSource=due.Select(i=>new { 监控目录=i.Monitor.Path,项目=i.Observation.Path,到期时间=i.DueAtUtc.ToLocalTime().ToString("g") }) });
        review.Content=panel; review.ShowDialog();
    });
    private void Log_Click(object sender,RoutedEventArgs e) => Run(()=>
    {
        if(System.IO.File.Exists(vm.Paths.LogPath)) Process.Start(new ProcessStartInfo("notepad.exe",vm.Paths.LogPath) { UseShellExecute=true });
    });
    private async Task TaskStatusAsync()
    {
        // Status lookup is small but should not freeze layout or editing.
        try { await Task.Run(vm.RefreshTaskStatus); }
        catch(Exception ex) { vm.Status=ex.Message; }
    }
    private async void Tasks_Click(object sender,RoutedEventArgs e) => await TaskStatusAsync();
    private void OnValidationError(object sender,ValidationErrorEventArgs e)
    {
        if(e.Action==ValidationErrorEventAction.Added) vm.Status=$"修改未保存：{e.Error.ErrorContent}";
    }
}
