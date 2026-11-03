using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using FolderNest.Core;
using FolderNest.Infrastructure;
using FolderNest.Desktop;
using FolderNest.Desktop.Controls;
using Microsoft.Data.Sqlite;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root=Path.Combine(Path.GetTempPath(),"FolderNest-desktop-tests-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var passed=0;
        void Check(string name,Action test){test();passed++;Console.WriteLine("PASS "+name);}
        void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        try
        {
            var app=new Application();
            var path=Directory.CreateDirectory(Path.Combine(root,"monitor")).FullName;
            var repo=new SqliteRepository(Path.Combine(root,"data","foldernest.db"));
            using var vm=new MainViewModel(repo,new NullLog(),new(Path.Combine(root,"data"),false));
            MonitorRow? row=null;
            Check("Add immediately appears and persists",()=>{row=vm.Add(new(Guid.NewGuid(),path));Assert(vm.Monitors.Count==1&&repo.GetMonitors().Count==1,"add not persisted/displayed");});
            Check("Invalid path remains a draft; Escape rollback clears validation",()=>
            {
                row!.Path="Z:\\not-a-real-path-"+Guid.NewGuid();
                Assert(row.HasErrors&&repo.GetMonitors()[0].Path==path&&row.Definition.Path==path,"invalid path persisted");
                row.CancelEdit();Assert(!row.HasErrors&&row.Path==path,"invalid draft not cleared");
            });
            Check("Days and fixed options reject invalid values",()=>
            {
                row!.RetentionDays=0;Assert(row.HasErrors&&repo.GetMonitors()[0].RetentionDays==30,"bad days persisted");
                row.CancelEdit();row.DeletionMode=(DeletionMode)999;Assert(row.HasErrors&&repo.GetMonitors()[0].DeletionMode==DeletionMode.RecycleBin,"bad mode persisted");row.CancelEdit();
                var rule=new IntegerDaysRule();foreach(var value in new[]{"","1.5","-1","text","36501"})Assert(!rule.Validate(value,CultureInfo.InvariantCulture).IsValid,"bad days rule");
                Assert(rule.Validate("1",CultureInfo.InvariantCulture).IsValid,"valid days rejected");
            });
            Check("Toggle keeps row identity; offline folder can be suspended",()=>
            {
                var original=row;vm.Toggle([row!]);Assert(ReferenceEquals(original,vm.Monitors[0])&&!row!.Enabled,"toggle changed row identity");
                Directory.Delete(path);vm.Toggle([row!]);vm.Toggle([row!]);Assert(!row!.Enabled,"offline suspension failed");Directory.CreateDirectory(path);vm.Toggle([row]);
            });
            Check("Deployment distinguishes selection from actual installed state",()=>
            {
                row!.Scheduled=true;row.SetDeployment(TaskDeployment.NotInstalled);Assert(row.ScheduleText=="未部署","false installed state");
                row.SetDeployment(TaskDeployment.Partial);Assert(row.ScheduleText=="部分部署","missing partial state");
                row.Scheduled=false;Assert(row.ScheduleText=="未参与","unselected state incorrect");
                bool rejected=false;try{vm.Deploy(new HashSet<Guid>());}catch(ArgumentException){rejected=true;}Assert(rejected,"empty deployment reported success");
            });
            Check("App and data folder hierarchies are rejected",()=>
            {
                bool rejected=false;try{vm.Add(new(Guid.NewGuid(),Path.Combine(root,"data")));}catch(ArgumentException){rejected=true;}Assert(rejected,"data hierarchy allowed");
            });
            Check("All production windows construct with expected native controls",()=>
            {
                var main=new MainWindow(vm);Assert(main.ShowInTaskbar&&main.Icon is not null,"missing taskbar icon");
                var grid=(DataGrid)main.FindName("MonitorGrid");Assert(grid.SelectionMode==DataGridSelectionMode.Extended,"multiselect disabled");
                var monitor=new MonitorDialog(vm);Assert(monitor.Width==560&&monitor.Height==240,"dialog resized");
                _=new SettingsWindow(vm);_=new ScheduleDialog(vm);_=new ReviewWindow(vm,[]);
            });
            Check("Empty monitor scan succeeds without a cleanup queue",()=>
            {
                var result=vm.ScanAsync().GetAwaiter().GetResult();Assert(result.Errors.Count==0&&result.DueItems.Count==0,"empty scan failed");
            });
            Check("Undeployed directories do not scan from background ticks",()=>
            {
                var file=Path.Combine(path,"not-auto.txt");File.WriteAllText(file,"test");
                vm.TickAsync().GetAwaiter().GetResult();Assert(repo.GetObservations(row!.Id).Count==0,"undeployed folder auto-scanned");
                vm.ScanAsync().GetAwaiter().GetResult();Assert(repo.GetObservations(row.Id).Count==1,"manual scan unavailable");
            });
            // No UI synchronization context here: all dispatcher-owned operations above complete synchronously.
            Check("Removing monitor preserves actual folder",()=>{vm.Remove([row!]);Assert(vm.Monitors.Count==0&&Directory.Exists(path),"remove deleted real folder");});
            Console.WriteLine($"{passed} desktop component tests passed.");app.Shutdown();return 0;
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
    private sealed class NullLog:IAuditLog {public void Write(string operation,string message) { }}
}
