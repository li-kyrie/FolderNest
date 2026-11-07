using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace FolderNest.Desktop;

public sealed class MonitorEditConfirmationWindow : Window
{
    public static string Describe(IReadOnlyList<MonitorRow> rows)
    {
        var text=new StringBuilder();
        foreach(var row in rows)
        {
            var old=row.Definition;var next=row.Candidate;text.AppendLine(old.Path);
            if(old.Path!=next.Path)text.AppendLine($"  路径：{old.Path} → {next.Path}（将清除旧路径计时，新路径重新计时）");
            if(old.RetentionDays!=next.RetentionDays)text.AppendLine($"  保留天数：{old.RetentionDays} → {next.RetentionDays}（保留计时起点，缩短期限可能立即到期）");
            if(old.Enabled!=next.Enabled)text.AppendLine($"  状态：{(old.Enabled?"启用":"停用")} → {(next.Enabled?"启用":"停用")}（保留计时）");
            string Mode(FolderNest.Core.DeletionMode mode)=>mode==FolderNest.Core.DeletionMode.Permanent?"永久删除":"回收站";
            if(old.DeletionMode!=next.DeletionMode)text.AppendLine($"  删除方式：{Mode(old.DeletionMode)} → {Mode(next.DeletionMode)}");
            text.AppendLine();
        }
        return text.ToString();
    }
    public MonitorEditConfirmationWindow(IReadOnlyList<MonitorRow> rows)
    {
        Title="确认监控配置修改";Width=760;Height=440;MinWidth=560;MinHeight=300;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        var panel=new DockPanel {Margin=new Thickness(12)};Content=panel;
        var note=new TextBlock {Text=$"请核对 {rows.Count} 个目录的变更。点击“确认保存”才写入数据库；关闭此窗口只返回编辑，不会保存。配置变更不会自动删除文件。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,0,3,10)};
        DockPanel.SetDock(note,Dock.Top);panel.Children.Add(note);
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        footer.Children.Add(DialogUi.Button("确认保存",()=>DialogResult=true));
        var cancel=DialogUi.Button("返回编辑",()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        var summary=new TextBox {Text=Describe(rows),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        System.Windows.Automation.AutomationProperties.SetName(summary,"待确认的监控配置变更");
        panel.Children.Add(summary);Loaded+=(_,_)=>cancel.Focus();
    }
}
