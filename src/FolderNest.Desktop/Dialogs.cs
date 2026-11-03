using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FolderNest.Core;
using FolderNest.Desktop.Controls;
using Microsoft.Win32;

namespace FolderNest.Desktop;

internal static class DialogUi
{
    public static Button Button(string title,Action action)
    {var b=new Button {Content=title,MinHeight=28};b.Click+=(_,_)=>action();return b;}
    public static Grid Field(string label,UIElement control)
    {
        var g=new Grid {Margin=new Thickness(0,3,0,3)};
        g.ColumnDefinitions.Add(new(){Width=new GridLength(96)});g.ColumnDefinitions.Add(new());
        var l=new Label {Content=label,VerticalAlignment=VerticalAlignment.Center,Target=control};
        g.Children.Add(l);Grid.SetColumn(control,1);g.Children.Add(control);return g;
    }
    public static ComboBox Modes(DeletionMode selected)
    {
        var c=new ComboBox {ItemsSource=new[]{new KeyValuePair<string,DeletionMode>("回收站",DeletionMode.RecycleBin),new("永久删除",DeletionMode.Permanent)},
            DisplayMemberPath="Key",SelectedValuePath="Value",SelectedValue=selected,MinHeight=26};return c;
    }
    public static bool Invalid(DependencyObject root)
    {
        if(Validation.GetHasError(root))return true;
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
            if(Invalid(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))return true;
        return false;
    }
}

public sealed class MonitorDialog : Window
{
    public MonitorDefinition? Result { get; private set; }
    public MonitorDialog(MainViewModel vm)
    {
        Title="添加监控目录";Width=560;Height=240;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        var panel=new DockPanel {Margin=new Thickness(12)};Content=panel;
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        var fields=new StackPanel();panel.Children.Add(fields);
        var path=new TextBox {VerticalContentAlignment=VerticalAlignment.Center,MinHeight=28};
        System.Windows.Automation.AutomationProperties.SetName(path,"监控路径");
        var pathPanel=new DockPanel();var browse=DialogUi.Button("浏览…",()=>
        {
            var picker=new OpenFolderDialog {Title="选择真实监控目录"};if(picker.ShowDialog(this)==true)path.Text=picker.FolderName;
        });
        DockPanel.SetDock(browse,Dock.Right);pathPanel.Children.Add(browse);pathPanel.Children.Add(path);
        fields.Children.Add(DialogUi.Field("监控路径",pathPanel));
        var days=new DayStepper {Width=96,HorizontalAlignment=HorizontalAlignment.Left};
        var mode=DialogUi.Modes(vm.Settings.DefaultDeletionMode);
        var options=new Grid();options.ColumnDefinitions.Add(new(){Width=new GridLength(195)});options.ColumnDefinitions.Add(new());
        options.Children.Add(DialogUi.Field("保留天数",days));
        var modeField=DialogUi.Field("删除模式",mode);Grid.SetColumn(modeField,1);options.Children.Add(modeField);fields.Children.Add(options);
        var enabled=new CheckBox {Content="启用此监控目录",IsChecked=true,Margin=new Thickness(100,5,0,3),HorizontalAlignment=HorizontalAlignment.Left};
        fields.Children.Add(enabled);
        var error=new TextBlock {Foreground=System.Windows.Media.Brushes.Firebrick,TextWrapping=TextWrapping.Wrap,FontSize=12};fields.Children.Add(error);
        var save=DialogUi.Button("保存",()=>
        {
            try
            {
                if(DialogUi.Invalid(fields))throw new ArgumentException("请将天数修正为 1–36500 的整数。");
                var m=new MonitorDefinition(Guid.NewGuid(),MonitorValidation.NormalizePath(path.Text),days.Value,(DeletionMode)mode.SelectedValue,enabled.IsChecked==true);
                vm.Validate(m);Result=m;DialogResult=true;
            }
            catch(Exception ex){error.Text=ex.Message;}
        });save.IsDefault=true;
        footer.Children.Add(save);var cancel=DialogUi.Button("取消",()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        Loaded+=(_,_)=>path.Focus();
    }
}

public sealed class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel vm)
    {
        Title="FolderNest · 设置";Width=550;Height=360;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        var panel=new DockPanel {Margin=new Thickness(16)};Content=panel;
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        var fields=new StackPanel();panel.Children.Add(fields);
        var time=new TextBox {Text=vm.Settings.DailyTime,MinHeight=26};fields.Children.Add(DialogUi.Field("每日时间",time));
        System.Windows.Automation.AutomationProperties.SetName(time,"每日检查时间 HH:mm");
        fields.Children.Add(new TextBlock {Text="本地时区 · 24 小时制，如 02:00 / 14:00。错过后登录补检查。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,4,0,8)});
        var limit=new TextBox {Text=vm.Settings.LogLimitMiB.ToString(),MinHeight=26};fields.Children.Add(DialogUi.Field("日志总 MiB",limit));
        System.Windows.Automation.AutomationProperties.SetName(limit,"日志总限额 MiB");
        var mode=DialogUi.Modes(vm.Settings.DefaultDeletionMode);fields.Children.Add(DialogUi.Field("新目录默认",mode));
        fields.Children.Add(new TextBlock {Text="日志总额 2–1024 MiB；当前与轮换文件各使用一半。删除模式仅影响以后添加的目录。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,6,0,6)});
        var error=new TextBlock {Foreground=System.Windows.Media.Brushes.Firebrick,TextWrapping=TextWrapping.Wrap};fields.Children.Add(error);
        footer.Children.Add(DialogUi.Button("保存",()=>
        {
            try
            {
                if(!int.TryParse(limit.Text,out var n))throw new ArgumentException("日志限额必须是整数。");
                vm.SaveSettings(new(time.Text.Trim(),n,(DeletionMode)mode.SelectedValue));DialogResult=true;
            }
            catch(Exception ex){error.Text=ex.Message;}
        }));var cancel=DialogUi.Button("取消",()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
    }
}

public sealed class ScheduleDialog : Window
{
    public ScheduleDialog(MainViewModel vm)
    {
        Title="选择参与自动扫描的目录";Width=650;Height=430;
        MinWidth=450;MinHeight=300;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new DockPanel {Margin=new Thickness(12)};Content=panel;
        var text=new TextBlock {Text=$"每天 {vm.Settings.DailyTime} + 当前用户登录后检查；两条全局任务共用下列选择。停用目录不能参与。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,0,4,8)};
        DockPanel.SetDock(text,Dock.Top);panel.Children.Add(text);
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        var error=new TextBlock {Foreground=System.Windows.Media.Brushes.Firebrick,TextWrapping=TextWrapping.Wrap};footer.Children.Add(error);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(buttons);
        var choices=vm.Monitors.Select(r=>new ScheduleChoice(r)).ToArray();
        var grid=new DataGrid {ItemsSource=choices,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false};
        grid.Columns.Add(new DataGridTemplateColumn {Header="参与",Width=60,CellTemplate=CheckTemplate()});
        grid.Columns.Add(new DataGridTextColumn {Header="目录",Binding=new Binding(nameof(ScheduleChoice.Path)),IsReadOnly=true,Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        grid.Columns.Add(new DataGridTextColumn {Header="状态",Binding=new Binding(nameof(ScheduleChoice.State)),IsReadOnly=true});panel.Children.Add(grid);
        buttons.Children.Add(DialogUi.Button("全部启用目录",()=>{foreach(var c in choices)c.Selected=c.Enabled;grid.Items.Refresh();}));
        buttons.Children.Add(DialogUi.Button("部署并保存",()=>
        {
            try{grid.CommitEdit();vm.Deploy(choices.Where(c=>c.Selected&&c.Enabled).Select(c=>c.Id).ToHashSet());DialogResult=true;}
            catch(Exception ex){error.Text=ex.Message;}
        }));var cancel=DialogUi.Button("取消",()=>DialogResult=false);cancel.IsCancel=true;buttons.Children.Add(cancel);
    }
    private static DataTemplate CheckTemplate()
    {
        var template=new DataTemplate();var box=new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(ScheduleChoice.Selected)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        box.SetBinding(CheckBox.IsEnabledProperty,new Binding(nameof(ScheduleChoice.Enabled)));
        box.SetValue(CheckBox.HorizontalAlignmentProperty,HorizontalAlignment.Center);box.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"参与此目录自动扫描");template.VisualTree=box;return template;
    }
    private sealed class ScheduleChoice(MonitorRow row)
    {
        public Guid Id=>row.Id;public string Path=>row.Definition.Path;public bool Enabled=>row.Enabled;
        public string State=>Enabled?"启用":"停用";public bool Selected {get;set;}=row.Scheduled;
    }
}

public sealed class ReviewWindow : Window
{
    private bool running;
    public ReviewWindow(MainViewModel vm,IReadOnlyList<DueItem> items)
    {
        Title="FolderNest · 确认到期清理";Width=1050;Height=560;MinWidth=760;MinHeight=380;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new DockPanel {Margin=new Thickness(12)};Content=panel;
        var rows=new ObservableCollection<ReviewRow>(items.Select(i=>new ReviewRow(i)));
        var view=CollectionViewSource.GetDefaultView(rows);
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        header.Children.Add(new TextBlock {Text="取消勾选本次不清理的项目。一级子文件夹按整体清理；执行前会再次校验变化。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,0,3,8)});
        var tools=new WrapPanel();header.Children.Add(tools);
        var filter=new TextBox {Width=230,MinHeight=28,Margin=new Thickness(4),VerticalContentAlignment=VerticalAlignment.Center};
        System.Windows.Automation.AutomationProperties.SetName(filter,"筛选到期项目路径");
        tools.Children.Add(new Label {Content="筛选路径",Target=filter});tools.Children.Add(filter);
        filter.TextChanged+=(_,_)=>{view.Filter=o=>((ReviewRow)o).Path.Contains(filter.Text,StringComparison.OrdinalIgnoreCase);view.Refresh();};
        var grid=new DataGrid {ItemsSource=view,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Extended};
        tools.Children.Add(DialogUi.Button("勾选筛选项",()=>{foreach(ReviewRow r in view)r.Selected=true;grid.Items.Refresh();}));
        tools.Children.Add(DialogUi.Button("取消全部勾选",()=>{foreach(var r in rows)r.Selected=false;grid.Items.Refresh();}));
        var mode=new ComboBox {ItemsSource=new[]{"使用各目录默认","本次全部回收站","本次全部永久删除"},SelectedIndex=0,MinWidth=160,Margin=new Thickness(4)};tools.Children.Add(mode);
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        var resultText=new TextBox {IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=85,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(0),Margin=new Thickness(3,6,3,3)};footer.Children.Add(resultText);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(buttons);
        var execute=DialogUi.Button("确认清理已勾选项目",()=>{});buttons.Children.Add(execute);
        var close=DialogUi.Button("关闭",()=>Close());close.IsCancel=true;buttons.Children.Add(close);
        var checkTemplate=new DataTemplate();var checkbox=new FrameworkElementFactory(typeof(CheckBox));
        checkbox.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(ReviewRow.Selected)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        checkbox.SetValue(CheckBox.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        checkbox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"勾选本次清理");checkTemplate.VisualTree=checkbox;
        grid.Columns.Add(new DataGridTemplateColumn {Header="清理",CellTemplate=checkTemplate,Width=55});
        void Column(string title,string property,DataGridLength width)=>grid.Columns.Add(new DataGridTextColumn {Header=title,Binding=new Binding(property),IsReadOnly=true,Width=width});
        Column("到期项目",nameof(ReviewRow.Path),new(1,DataGridLengthUnitType.Star));
        Column("大小 MiB",nameof(ReviewRow.Size),85);Column("到期时间",nameof(ReviewRow.Due),140);Column("默认方式",nameof(ReviewRow.Mode),90);
        panel.Children.Add(grid);
        execute.Click+=async (_,_)=>
        {
            if(running)return;grid.CommitEdit();grid.CommitEdit(DataGridEditingUnit.Row,true);
            var selected=rows.Where(r=>r.Selected).Select(r=>r.Item).ToArray();
            if(selected.Length==0){resultText.Text="请勾选至少一个项目。";return;}
            DeletionMode? overrideMode=mode.SelectedIndex==0?null:mode.SelectedIndex==1?DeletionMode.RecycleBin:DeletionMode.Permanent;
            var permanent=selected.Count(i=>(overrideMode??i.Monitor.DeletionMode)==DeletionMode.Permanent);
            if(MessageBox.Show(this,$"确认清理 {selected.Length} 项？其中 {permanent} 项将永久删除、不能从回收站还原。\n未勾选项目不会处理。","确认清理",MessageBoxButton.YesNo,permanent>0?MessageBoxImage.Warning:MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            running=true;grid.IsEnabled=false;header.IsEnabled=false;buttons.IsEnabled=false;
            resultText.Text="正在逐项校验并清理，请勿关闭程序…";
            try
            {
                var results=await vm.CleanupAsync(new(selected,overrideMode));
                var successes=results.Where(r=>r.Success).Select(r=>r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(var row in rows.Where(r=>successes.Contains(r.Path)).ToArray())rows.Remove(row);
                resultText.Text=$"成功 {successes.Count} 项；跳过/失败 {results.Count-successes.Count} 项。"+string.Join("\n",results.Where(r=>!r.Success).Select(r=>r.Path+"："+r.Error));
                vm.Status=resultText.Text.Split('\n')[0];
            }
            catch(Exception ex){resultText.Text=ex.Message;}
            finally {running=false;grid.IsEnabled=true;header.IsEnabled=true;buttons.IsEnabled=true;}
        };
        Closing+=(_,e)=>{if(running)e.Cancel=true;};
    }
    private sealed class ReviewRow(DueItem item)
    {
        public DueItem Item=>item;public bool Selected {get;set;}=true;public string Path=>item.Observation.Path;
        public string Size=>(item.Observation.Size/1048576d).ToString("N2");
        public string Due=>item.DueAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string Mode=>item.Monitor.DeletionMode==DeletionMode.RecycleBin?"回收站":"永久删除";
    }
}
