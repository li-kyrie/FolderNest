using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FolderNest.Core;

namespace FolderNest.Desktop;

public sealed class RemoveMonitorsWindow : Window
{
    public RemoveMonitorsWindow(MainViewModel vm,IReadOnlyList<MonitorRow> selected)
    {
        Title="删除监控任务";Width=700;Height=430;MinWidth=480;MinHeight=320;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        var panel=new DockPanel {Margin=new Thickness(12)};Content=panel;
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        header.Children.Add(new TextBlock {Text="只删除勾选目录的监控配置及计时记录，不删除磁盘上的文件。以后重新添加，该目录从新加入时重新计时。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,0,4,8)});
        var choices=vm.Monitors.Select(row=>new RemoveChoice(row,selected.Contains(row))).ToArray();
        var list=new ListBox {ItemsSource=choices,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        list.ItemTemplate=ChoiceTemplate(typeof(RemoveChoice),nameof(RemoveChoice.Selected),nameof(RemoveChoice.Label));
        var tools=new WrapPanel();header.Children.Add(tools);
        tools.Children.Add(DialogUi.Button("全选",()=>{foreach(var c in choices)c.Selected=true;}));
        tools.Children.Add(DialogUi.Button("全不选",()=>{foreach(var c in choices)c.Selected=false;}));
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        var error=new TextBlock {TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.Firebrick};footer.Children.Add(error);
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(buttons);
        buttons.Children.Add(DialogUi.Button("删除勾选的监控",()=>
        {
            var rows=choices.Where(c=>c.Selected).Select(c=>c.Row).ToArray();
            if(rows.Length==0){error.Text="请勾选至少一个监控目录。";return;}
            if(MessageBox.Show(this,$"删除 {rows.Length} 个监控的配置和计时？\n真实文件保留，重新添加会重新计时。","确认删除监控",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            try{vm.Remove(rows);DialogResult=true;}catch(Exception ex){error.Text=ex.Message;}
        }));
        var cancel=DialogUi.Button("取消",()=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
        panel.Children.Add(list);
    }
    internal static DataTemplate ChoiceTemplate(Type type,string property,string label)
    {
        var template=new DataTemplate(type);var box=new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(CheckBox.IsCheckedProperty,new Binding(property){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding(label));
        text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);box.AppendChild(text);
        box.SetBinding(FrameworkElement.ToolTipProperty,new Binding(label));
        box.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,new Binding(label));
        box.SetValue(FrameworkElement.MarginProperty,new Thickness(4));template.VisualTree=box;return template;
    }
    private sealed class RemoveChoice(MonitorRow row,bool selected) : INotifyPropertyChanged
    {
        public MonitorRow Row=>row;
        public string Label=>$"{row.Definition.Path}  [{(row.Enabled?"启用":"停用")}]";
        public bool Selected {get=>selected;set{selected=value;PropertyChanged?.Invoke(this,new(nameof(Selected)));}}
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

public sealed record ResetCandidateGroup(MonitorDefinition Monitor,IReadOnlyList<FileUnit> Units,string? Error);

// A parent tick selects the whole live directory; child ticks select only those shown paths.
public sealed class ResetChoice : INotifyPropertyChanged
{
    private readonly ResetChoice? parent;
    private bool selected;
    private bool whole;
    private ResetChoice(string label,string? path,ResetChoice? parent=null){Label=label;Path=path;this.parent=parent;}
    public ResetChoice(ResetCandidateGroup group,bool selected=false)
        :this($"{group.Monitor.Path}  [{(group.Monitor.Enabled?"启用":"停用")}]"+(group.Error is null?"":"  [读取失败："+group.Error+"]"),null)
    {
        MonitorId=group.Monitor.Id;
        foreach(var unit in group.Units.OrderBy(u=>u.Path,StringComparer.OrdinalIgnoreCase))
            Children.Add(new((unit.IsDirectory?"子文件夹（整体计时）：":"文件：")+System.IO.Path.GetFileName(unit.Path),unit.Path,this));
        Checked=selected;
    }
    public Guid MonitorId {get;}
    public string Label {get;}
    public string? Path {get;}
    public ObservableCollection<ResetChoice> Children {get;}=[];
    public bool? Checked
    {
        get=>parent is not null?selected:whole?true:Children.Count==0?false:
            Children.All(c=>c.selected)?true:Children.Any(c=>c.selected)?null:false;
        set
        {
            if(parent is not null){selected=value==true;parent.whole=false;Notify();parent.Notify();return;}
            whole=value==true;
            foreach(var child in Children){child.selected=whole;child.Notify();}Notify();
        }
    }
    public TimingResetSelection? ToSelection()
    {
        if(parent is not null)throw new InvalidOperationException("只能从目录生成重置选择。");
        if(whole)return new(MonitorId);
        var paths=Children.Where(c=>c.selected).Select(c=>c.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return paths.Count==0?null:new(MonitorId,paths);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify()=>PropertyChanged?.Invoke(this,new(nameof(Checked)));
}

public sealed class ResetTimingWindow : Window
{
    private bool running;
    public ResetTimingWindow(MainViewModel vm,IReadOnlyList<MonitorRow> selected)
    {
        Title="重置计时";Width=780;Height=520;MinWidth=520;MinHeight=360;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        var roots=new ObservableCollection<ResetChoice>();
        var panel=new DockPanel {Margin=new Thickness(12)};Content=panel;
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        header.Children.Add(new TextBlock {Text="勾选目录可重置其全部当前项目；展开目录可只勾选个别文件。一级子文件夹作为整体计时。只改变软件内计时，不改文件日期、不删除文件；停用目录仍保持停用。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,0,4,8)});
        var tools=new WrapPanel();header.Children.Add(tools);
        tools.Children.Add(DialogUi.Button("全选目录",()=>{foreach(var c in roots)c.Checked=true;}));
        tools.Children.Add(DialogUi.Button("全不选",()=>{foreach(var c in roots)c.Checked=false;}));
        var tree=new TreeView {ItemsSource=roots};
        var template=new HierarchicalDataTemplate(typeof(ResetChoice)){ItemsSource=new Binding(nameof(ResetChoice.Children))};
        template.VisualTree=RemoveMonitorsWindow.ChoiceTemplate(typeof(ResetChoice),nameof(ResetChoice.Checked),nameof(ResetChoice.Label)).VisualTree;
        tree.ItemTemplate=template;
        VirtualizingStackPanel.SetIsVirtualizing(tree,true);VirtualizingStackPanel.SetVirtualizationMode(tree,VirtualizationMode.Recycling);
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        var resultText=new TextBox {IsReadOnly=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),MaxHeight=96,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text="正在读取目录内容…",Margin=new Thickness(4,8,4,4)};footer.Children.Add(resultText);
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(buttons);
        var execute=DialogUi.Button("重置勾选项计时",()=>{});execute.IsEnabled=false;buttons.Children.Add(execute);
        var close=DialogUi.Button("关闭",()=>Close());close.IsCancel=true;buttons.Children.Add(close);
        panel.Children.Add(tree);
        Loaded+=async (_,_)=>
        {
            running=true;header.IsEnabled=false;close.IsEnabled=false;
            try
            {
                var candidates=await vm.GetResetCandidatesAsync();
                foreach(var group in candidates)roots.Add(new(group,selected.Any(r=>r.Id==group.Monitor.Id)));
                var errors=candidates.Where(c=>c.Error is not null).ToArray();
                resultText.Text=errors.Length==0?"展开目录左侧箭头查看项目。确认后从执行时重新开始计时。":$"{errors.Length} 个目录无法读取，旧计时未改变。可以关闭后检查路径或权限，再重试。";
                execute.IsEnabled=roots.Count>0;
            }
            catch(Exception ex){resultText.Text=ex.Message;}
            finally{running=false;header.IsEnabled=true;close.IsEnabled=true;}
        };
        execute.Click+=async (_,_)=>
        {
            if(running)return;
            var selections=roots.Select(r=>r.ToSelection()).OfType<TimingResetSelection>().ToArray();
            if(selections.Length==0){resultText.Text="请勾选目录或文件项目。";return;}
            var entire=selections.Count(s=>s.Paths is null);var files=selections.Sum(s=>s.Paths?.Count??0);
            if(MessageBox.Show(this,$"确认重置 {entire} 个整目录及 {files} 个单独项目？\n只有勾选的范围重新计时，其他记录不改变。","确认重置计时",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            running=true;header.IsEnabled=false;tree.IsEnabled=false;buttons.IsEnabled=false;resultText.Text="正在重置计时…";
            try
            {
                var results=await vm.ResetAsync(selections);
                resultText.Text=$"已重置 {results.Sum(r=>r.ResetCount)} 项；失败 {results.Count(r=>r.Error is not null)} 个目录。"+
                    string.Join("\n",results.Where(r=>r.Error is not null).Select(r=>"\n"+roots.Single(c=>c.MonitorId==r.MonitorId).Label+"："+r.Error));
                var succeeded=results.Where(r=>r.Error is null).Select(r=>r.MonitorId).ToHashSet();
                foreach(var root in roots.Where(r=>succeeded.Contains(r.MonitorId)))root.Checked=false;
            }
            catch(Exception ex){resultText.Text=ex.Message;}
            finally{running=false;header.IsEnabled=true;tree.IsEnabled=true;buttons.IsEnabled=true;}
        };
        Closing+=(_,e)=>{if(running)e.Cancel=true;};
    }
}
