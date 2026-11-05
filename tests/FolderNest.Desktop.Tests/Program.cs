using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
            Check("Production windows use taskbar icon, native multiselect and simplified columns",()=>
            {
                var main=new MainWindow(vm);Assert(main.ShowInTaskbar&&main.Icon is not null,"missing taskbar icon");
                Assert(vm.ConfigurationText.Contains(vm.Paths.DatabasePath),"configuration location missing");
                var grid=(DataGrid)main.FindName("MonitorGrid");
                Assert(grid.SelectionMode==DataGridSelectionMode.Extended&&grid.Columns.Count==5&&!grid.CanUserSortColumns,"unexpected grid columns/selection");
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
                    Render(main,1076,630,"main-default");Render(main,736,450,"main-small");
                    Assert(grid.ActualHeight>70,"small window clipped directory list");
                    Render(monitor,536,190,"add-monitor");
                    Render(new RemoveMonitorsWindow(vm,[second]),676,380,"remove-monitors");
                    Render(new ImportPreviewWindow([row!.Definition]),776,350,"import-preview");
                }
            });
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
