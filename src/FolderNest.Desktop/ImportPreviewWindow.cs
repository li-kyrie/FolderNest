using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FolderNest.Core;

namespace FolderNest.Desktop;

public sealed class ImportPreviewWindow : Window
{
    public ImportPreviewWindow(IReadOnlyList<MonitorDefinition> monitors)
    {
        Title="导入监控配置 · 确认";Width=800;Height=400;MinWidth=600;MinHeight=300;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        var panel=new DockPanel {Margin=new Thickness(12)};Content=panel;
        var note=new TextBlock {Text=$"将新增 {monitors.Count} 个目录，从导入时重新计时。不会覆盖已有监控，不导入旧计时。源 JSON 不会修改，也不会持续同步。请核对删除模式。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,0,3,10)};
        DockPanel.SetDock(note,Dock.Top);panel.Children.Add(note);
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        var accept=DialogUi.Button("确认导入",()=>DialogResult=true);footer.Children.Add(accept);
        var cancel=DialogUi.Button("取消",()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        var grid=new DataGrid {IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,
            ItemsSource=monitors.Select(m=>new {m.Path,Days=m.RetentionDays,Mode=m.DeletionMode==DeletionMode.Permanent?"永久删除":"回收站",State=m.Enabled?"启用":"停用"})};
        foreach(var (header,property,width) in new[]{("目录","Path",new DataGridLength(1,DataGridLengthUnitType.Star)),("天数","Days",new DataGridLength(70)),("删除模式","Mode",new DataGridLength(100)),("状态","State",new DataGridLength(70))})
            grid.Columns.Add(new DataGridTextColumn {Header=header,Binding=new Binding(property),Width=width});
        panel.Children.Add(grid);Loaded+=(_,_)=>cancel.Focus();
    }
}
