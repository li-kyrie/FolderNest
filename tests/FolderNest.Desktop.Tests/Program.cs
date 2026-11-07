using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Xml.Linq;
using FolderNest.Core;
using FolderNest.Infrastructure;
using FolderNest.Desktop;
using FolderNest.Desktop.Controls;
using Microsoft.Data.Sqlite;
using TaskStatus = FolderNest.Core.TaskStatus;

internal static class Program
{
    private static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);if(child is T found)yield return found;
            foreach(var nested in Children<T>(child))yield return nested;
        }
    }
    private static T Await<T>(Task<T> task)
    {
        if(!task.IsCompleted)
        {
            var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;
            _=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
    [STAThread]
    private static int Main(string[] args)
    {
        var root=Path.Combine(Path.GetTempPath(),"FolderNest-desktop-tests-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var passed=0;
        void Check(string name,Action test){test();passed++;Console.WriteLine("PASS "+name);}
        void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        try
        {
            var app=new Application();
            using(var resource=typeof(Program).Assembly.GetManifestResourceStream("ProductionApp.xaml")!)
            {
                var xaml=XDocument.Load(resource);XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var dictionary=new XElement(ns+"ResourceDictionary",xaml.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration),xaml.Root.Element(ns+"Application.Resources")!.Elements());
                app.Resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            }
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var path=Directory.CreateDirectory(Path.Combine(root,"monitor")).FullName;
            var file=Path.Combine(path,"old.txt");File.WriteAllText(file,"keep");File.SetLastWriteTimeUtc(file,new DateTime(2024,1,1));
            var repo=new SqliteRepository(Path.Combine(root,"data","foldernest.db"));var scheduler=new FakeScheduler();
            using var vm=new MainViewModel(repo,new NullLog(),new(Path.Combine(root,"data"),false),scheduler);
            MonitorRow? row=null;
            Check("Add appears, starts timing now and automatically installs background plans",()=>
            {
                var before=DateTimeOffset.UtcNow;row=Await(vm.AddAsync(new(Guid.NewGuid(),path)));
                Assert(vm.Monitors.Count==1&&repo.GetMonitors().Count==1,"add not persisted/displayed");
                Assert(repo.GetObservations(row.Id).Single().FirstSeenUtc>=before,"join did not start timing");
                Assert(scheduler.DeployCount==1&&scheduler.State==TaskDeployment.Installed,"automatic plans missing");
            });
            Check("Invalid path remains draft and Escape restores configuration",()=>
            {
                row!.Path="Z:\\not-a-real-path-"+Guid.NewGuid();
                Assert(row.HasErrors&&repo.GetMonitors()[0].Path==path&&row.Definition.Path==path,"invalid path persisted");
                row.CancelEdit();Assert(!row.HasErrors&&row.Path==path,"invalid draft not cleared");
            });
            Check("Days and fixed options reject invalid values",()=>
            {
                row!.RetentionDays=0;Assert(row.HasErrors&&repo.GetMonitors()[0].RetentionDays==30,"bad days persisted");row.CancelEdit();
                row.DeletionMode=(DeletionMode)999;Assert(row.HasErrors&&repo.GetMonitors()[0].DeletionMode==DeletionMode.RecycleBin,"bad mode persisted");row.CancelEdit();
                var rule=new IntegerDaysRule();foreach(var value in new[]{"","1.5","-1","text","36501"})Assert(!rule.Validate(value,CultureInfo.InvariantCulture).IsValid,"bad days rule");
                Assert(rule.Validate("1",CultureInfo.InvariantCulture).IsValid,"valid days rejected");
            });
            Check("Production typed days stay draft; confirm persists before success and scanning uses new expiry",()=>
            {
                var observed=repo.GetObservations(row!.Id).Single();var old=DateTimeOffset.UtcNow.AddDays(-3);
                var aged=observed with{FirstSeenUtc=old,LastActivityUtc=old};repo.ReplaceObservations(row.Id,[aged]);
                bool accept=false;int prompts=0;int successes=0;
                var main=new MainWindow(vm,pending=>
                {
                    prompts++;Assert(pending.Count==1&&pending[0].Candidate.RetentionDays==1,"typed days absent from confirmation");
                    Assert(repo.GetMonitors().Single().RetentionDays==30,"configuration saved before confirmation");
                    Assert(MonitorEditConfirmationWindow.Describe(pending).Contains("30 → 1"),"missing old/new summary");return accept;
                },()=>
                {
                    successes++;Assert(repo.GetMonitors().Single().RetentionDays==1&&vm.Status.StartsWith("配置已保存生效"),"success preceded database commit");
                });
                var content=(FrameworkElement)main.Content;content.Measure(new Size(1076,670));content.Arrange(new Rect(0,0,1076,670));content.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                var grid=(DataGrid)main.FindName("MonitorGrid");grid.SelectedItem=row;
                var stepper=Children<DayStepper>(grid).Single();var editor=(TextBox)stepper.FindName("Editor");
                editor.SetCurrentValue(TextBox.TextProperty,"1");
                editor.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,0,editor,grid){RoutedEvent=System.Windows.Input.Keyboard.LostKeyboardFocusEvent});
                Assert(row.RetentionDays==1&&row.EditState=="待确认"&&row.Definition.RetentionDays==30,"lost-focus typed value not staged");
                Assert(Await(vm.ScanAsync()).DueItems.Count==0,"unconfirmed draft changed scan policy");
                var confirm=Children<Button>(content).Single(b=>Equals(b.Content,"确认修改"));
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(prompts==1&&successes==0&&repo.GetMonitors().Single().RetentionDays==30,"return-to-edit saved or showed success");
                accept=true;confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(prompts==2&&successes==1&&!row.HasPendingChanges&&row.EditState=="已生效","confirmed changes not accepted");
                Assert(repo.GetObservations(row.Id).Single()==aged,"changing days reset timing");
                Assert(Await(vm.ScanAsync()).DueItems.Count==1,"scan did not honor confirmed one-day retention");
                Assert(grid.SelectedItems.Contains(row),"confirmation lost row selection");
                editor.SetCurrentValue(TextBox.TextProperty,"1.5");Assert(!stepper.CommitInput(),"fractional days accepted");
                var cancel=Children<Button>(content).Single(b=>Equals(b.Content,"取消修改"));cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(editor.Text=="1"&&!Validation.GetHasError(editor),"cancel retained invalid editor text");
                editor.SetCurrentValue(TextBox.TextProperty,"30");Assert(stepper.CommitInput(),"valid restore input rejected");vm.ApplyEdits();
                repo.ReplaceObservations(row.Id,[observed]);
            });
            Check("Path, state and deletion-mode drafts cancel without touching persisted configuration or history",()=>
            {
                var saved=row!.Definition;var history=repo.GetObservations(row.Id).ToArray();
                var alternate=Directory.CreateDirectory(Path.Combine(root,"alternate")).FullName;
                row.Path=alternate;row.Enabled=false;row.DeletionMode=DeletionMode.Permanent;
                Assert(row.HasPendingChanges&&!row.HasErrors&&row.Definition==saved&&repo.GetMonitors().Single()==saved,"draft leaked into stored configuration");
                Assert(repo.GetObservations(row.Id).SequenceEqual(history),"path draft cleared timing");
                vm.CancelEdits();Assert(row.Path==saved.Path&&row.Enabled==saved.Enabled&&row.DeletionMode==saved.DeletionMode&&!row.HasPendingChanges,"cancel failed");
                row.Enabled=false;row.DeletionMode=DeletionMode.Permanent;vm.ApplyEdits();
                Assert(!repo.GetMonitors().Single().Enabled&&repo.GetMonitors().Single().DeletionMode==DeletionMode.Permanent,"confirmed fixed options not stored");
                row.Enabled=true;row.DeletionMode=DeletionMode.RecycleBin;vm.ApplyEdits();
                Assert(repo.GetObservations(row.Id).SequenceEqual(history),"option edits changed timing");
            });
            Check("Failed multi-row save rolls back database and path timing; no success state is reported",()=>
            {
                var database=Path.Combine(root,"atomic","foldernest.db");var atomic=new SqliteRepository(database);
                using var editing=new MainViewModel(atomic,new NullLog(),new(Path.Combine(root,"atomic"),false),new FakeScheduler());
                var x=Await(editing.AddAsync(new(Guid.NewGuid(),Directory.CreateDirectory(Path.Combine(root,"atomic-x")).FullName)));
                var y=Await(editing.AddAsync(new(Guid.NewGuid(),Directory.CreateDirectory(Path.Combine(root,"atomic-y")).FullName)));
                var observation=new Observation(x.Id,Path.Combine(x.Path,"keep.txt"),false,1,"old",DateTimeOffset.UtcNow.AddDays(-2),DateTimeOffset.UtcNow.AddDays(-2));
                atomic.ReplaceObservations(x.Id,[observation]);var originals=atomic.GetMonitors().ToArray();
                x.Path=Directory.CreateDirectory(Path.Combine(root,"atomic-new")).FullName;y.RetentionDays=1;
                using(var connection=new SqliteConnection("Data Source="+database))
                {
                    connection.Open();using var trigger=connection.CreateCommand();
                    trigger.CommandText=$"CREATE TRIGGER reject_test_save BEFORE UPDATE ON monitors WHEN NEW.id='{y.Id}' BEGIN SELECT RAISE(ABORT,'test write failure'); END";
                    trigger.ExecuteNonQuery();
                }
                var failed=false;try{editing.ApplyEdits();}catch(SqliteException){failed=true;}
                Assert(failed&&editing.Status.StartsWith("配置保存失败")&&x.HasPendingChanges&&y.HasPendingChanges,"failed save reported success or cleared drafts");
                Assert(atomic.GetMonitors().SequenceEqual(originals)&&atomic.GetObservations(x.Id).Single()==observation,"failed batch partially committed or lost original timing");
            });
            Check("Toggle preserves row and timing, including unavailable directories",()=>
            {
                var original=row;var history=repo.GetObservations(row!.Id).ToArray();vm.Toggle([row]);
                Assert(ReferenceEquals(original,vm.Monitors[0])&&!row.Enabled,"toggle changed row identity");
                Directory.Move(path,path+"-offline");vm.Toggle([row]);vm.Toggle([row]);
                Assert(!row.Enabled&&history.SequenceEqual(repo.GetObservations(row.Id)),"offline toggle reset timing");
                Directory.Move(path+"-offline",path);vm.Toggle([row]);
            });
            var otherPath=Directory.CreateDirectory(Path.Combine(root,"second")).FullName;
            var second=Await(vm.AddAsync(new(Guid.NewGuid(),otherPath,Enabled:false)));
            var third=Await(vm.AddAsync(new(Guid.NewGuid(),Directory.CreateDirectory(Path.Combine(root,"third")).FullName)));
            var fourth=Await(vm.AddAsync(new(Guid.NewGuid(),Directory.CreateDirectory(Path.Combine(root,"fourth")).FullName)));
            Check("Enable and disable affect only selected rows",()=>
            {
                vm.Toggle([second]);Assert(second.Enabled&&row!.Enabled&&third.Enabled,"enable affected other rows");
                vm.Toggle([second]);Assert(!second.Enabled&&row!.Enabled&&third.Enabled,"disable affected other rows");
                Assert(scheduler.DeployCount==1,"adding directories reinstalled intact plans");
            });
            Check("Persistent ordering preserves selected objects and all timers",()=>
            {
                var old=repo.GetObservations(row!.Id).ToArray();vm.MoveSelection([second,fourth],-1);
                Assert(vm.Monitors.SequenceEqual(new[]{second,row!,fourth,third}),"noncontiguous up order wrong");
                vm.MoveSelection([second,fourth],1);
                Assert(vm.Monitors.SequenceEqual(new[]{row!,second,third,fourth}),"noncontiguous down order wrong");
                vm.Reorder([fourth,third,second,row!]);
                Assert(repo.GetMonitors().Select(m=>m.Id).SequenceEqual(vm.Monitors.Select(m=>m.Id)),"order not saved");
                Assert(old.SequenceEqual(repo.GetObservations(row.Id)),"ordering changed timing");
            });
            Check("Reset directory tree supports whole, partial and empty selection",()=>
            {
                var m=row!.Definition;
                var group=new ResetCandidateGroup(m,[new(file,false,4,"a"),new(Path.Combine(path,"sibling"),false,4,"b")],null);
                var choice=new ResetChoice(group,true);
                Assert(choice.ToSelection()?.Paths is null&&choice.Checked==true,"whole directory selection lost");
                choice.Children[0].Checked=false;
                Assert(choice.Checked is null&&choice.ToSelection()!.Paths!.SetEquals(new[]{Path.Combine(path,"sibling")}),"partial selection incorrect");
                choice.Checked=false;Assert(choice.ToSelection() is null&&choice.Children.All(c=>c.Checked==false),"clear left hidden selections");
                var empty=new ResetChoice(new(second.Definition,[],null));empty.Checked=true;
                Assert(empty.ToSelection() is {Paths:null},"empty whole-directory selection not allowed");
            });
            Check("Reset inventory reads disabled directories without starting timers",()=>
            {
                var added=Path.Combine(otherPath,"new.txt");File.WriteAllText(added,"new");
                var candidates=Await(vm.GetResetCandidatesAsync());
                Assert(candidates.Single(c=>c.Monitor.Id==second.Id).Units.Count==1,"disabled files absent in reset dialog");
                Assert(repo.GetObservations(second.Id).Count==0&&!second.Enabled,"opening dialog changed state");
            });
            Check("Reset only chosen files and retain monitor state",()=>
            {
                var old=DateTimeOffset.UtcNow.AddDays(-20);var observation=repo.GetObservations(row!.Id).Single() with{FirstSeenUtc=old,LastActivityUtc=old};
                repo.ReplaceObservations(row.Id,[observation]);var stamp=File.GetLastWriteTimeUtc(file);
                var result=Await(vm.ResetAsync([new(row.Id,new HashSet<string>{file})]));
                Assert(result.Single().ResetCount==1&&repo.GetObservations(row.Id).Single().FirstSeenUtc>old,"reset not persisted");
                Assert(File.GetLastWriteTimeUtc(file)==stamp&&row.Enabled,"reset changed file or monitor state");
            });
            Check("App and data hierarchies are rejected",()=>
            {
                bool rejected=false;try{Await(vm.AddAsync(new(Guid.NewGuid(),Path.Combine(root,"data"))));}catch(ArgumentException){rejected=true;}Assert(rejected,"data hierarchy allowed");
            });
            var revealFile=Path.Combine(path,"定位 测试, report.txt");File.WriteAllText(revealFile,"keep");
            var revealFolder=Directory.CreateDirectory(Path.Combine(path,"定位 文件夹, test")).FullName;
            var missingFile=Path.Combine(path,"missing.txt");
            DueItem Due(string target,bool directory=false)=>new(row!.Definition,new(row.Id,target,directory,4,"test",DateTimeOffset.UtcNow.AddDays(-31),DateTimeOffset.UtcNow.AddDays(-31)),DateTimeOffset.UtcNow.AddDays(-1));
            var reviewItems=new[]{Due(revealFile),Due(revealFolder,true),Due(missingFile)};
            void Layout(Window window)
            {
                var content=(FrameworkElement)window.Content;content.Measure(new Size(1026,510));content.Arrange(new Rect(0,0,1026,510));content.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            }
            void WaitForReveal(TextBox status)
            {
                var timeout=DateTime.UtcNow.AddSeconds(10);
                while(status.Text=="正在资源管理器中定位…"&&DateTime.UtcNow<timeout)
                    Await(Task.Delay(10).ContinueWith(_=>true));
                Assert(status.Text!="正在资源管理器中定位…","Explorer dispatch did not complete");
            }
            Check("Review reveal uses one highlighted row, preserves cleanup checks and exact Unicode paths",()=>
            {
                var calls=new List<string>();var review=new ReviewWindow(vm,reviewItems,p=>calls.Add(p));Layout(review);
                var content=(DependencyObject)review.Content;
                var grid=Children<DataGrid>(content).Single();var button=Children<Button>(content).Single(b=>Equals(b.Content,"在资源管理器中显示"));
                var status=Children<TextBox>(content).Single(t=>t.IsReadOnly);
                Assert(!button.IsEnabled,"reveal enabled without selection");
                // Cleanup choices are independent of row highlighting, including unchecked items.
                var chosen=grid.Items[0];var selectedProperty=chosen.GetType().GetProperty("Selected")!;selectedProperty.SetValue(chosen,false);
                var checks=grid.Items.Cast<object>().Select(o=>(bool)selectedProperty.GetValue(o)!).ToArray();
                grid.SelectedItem=chosen;Assert(button.IsEnabled,"single-row reveal disabled");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(!button.IsEnabled,"reveal not disabled while dispatching");WaitForReveal(status);
                Assert(calls.SequenceEqual(new[]{revealFile})&&status.Text.StartsWith("已请求资源管理器显示"),"wrong file path or success feedback");
                grid.SelectedItems.Add(grid.Items[1]);Assert(!button.IsEnabled,"reveal enabled for multiple rows");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(calls.Count==1,"multiple selection dispatched");
                grid.SelectedItems.Clear();grid.SelectedItem=grid.Items[1];button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForReveal(status);
                Assert(calls.SequenceEqual(new[]{revealFile,revealFolder}),"folder was not dispatched exactly");
                Assert(checks.SequenceEqual(grid.Items.Cast<object>().Select(o=>(bool)selectedProperty.GetValue(o)!)),"reveal changed cleanup checks");
                Assert(File.ReadAllText(revealFile)=="keep"&&Directory.Exists(revealFolder),"reveal modified targets");
                grid.SelectedItems.Clear();Assert(!button.IsEnabled,"reveal retained stale enabled state");
            });
            Check("Review missing paths and shell failures show inline errors without cleanup or shell fallback",()=>
            {
                var calls=0;var review=new ReviewWindow(vm,reviewItems,_=>{calls++;throw new IOException("测试：资源管理器不可用");});Layout(review);
                var content=(DependencyObject)review.Content;
                var grid=Children<DataGrid>(content).Single();var button=Children<Button>(content).Single(b=>Equals(b.Content,"在资源管理器中显示"));
                var status=Children<TextBox>(content).Single(t=>t.IsReadOnly);
                grid.SelectedItem=grid.Items[2];button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForReveal(status);
                Assert(calls==0&&status.Text.Contains("不存在")&&button.IsEnabled,"missing path dispatched or failed to recover");
                grid.SelectedItem=grid.Items[0];button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForReveal(status);
                Assert(calls==1&&status.Text.Contains("测试：资源管理器不可用")&&button.IsEnabled,"shell failure swallowed or disabled button stuck");
                Assert(File.Exists(revealFile)&&grid.Items.Count==3,"failure removed a target or review row");
                var invalid=new ReviewWindow(vm,[Due("relative.txt")],_=>calls++);Layout(invalid);
                var invalidContent=(DependencyObject)invalid.Content;
                var invalidGrid=Children<DataGrid>(invalidContent).Single();invalidGrid.SelectedIndex=0;
                Children<Button>(invalidContent).Single(b=>Equals(b.Content,"在资源管理器中显示")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var invalidStatus=Children<TextBox>(invalidContent).Single(t=>t.IsReadOnly);WaitForReveal(invalidStatus);
                Assert(calls==1&&invalidStatus.Text.Contains("路径无效"),"relative path passed to shell");
            });
            DueItem DisplayDue(string monitorPath,string target,bool directory=false)
            {
                var m=new MonitorDefinition(Guid.NewGuid(),monitorPath);
                return new(m,new(m.Id,target,directory,123456,"display-only",DateTimeOffset.UtcNow.AddDays(-31),DateTimeOffset.UtcNow.AddDays(-31)),DateTimeOffset.UtcNow.AddDays(-1));
            }
            Check("Review bolds only the owning monitor prefix and preserves full original paths",()=>
            {
                var examples=new (string Root,string Full,string Prefix)[]
                {
                    (@"D:\下载 暂存",@"D:\下载 暂存\报告, 2026.txt",@"D:\下载 暂存"),
                    (@"D:\下载 暂存\",@"D:\下载 暂存\一级文件夹\报告.txt",@"D:\下载 暂存"),
                    (@"D:\Downloads",@"d:\DOWNLOADS\Report.txt",@"d:\DOWNLOADS"),
                    (@"D:/Downloads/",@"D:\Downloads\Report.txt",@"D:\Downloads"),
                    (@"\\server\share\收件箱",@"\\server\share\收件箱\文档.txt",@"\\server\share\收件箱"),
                    (@"D:\Downloads",@"D:\Downloads-old\other.txt",""),
                    (@"D:\Downloads",@"D:\Elsewhere\Downloads\other.txt","")
                };
                foreach(var sample in examples)
                {
                    var review=new ReviewWindow(vm,[DisplayDue(sample.Root,sample.Full)],_=>{});Layout(review);
                    var content=(DependencyObject)review.Content;var grid=Children<DataGrid>(content).Single();
                    var text=Children<TextBlock>(grid).Single(t=>t.Inlines.OfType<Run>().Count()==2);
                    var parts=text.Inlines.OfType<Run>().ToArray();
                    Assert(parts[0].Text==sample.Prefix&&parts[0].FontWeight==FontWeights.Bold,"wrong monitor emphasis: "+sample.Full);
                    Assert(parts[1].FontWeight==FontWeights.Normal&&parts[0].Text+parts[1].Text==sample.Full,"path text changed or suffix emphasized");
                    Assert(Equals(text.ToolTip,sample.Full)&&text.TextWrapping==TextWrapping.Wrap,"missing full-path tooltip or wrapping");
                    Assert(System.Windows.Automation.AutomationProperties.GetName(text)==sample.Full,"accessible path incomplete");
                    Assert(((System.Windows.Data.Binding)grid.Columns[1].ClipboardContentBinding).Path.Path=="Path"&&grid.Columns[1].SortMemberPath=="Path","clipboard or sorting no longer uses full path");
                    Assert((bool)grid.Items[0].GetType().GetProperty("Selected")!.GetValue(grid.Items[0])!,"display changed cleanup selection");
                }
            });
            Check("Production windows use taskbar icon, native multiselect and simplified columns",()=>
            {
                var main=new MainWindow(vm);Assert(main.ShowInTaskbar&&main.Icon is not null,"missing taskbar icon");
                Assert(vm.ConfigurationText.Contains(vm.Paths.DatabasePath),"configuration location missing");
                var grid=(DataGrid)main.FindName("MonitorGrid");
                Assert(grid.SelectionMode==DataGridSelectionMode.Extended&&grid.Columns.Count==6&&!grid.CanUserSortColumns,"unexpected grid columns/selection");
                var monitor=new MonitorDialog(vm);Assert(monitor.Width==560&&monitor.Height==240,"dialog resized");
                _=new SettingsWindow(vm);_=new RemoveMonitorsWindow(vm,[second]);_=new ResetTimingWindow(vm,[row!]);_=new ReviewWindow(vm,[]);
                if(args.Length==2&&args[0]=="--render-ui")
                {
                    Directory.CreateDirectory(args[1]);
                    void Render(Window window,int width,int height,string name)
                    {
                        var content=(FrameworkElement)window.Content;
                        content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
                        Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                        var background=new DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(SystemColors.WindowBrush,null,new Rect(0,0,width,height));
                        bitmap.Render(background);bitmap.Render(content);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream=File.Create(Path.Combine(args[1],name+".png"));encoder.Save(stream);
                    }
                    Render(main,1076,670,"main-default");Render(main,736,510,"main-small");
                    Assert(grid.ActualHeight>70,"small window clipped directory list");
                    Render(monitor,536,190,"add-monitor");
                    Render(new RemoveMonitorsWindow(vm,[second]),676,380,"remove-monitors");
                    Render(new ImportPreviewWindow([row!.Definition]),776,350,"import-preview");
                    var displayItems=new[]{
                        DisplayDue(@"D:\下载\临时收件箱",@"D:\下载\临时收件箱\报告, 2026.pdf"),
                        DisplayDue(@"E:\工作资料\待整理",@"E:\工作资料\待整理\旧项目附件",true),
                        DisplayDue(@"D:\下载\临时收件箱",@"D:\下载\临时收件箱\一份文件名称比较长的过期文档用于检查窄窗口显示.docx"),
                        DisplayDue(@"E:\工作资料\需要暂时保留的项目归档与共享材料收集目录",@"E:\工作资料\需要暂时保留的项目归档与共享材料收集目录\附件资料.zip")};
                    var review=new ReviewWindow(vm,displayItems,_=>{});
                    Render(review,1026,510,"review-default");
                    var reviewGrid=Children<DataGrid>((DependencyObject)review.Content).Single();reviewGrid.SelectedIndex=0;
                    Render(review,736,330,"review-small");
                    Assert(reviewGrid.ActualHeight>70,"small review window clipped list");
                    row!.RetentionDays=1;row.DeletionMode=DeletionMode.Permanent;
                    Render(new MonitorEditConfirmationWindow([row]),736,390,"confirm-edit");
                    Render(main,1076,670,"main-pending");vm.CancelEdits();
                }
            });
            File.Delete(revealFile);Directory.Delete(revealFolder);
            Check("Empty selected scan is rejected instead of scanning every directory",()=>
            {
                bool rejected=false;try{Await(vm.ScanAsync([]));}catch(ArgumentException){rejected=true;}
                Assert(rejected,"empty selection scanned all");
            });
            Check("Deleting and readding resets only its own timing, not real files",()=>
            {
                var prior=repo.GetObservations(row!.Id).Single();vm.Remove([second,third,fourth]);
                Assert(scheduler.RemoveCount==0&&repo.GetObservations(row.Id).Single()==prior,"removing other monitors reset timing/schedule");
                vm.Remove([row]);Assert(scheduler.RemoveCount==1&&File.Exists(file)&&repo.GetObservations(row.Id).Count==0,"last removal touched files or left plans");
                var fresh=Await(vm.AddAsync(new(Guid.NewGuid(),path)));
                Assert(fresh.Id!=row.Id&&repo.GetObservations(fresh.Id).Single().FirstSeenUtc>prior.FirstSeenUtc,"readd kept deleted timing");
                vm.Remove([fresh]);
            });
            Check("Scheduling failure is visible but configuration and timing survive",()=>
            {
                var failedScheduler=new FakeScheduler {Fail=true};
                using var failedVm=new MainViewModel(repo,new NullLog(),new(Path.Combine(root,"data"),false),failedScheduler);
                var added=Await(failedVm.AddAsync(new(Guid.NewGuid(),path)));
                Assert(failedVm.BackgroundError.Length>0&&repo.GetMonitors().Count==1&&repo.GetObservations(added.Id).Count==1,"schedule failure lost config or reported success");
                Assert(Await(failedVm.ScanAsync()).Errors.Count==0,"manual scan failed after schedule error");failedVm.Remove([added]);
            });
            Console.WriteLine($"{passed} desktop component tests passed.");app.Shutdown();return 0;
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
    private sealed class NullLog:IAuditLog {public void Write(string operation,string message) { }}
    private sealed class FakeScheduler:ITaskScheduler
    {
        public TaskDeployment State {get;set;}=TaskDeployment.NotInstalled;
        public bool Fail {get;set;}
        public int DeployCount {get;private set;}
        public int RemoveCount {get;private set;}
        public TaskStatus GetStatus()=>new(State,"test");
        public TaskStatus Deploy(ScheduleSettings settings,string executable)
        {if(Fail)throw new IOException("test schedule unavailable");DeployCount++;State=TaskDeployment.Installed;return GetStatus();}
        public TaskStatus Remove(){RemoveCount++;State=TaskDeployment.NotInstalled;return GetStatus();}
    }
}
