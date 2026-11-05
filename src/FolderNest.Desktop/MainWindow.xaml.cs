using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FolderNest.Core;
using FolderNest.Infrastructure;
using Microsoft.Win32;

namespace FolderNest.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private bool reviewing;
    private Point? dragStart;
    private MonitorRow? dragRow;
    private MonitorRow[] dragSelection=[];
    private const string RowDragFormat="FolderNest.MonitorRow";
    public MainWindow(MainViewModel vm)
    {
        this.vm=vm;
        Resources["EnabledOptions"]=new[]{new KeyValuePair<string,bool>("启用",true),new("停用",false)};
        Resources["DeletionOptions"]=new[]{new KeyValuePair<string,DeletionMode>("回收站",DeletionMode.RecycleBin),new("永久删除",DeletionMode.Permanent)};
        InitializeComponent();DataContext=vm;Closing+=OnClosing;
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        var app=(App)Application.Current;if(app.Exiting)return;e.Cancel=true;
        if(vm.IsBusy){vm.Status="正在处理文件，请稍后关闭。";return;}
        if(vm.HasMonitors){Hide();vm.Status="已收起到托盘；启用目录继续监控。";}
        else Dispatcher.InvokeAsync(app.ExitApplication);
    }
    private List<MonitorRow> Selection()=>MonitorGrid.SelectedItems.Cast<MonitorRow>().ToList();
    private void PreserveSelection(IReadOnlyList<MonitorRow> selected,object? current)
    {
        MonitorGrid.SelectedItems.Clear();foreach(var row in selected.Where(vm.Monitors.Contains))MonitorGrid.SelectedItems.Add(row);
        MonitorGrid.CurrentItem=current is MonitorRow old&&selected.Contains(old)&&vm.Monitors.Contains(old)?old:selected.FirstOrDefault(vm.Monitors.Contains);
        MonitorGrid.Focus();
    }
    private static bool HasValidationError(DependencyObject obj)
    {
        if(Validation.GetHasError(obj))return true;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(obj);i++)if(HasValidationError(VisualTreeHelper.GetChild(obj,i)))return true;
        return false;
    }
    private void CommitEdits()
    {
        if(!MonitorGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!MonitorGrid.CommitEdit(DataGridEditingUnit.Row,true)||
           HasValidationError(MonitorGrid)||vm.Monitors.Any(r=>r.HasErrors))
            throw new ArgumentException("存在未保存的无效修改，请修正红框内的值（路径编辑可用 Esc 撤销）。");
    }
    private void Error(Exception ex){vm.Status=ex.Message;MessageBox.Show(this,ex.Message,"FolderNest");}
    private void Run(Action action)
    {
        var rows=Selection();var current=MonitorGrid.CurrentItem;
        try{CommitEdits();action();}catch(Exception ex){Error(ex);}finally{PreserveSelection(rows,current);}
    }
    private async void Add_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            CommitEdits();var dialog=new MonitorDialog(vm){Owner=this,Icon=Icon};
            if(dialog.ShowDialog()==true&&dialog.Result is {} result){var row=await vm.AddAsync(result);PreserveSelection([row],row);}
        }
        catch(Exception ex){Error(ex);}
    }
    private void Toggle_Click(object sender,RoutedEventArgs e)=>Run(()=>vm.Toggle(Selection()));
    private void Remove_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(vm.Monitors.Count==0)throw new ArgumentException("没有监控目录可删除。");
        var index=MonitorGrid.SelectedIndex;
        if(new RemoveMonitorsWindow(vm,Selection()){Owner=this,Icon=Icon}.ShowDialog()==true&&vm.Monitors.Count>0)
        {
            var remaining=Selection().Where(vm.Monitors.Contains).ToArray();
            var next=vm.Monitors[Math.Clamp(index,0,vm.Monitors.Count-1)];
            Dispatcher.InvokeAsync(()=>PreserveSelection(remaining.Length>0?remaining:[next],remaining.FirstOrDefault()??next));
        }
    });
    private void Reset_Click(object sender,RoutedEventArgs e)
    {
        var rows=Selection();var current=MonitorGrid.CurrentItem;
        try
        {
            CommitEdits();if(vm.Monitors.Count==0)throw new ArgumentException("没有监控目录可重置。");
            var dialog=new ResetTimingWindow(vm,rows){Owner=this,Icon=Icon};dialog.ShowDialog();
        }
        catch(Exception ex){Error(ex);}finally{PreserveSelection(rows,current);}
    }
    private async Task ScanAsync(bool selected)
    {
        var rows=Selection();var current=MonitorGrid.CurrentItem;
        try
        {
            CommitEdits();var result=await vm.ScanAsync(selected?rows:null);
            if(result.Errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,result.Errors),"扫描遇到问题");
        }
        catch(Exception ex){Error(ex);}finally{PreserveSelection(rows,current);}
    }
    private async void ScanSelected_Click(object sender,RoutedEventArgs e)=>await ScanAsync(true);
    private async void ScanAll_Click(object sender,RoutedEventArgs e)=>await ScanAsync(false);
    private async void Review_Click(object sender,RoutedEventArgs e)=>await OpenReviewAsync();
    public async Task OpenReviewAsync()
    {
        if(reviewing)return;reviewing=true;var rows=Selection();var current=MonitorGrid.CurrentItem;
        try
        {
            CommitEdits();var result=await vm.ScanAsync();
            if(result.Errors.Count>0)MessageBox.Show(this,string.Join(Environment.NewLine,result.Errors),"部分目录不可访问，未列入清单");
            if(result.DueItems.Count==0){vm.Status="当前没有到期项目。";return;}
            new ReviewWindow(vm,result.DueItems){Owner=this,Icon=Icon}.ShowDialog();
        }
        catch(Exception ex){Error(ex);}finally{reviewing=false;PreserveSelection(rows,current);}
    }
    private void MoveUp_Click(object sender,RoutedEventArgs e)=>Run(()=>vm.MoveSelection(Selection(),-1));
    private void MoveDown_Click(object sender,RoutedEventArgs e)=>Run(()=>vm.MoveSelection(Selection(),1));
    private void SelectAll_Click(object sender,RoutedEventArgs e){try{CommitEdits();MonitorGrid.SelectAll();}catch(Exception ex){Error(ex);}}
    private void SelectNone_Click(object sender,RoutedEventArgs e){MonitorGrid.UnselectAll();MonitorGrid.CurrentItem=null;}
    private void Settings_Click(object sender,RoutedEventArgs e)=>Run(()=>new SettingsWindow(vm){Owner=this,Icon=Icon}.ShowDialog());
    private async void Import_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            CommitEdits();
            var picker=new OpenFileDialog{Title="导入监控配置（JSON）",Filter="JSON 配置|*.json"};
            var backup=Path.Combine(AppContext.BaseDirectory,"data","legacy-backup");
            if(Directory.Exists(backup))picker.InitialDirectory=backup;
            if(picker.ShowDialog(this)!=true)return;
            var monitors=MonitorConfigImporter.Read(picker.FileName);
            foreach(var m in monitors)vm.Validate(m);
            if(new ImportPreviewWindow(monitors){Owner=this,Icon=Icon}.ShowDialog()!=true)return;
            var errors=new List<string>();int count=0;
            foreach(var m in monitors)try{await vm.AddAsync(m);count++;}catch(Exception ex){errors.Add(m.Path+"："+ex.Message);}
            MessageBox.Show(this,$"已导入 {count} 个目录，从导入时重新计时；后台扫描自动设置。源 JSON 未修改。"+(errors.Count>0?"\n"+string.Join("\n",errors):""),"导入结果");
        }
        catch(Exception ex){Error(ex);}
    }
    private void Log_Click(object sender,RoutedEventArgs e)=>Run(()=>
    {
        if(File.Exists(vm.Paths.LogPath))Process.Start(new ProcessStartInfo("notepad.exe"){UseShellExecute=true,ArgumentList={vm.Paths.LogPath}});
        else vm.Status="暂时没有日志。";
    });
    private void Exit_Click(object sender,RoutedEventArgs e)=>((App)Application.Current).ExitApplication();
    private void OnValidationError(object sender,ValidationErrorEventArgs e){if(e.Action==ValidationErrorEventAction.Added)vm.Status=$"修改未保存：{e.Error.ErrorContent}";}
    private void Handle_Down(object sender,MouseButtonEventArgs e)
    {
        if(sender is not FrameworkElement {DataContext:MonitorRow row} || !vm.IsIdle)return;
        dragStart=e.GetPosition(MonitorGrid);dragRow=row;
        dragSelection=MonitorGrid.SelectedItems.Contains(row)?Selection().ToArray():[row];
        e.Handled=true;
    }
    private void Handle_Up(object sender,MouseButtonEventArgs e)
    {
        if(dragRow is {} row){PreserveSelection(dragSelection,row);e.Handled=true;}
        dragStart=null;dragRow=null;
    }
    private void Handle_Move(object sender,MouseEventArgs e)
    {
        if(dragStart is not {} start||dragRow is null||e.LeftButton!=MouseButtonState.Pressed)return;
        var point=e.GetPosition(MonitorGrid);
        if(Math.Abs(point.Y-start.Y)<SystemParameters.MinimumVerticalDragDistance&&Math.Abs(point.X-start.X)<SystemParameters.MinimumHorizontalDragDistance)return;
        var row=dragRow;dragStart=null;
        try{CommitEdits();DragDrop.DoDragDrop((DependencyObject)sender,new DataObject(RowDragFormat,row),DragDropEffects.Move);}
        catch(Exception ex){Error(ex);}finally{PreserveSelection(dragSelection,row);dragRow=null;}
    }
    private void Grid_DragOver(object sender,DragEventArgs e)
    {
        e.Effects=e.Data.GetDataPresent(RowDragFormat)&&vm.IsIdle?DragDropEffects.Move:DragDropEffects.None;e.Handled=true;
    }
    private void Grid_Drop(object sender,DragEventArgs e)
    {
        e.Handled=true;
        if(e.Data.GetData(RowDragFormat) is not MonitorRow source||!vm.Monitors.Contains(source))return;
        var target=FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        var order=vm.Monitors.ToList();order.Remove(source);
        var index=order.Count;
        if(target?.Item is MonitorRow to&&to!=source)
        {
            index=order.IndexOf(to);
            if(e.GetPosition(target).Y>target.ActualHeight/2)index++;
        }
        else if(target?.Item==source)return;
        order.Insert(Math.Clamp(index,0,order.Count),source);
        try{vm.Reorder(order);}catch(Exception ex){Error(ex);}
    }
    private static T? FindAncestor<T>(DependencyObject? node) where T:DependencyObject
    {
        while(node is not null)
        {
            if(node is T found)return found;
            node=node is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
