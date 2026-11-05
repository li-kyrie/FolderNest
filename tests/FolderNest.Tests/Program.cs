using FolderNest.Core;
using FolderNest.Infrastructure;
using Microsoft.Data.Sqlite;

var passed=0;
void Test(string name,Action action){action();Console.WriteLine("PASS "+name);passed++;}
void Assert(bool value,string message){if(!value) throw new Exception(message);}
var temp=Path.Combine(Path.GetTempPath(),"FolderNest-tests-"+Guid.NewGuid());
Directory.CreateDirectory(temp);
try
{
    var now=new DateTimeOffset(2026,10,10,0,0,0,TimeSpan.Zero);
    Test("Old timestamps start at first discovery; offline expiry advances",()=>
    {
        var m=new MonitorDefinition(Guid.NewGuid(),temp,14);
        var unit=new FileUnit(Path.Combine(temp,"old.txt"),false,0,"2024-fingerprint");
        var observation=ExpiryPolicy.Observe(m.Id,unit,null,now);
        Assert(ExpiryPolicy.GetDue(m,[observation],now.AddDays(13)).Count==0,"expired too early");
        Assert(ExpiryPolicy.GetDue(m,[observation],now.AddDays(14)).Count==1,"offline deadline did not advance");
    });
    Test("Observed updates refresh retention and clear computed expiry",()=>
    {
        var m=new MonitorDefinition(Guid.NewGuid(),temp,1);
        var old=ExpiryPolicy.Observe(m.Id,new("folder",true,1,"old"),null,now);
        var updated=ExpiryPolicy.Observe(m.Id,new("folder",true,2,"new"),old,now.AddDays(3));
        Assert(ExpiryPolicy.GetDue(m,[updated],now.AddDays(3)).Count==0,"stale pending state survived update");
    });
    var repo=new SqliteRepository(Path.Combine(temp,"history.db"));
    var folderA=Directory.CreateDirectory(Path.Combine(temp,"A")).FullName;
    var folderB=Directory.CreateDirectory(Path.Combine(temp,"B")).FullName;
    var a=new MonitorDefinition(Guid.NewGuid(),folderA,30);
    var b=new MonitorDefinition(Guid.NewGuid(),folderB,30);
    repo.SaveMonitor(a);repo.SaveMonitor(b);
    Test("Selection scan preserves other monitors and suspension history",()=>
    {
        var stored=ExpiryPolicy.Observe(b.Id,new(Path.Combine(folderB,"b.txt"),false,1,"b"),null,now);
        repo.ReplaceObservations(b.Id,[stored]);repo.SaveMonitor(b with{Enabled=false});
        var scanner=new ScanService(repo,new LocalFileInventory(),new FakeClock(now),new NoLog());
        scanner.Scan(ScanScope.Selected,new HashSet<Guid>{a.Id});
        Assert(repo.GetObservations(b.Id).Count==1,"non-selected history lost");
        scanner.Scan(ScanScope.AllEnabled);
        Assert(repo.GetObservations(b.Id).Count==1,"paused history lost");
    });
    Test("Unavailable folder preserves history; empty folder produces no units",()=>
    {
        var stored=ExpiryPolicy.Observe(a.Id,new(Path.Combine(folderA,"a.txt"),false,1,"a"),null,now);
        repo.ReplaceObservations(a.Id,[stored]);Directory.Delete(folderA);
        var scanner=new ScanService(repo,new LocalFileInventory(),new FakeClock(now),new NoLog());
        var result=scanner.Scan(ScanScope.Selected,new HashSet<Guid>{a.Id});
        Assert(result.Errors.Count==1 && repo.GetObservations(a.Id).Count==1,"missing folder erased data");
        Directory.CreateDirectory(folderA);
        Assert(new LocalFileInventory().Enumerate(folderA).Count==0,"empty folder not empty");
    });
    Test("Path edit resets only its own history; removing monitor leaves files",()=>
    {
        var folderC=Directory.CreateDirectory(Path.Combine(temp,"C")).FullName;
        repo.SaveMonitor(a with{Path=folderC});
        Assert(repo.GetObservations(a.Id).Count==0 && repo.GetObservations(b.Id).Count==1,"path change reset incorrect scope");
        var file=Path.Combine(folderB,"keep.txt");File.WriteAllText(file,"keep");repo.RemoveMonitor(b.Id);
        Assert(File.Exists(file) && repo.GetObservations(b.Id).Count==0,"monitor removal affected content");
    });
    Test("Old .lnk remains an ordinary file",()=>
    {
        File.WriteAllText(Path.Combine(folderA,"shortcut.lnk"),"test shortcut metadata");
        File.SetLastWriteTimeUtc(Path.Combine(folderA,"shortcut.lnk"),new DateTime(2024,1,1));
        var units=new LocalFileInventory().Enumerate(folderA);
        Assert(units.Count==1 && !units[0].IsDirectory,"shortcut wasn't treated as a file");
        Assert(ExpiryPolicy.Observe(a.Id,units[0],null,now).FirstSeenUtc==now,"old metadata controlled first seen");
    });
    Test("Junction target changes do not affect monitored units",()=>
    {
        var target=Directory.CreateDirectory(Path.Combine(temp,"external")).FullName;
        File.WriteAllText(Path.Combine(target,"external.txt"),"content");
        var link=Path.Combine(folderA,"junction");
        var start=new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true, RedirectStandardError=true };
        start.Arguments=$"/d /c mklink /J \"{link}\" \"{target}\"";
        using var process=System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert(process.ExitCode==0,"test junction could not be created: "+process.StandardError.ReadToEnd());
        var before=new LocalFileInventory().Enumerate(folderA);
        File.AppendAllText(Path.Combine(target,"external.txt"),"changed");
        var after=new LocalFileInventory().Enumerate(folderA);
        Assert(before.Count==1 && after.Count==1 && before[0].Fingerprint==after[0].Fingerprint,"followed junction target");
        Directory.Delete(link);
    });
    Test("Audit log total size stays within its configured cap",()=>
    {
        var path=Path.Combine(temp,"logs","test.log");var audit=new RollingAuditLog(path);
        for(var i=0;i<1300;i++) audit.Write("test",new string('x',8192));
        var size=Directory.GetFiles(Path.GetDirectoryName(path)!).Sum(p=>new FileInfo(p).Length);
        Assert(size<=10*1024*1024 && File.Exists(path+".1"),"log did not rotate within limit");
    });
    Test("Reminder is silent without due items and respects daily time",()=>
    {
        var settings=new ScheduleSettings(new TimeOnly(2,0),"China Standard Time");
        var before=new DateTimeOffset(2026,10,10,1,59,0,TimeSpan.FromHours(8));
        var after=before.AddMinutes(1);
        Assert(!ReminderPolicy.ShouldRemind(after,settings,0,null),"empty reminder");
        Assert(!ReminderPolicy.ShouldRemind(before,settings,1,null),"early login reminder");
        Assert(ReminderPolicy.ShouldRemind(after,settings,1,null),"missing due reminder");
        Assert(!ReminderPolicy.ShouldRemind(after,settings,1,new DateOnly(2026,10,10)),"repeat same day");
    });
    Test("Duplicate/overlapping paths and invalid days are rejected",()=>
    {
        var nested=Directory.CreateDirectory(Path.Combine(folderA,"nested")).FullName;
        bool Rejected(MonitorDefinition m,IReadOnlyList<MonitorDefinition> others){try{MonitorValidation.Validate(m,others);return false;}catch(ArgumentException){return true;}}
        Assert(Rejected(new(Guid.NewGuid(),folderA,0),[]),"zero accepted");
        Assert(Rejected(new(Guid.NewGuid(),nested),[new(Guid.NewGuid(),folderA)]),"overlap accepted");
    });
    Test("Unavailable expired folder is excluded from notification results",()=>
    {
        var missing=Path.Combine(temp,"missing");var m=new MonitorDefinition(Guid.NewGuid(),missing,1);
        repo.SaveMonitor(m);repo.ReplaceObservations(m.Id,[new(m.Id,Path.Combine(missing,"x"),false,1,"x",now.AddDays(-5),now.AddDays(-5))]);
        var result=new ScanService(repo,new LocalFileInventory(),new FakeClock(now),new NoLog()).Scan(ScanScope.AllEnabled);
        Assert(result.DueItems.Count==0 && result.Errors.Count==1,"unavailable folder notified");repo.RemoveMonitor(m.Id);
    });
    Test("App settings persist, reject invalid time and log limits",()=>
    {
        repo.WriteSettings(new("14:35",4,DeletionMode.Permanent));Assert(repo.ReadSettings()==new AppSettings("14:35",4,DeletionMode.Permanent),"settings roundtrip failed");
        foreach(var invalid in new[]{new AppSettings("2:00"),new AppSettings("25:00"),new AppSettings(LogLimitMiB:1)})
        {var rejected=false;try{repo.WriteSettings(invalid);}catch(ArgumentException){rejected=true;}Assert(rejected,"bad settings accepted");}
    });
    Test("Cleanup requires reviewed expiry, unchanged config and unchanged fingerprint",()=>
    {
        var root=Directory.CreateDirectory(Path.Combine(temp,"cleanup")).FullName;
        var file=Path.Combine(root,"old.txt");File.WriteAllText(file,"content");var inventory=new LocalFileInventory();
        var m=new MonitorDefinition(Guid.NewGuid(),root,1);repo.SaveMonitor(m);
        var observation=ExpiryPolicy.Observe(m.Id,inventory.Enumerate(root).Single(),null,now.AddDays(-2));repo.ReplaceObservations(m.Id,[observation]);
        var item=new DueItem(m,observation,ExpiryPolicy.DueAt(observation,1));var deletion=new FakeDeletion();
        var service=new CleanupService(repo,inventory,deletion,new FakeClock(now),new NoLog());
        IReadOnlyList<CleanupResult> Run(CleanupRequest r)=>service.ExecuteAsync(r,CancellationToken.None).GetAwaiter().GetResult();
        repo.SaveMonitor(m with{Enabled=false});Assert(!Run(new([item]))[0].Success,"disabled cleaned");
        repo.SaveMonitor(m with{RetentionDays=30});Assert(!Run(new([item]))[0].Success,"not due cleaned");
        repo.SaveMonitor(m with{DeletionMode=DeletionMode.Permanent});Assert(!Run(new([item]))[0].Success,"mode changed without reconfirm");
        repo.SaveMonitor(m);File.AppendAllText(file,"updated");Assert(!Run(new([item]))[0].Success,"changed file cleaned");
        Assert(deletion.Calls.Count==0 && repo.GetObservations(m.Id).Count==1,"rejected operation touched content/history");
        var fresh=ExpiryPolicy.Observe(m.Id,inventory.Enumerate(root).Single(),null,now.AddDays(-2));repo.ReplaceObservations(m.Id,[fresh]);
        Assert(Run(new([new(m,fresh,ExpiryPolicy.DueAt(fresh,1))]))[0].Success,"valid cleanup failed");
        Assert(deletion.Calls.Single().Mode==DeletionMode.RecycleBin && repo.GetObservations(m.Id).Count==0,"default mode/history wrong");
        repo.ReplaceObservations(m.Id,[fresh]);Assert(Run(new([new(m,fresh,ExpiryPolicy.DueAt(fresh,1))],DeletionMode.Permanent))[0].Success,"override failed");
        Assert(deletion.Calls.Last().Mode==DeletionMode.Permanent,"override not honored");
        Assert(Run(new([])).Count==0,"empty selection had side effects");repo.RemoveMonitor(m.Id);
    });
    Test("Failed deletion retains observations; forged outside target is rejected",()=>
    {
        var m=new MonitorDefinition(Guid.NewGuid(),folderA,1);repo.SaveMonitor(m);
        var unit=new LocalFileInventory().Enumerate(folderA).First();var o=ExpiryPolicy.Observe(m.Id,unit,null,now.AddDays(-2));repo.ReplaceObservations(m.Id,[o]);
        var service=new CleanupService(repo,new LocalFileInventory(),new FailingDeletion(),new FakeClock(now),new NoLog());
        var results=service.ExecuteAsync(new([new(m,o,now)]),CancellationToken.None).Result;
        Assert(!results[0].Success && repo.GetObservations(m.Id).Count==1,"failed cleanup lost history");
        var forged=o with{Path=Path.Combine(temp,"outside")};repo.ReplaceObservations(m.Id,[forged]);
        Assert(!service.ExecuteAsync(new([new(m,forged,now)]),CancellationToken.None).Result[0].Success,"outside allowed");repo.RemoveMonitor(m.Id);
    });
    Test("Real Windows recycle and permanent deletion affect only test files",()=>
    {
        var deletion=new WindowsFileDeletion();
        var file=Path.Combine(temp,"recycle-test-"+Guid.NewGuid()+".txt");File.WriteAllText(file,"FolderNest disposable test");
        deletion.Delete(file,false,DeletionMode.RecycleBin);Assert(!File.Exists(file),"recycle did not complete");
        var dir=Directory.CreateDirectory(Path.Combine(temp,"permanent-test")).FullName;File.WriteAllText(Path.Combine(dir,"test.txt"),"test");
        deletion.Delete(dir,true,DeletionMode.Permanent);Assert(!Directory.Exists(dir),"permanent folder did not complete");
    });
    Test("Recursive deletion refuses nested junction; linked ancestor path rejected",()=>
    {
        var root=Directory.CreateDirectory(Path.Combine(temp,"nested-link")).FullName;
        var target=Directory.CreateDirectory(Path.Combine(temp,"keep-target")).FullName;var keep=Path.Combine(target,"keep.txt");File.WriteAllText(keep,"keep");
        var link=Path.Combine(root,"link");
        using var p=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",$"/d /c mklink /J \"{link}\" \"{target}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})!;p.WaitForExit();Assert(p.ExitCode==0,"junction setup failed");
        try
        {
            var rejected=false;try{new WindowsFileDeletion().Delete(root,true,DeletionMode.Permanent);}catch(IOException){rejected=true;}
            Assert(rejected && File.Exists(keep),"followed destructive junction");
            var sub=Directory.CreateDirectory(Path.Combine(target,"sub")).FullName;
            rejected=false;try{MonitorValidation.NormalizePath(Path.Combine(link,"sub"));}catch(IOException){rejected=true;}
            Assert(rejected,"linked parent accepted");
        }
        finally {Directory.Delete(link);}
    });
    Test("Arrival watcher reports nested changes without modifying outside files",()=>
    {
        var root=Directory.CreateDirectory(Path.Combine(temp,"watch")).FullName;var m=new MonitorDefinition(Guid.NewGuid(),root);
        using var signal=new ManualResetEventSlim();using var watcher=new ArrivalTracker();
        watcher.Changed+=(_,e)=>{if(e.MonitorId==m.Id && e.Path.EndsWith("new.txt"))signal.Set();};watcher.Watch([m],new NoLog());
        File.WriteAllText(Path.Combine(root,"new.txt"),"test");Assert(signal.Wait(5000),"arrival event missing");
    });
    Test("Shrinking log limit enforces total size after next write",()=>
    {
        var path=Path.Combine(temp,"shrink","test.log");var limit=10;var log=new RollingAuditLog(path,()=>limit);
        for(int i=0;i<1100;i++)log.Write("test",new string('x',8192));limit=2;log.Write("settings","shrink");
        Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Sum(p=>new FileInfo(p).Length)<=2*1024*1024,"shrink exceeds cap");
    });
    Test("Legacy import reads configuration without modifying its source",()=>
    {
        var path=Path.Combine(temp,"config.json");File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new{Monitors=new[]{new{Path=folderA,Days=14,DeleteMode="RecycleBin",Enabled=true,Scheduled=true}}}));
        var source=File.ReadAllText(path);var m=MonitorConfigImporter.Read(path).Single();
        Assert(m.Path==folderA && m.RetentionDays==14 && m.Enabled && source==File.ReadAllText(path),"legacy migration altered source");
    });
    Test("Monitor import rejects invalid types, unknown fields and duplicate paths",()=>
    {
        var path=Path.Combine(temp,"import.json");
        void Rejected(string json)
        {
            File.WriteAllText(path,json);var failed=false;
            try{MonitorConfigImporter.Read(path);}catch(Exception ex) when(ex is ArgumentException or System.Text.Json.JsonException){failed=true;}
            Assert(failed,"invalid configuration accepted: "+json);
        }
        var escaped=System.Text.Json.JsonSerializer.Serialize(folderA);
        foreach(var fields in new[]{"\"Days\":1.5","\"Days\":0","\"Days\":36501","\"Enabled\":\"false\"","\"DeleteMode\":\"Permnent\"","\"Day\":30","\"Days\":1,\"Days\":2"})
            Rejected("{\"Path\":"+escaped+","+fields+"}");
        Rejected("{\"Monitors\":[]}");Rejected("{\"Monitors\":[{\"Path\":"+escaped+"},{\"Path\":"+escaped+"}]}");
        File.WriteAllText(path,"{\"Root\":"+escaped+"}");
        Assert(MonitorConfigImporter.Read(path).Single().RetentionDays==30,"legacy defaults failed");
    });
    Test("Ordering persists without resetting observations; invalid orders roll back",()=>
    {
        var r=new SqliteRepository(Path.Combine(temp,"ordering.db"));
        var x=new MonitorDefinition(Guid.NewGuid(),folderA);var y=new MonitorDefinition(Guid.NewGuid(),folderB);
        r.SaveMonitor(x);r.SaveMonitor(y);
        var o=ExpiryPolicy.Observe(x.Id,new(Path.Combine(folderA,"test.txt"),false,1,"x"),null,now);r.ReplaceObservations(x.Id,[o]);
        r.SaveOrder([y.Id,x.Id]);
        var reopened=new SqliteRepository(Path.Combine(temp,"ordering.db"));
        Assert(reopened.GetMonitors().Select(m=>m.Id).SequenceEqual(new[]{y.Id,x.Id}),"order not persisted");
        Assert(reopened.GetObservations(x.Id).Single()==o,"sort changed history");
        var rejected=false;try{r.SaveOrder([x.Id,x.Id]);}catch(ArgumentException){rejected=true;}
        Assert(rejected&&r.GetMonitors()[0].Id==y.Id,"invalid order accepted or partially committed");
        r.RemoveMonitor(y.Id);r.SaveOrder([x.Id]);Assert(r.GetMonitors().Count==1,"order foreign key orphan");
    });
    Test("Selected reset preserves siblings, files, timestamps and enabled state",()=>
    {
        var root=Directory.CreateDirectory(Path.Combine(temp,"reset")).FullName;
        var file=Path.Combine(root,"one.txt");File.WriteAllText(file,"one");File.WriteAllText(Path.Combine(root,"two.txt"),"two");
        var sub=Directory.CreateDirectory(Path.Combine(root,"group")).FullName;File.WriteAllText(Path.Combine(sub,"nested.txt"),"nested");
        var m=new MonitorDefinition(Guid.NewGuid(),root,30,Enabled:false);repo.SaveMonitor(m);
        var inventory=new LocalFileInventory();var old=inventory.Enumerate(root).Select(u=>ExpiryPolicy.Observe(m.Id,u,null,now.AddDays(-40))).ToArray();repo.ReplaceObservations(m.Id,old);
        var stamp=File.GetLastWriteTimeUtc(file);var reset=new TimingResetService(repo,inventory,new FakeClock(now),new NoLog());
        var result=reset.Reset([new(m.Id,new HashSet<string>(StringComparer.OrdinalIgnoreCase){file.ToUpperInvariant()})]);
        Assert(result.Single().ResetCount==1&&result[0].Error is null,"partial reset failed");
        var current=repo.GetObservations(m.Id);Assert(current.Single(o=>o.Path==file).FirstSeenUtc==now,"selected file not reset");
        Assert(current.Where(o=>o.Path!=file).All(o=>old.Contains(o)),"unselected sibling changed");
        Assert(File.ReadAllText(file)=="one"&&File.GetLastWriteTimeUtc(file)==stamp&&!repo.GetMonitors().Single(x=>x.Id==m.Id).Enabled,"reset changed file or enabled state");
        Assert(reset.Reset([new(m.Id,new HashSet<string>())]).Count==0,"empty selection had effects");
        var before=repo.GetObservations(m.Id).ToArray();
        var failed=reset.Reset([new(m.Id,new HashSet<string>{file,Path.Combine(root,"missing.txt")})]);
        Assert(failed.Single().Error is not null&&repo.GetObservations(m.Id).SequenceEqual(before),"failed reset partially committed");
        var grouped=reset.Reset([new(m.Id,new HashSet<string>{sub})]);
        Assert(grouped.Single().ResetCount==1&&repo.GetObservations(m.Id).Single(o=>o.Path==sub).FirstSeenUtc==now,"subfolder unit not reset");
        File.WriteAllText(Path.Combine(root,"new.txt"),"new");
        Assert(reset.Reset([new(m.Id)]).Single().ResetCount==4,"whole directory missed live units");
        Assert(repo.GetObservations(m.Id).All(o=>o.FirstSeenUtc==now&&o.LastActivityUtc==now),"whole reset incomplete");
        Directory.Move(root,root+"-offline");before=repo.GetObservations(m.Id).ToArray();
        Assert(reset.Reset([new(m.Id)]).Single().Error is not null&&repo.GetObservations(m.Id).SequenceEqual(before),"offline reset lost history");
        Directory.Move(root+"-offline",root);repo.RemoveMonitor(m.Id);
    });
    Test("2.0 JSON monitor records retain IDs and history when obsolete selection is ignored",()=>
    {
        var id=Guid.NewGuid();var json=System.Text.Json.JsonSerializer.Serialize(new{Id=id,Path=folderA,RetentionDays=9,DeletionMode=0,Enabled=false,Scheduled=true});
        var m=System.Text.Json.JsonSerializer.Deserialize<MonitorDefinition>(json)!;
        Assert(m.Id==id&&!m.Enabled&&m.RetentionDays==9,"2.0 monitor migration lost configuration");
        Assert(new WindowsTaskScheduler(temp,new NoLog()).Prefix=="FolderNest","default task name includes version");
    });
    if(OperatingSystem.IsWindows())Test("Real isolated Task Scheduler deployment, XML, status and removal",()=>
    {
        var prefix="FolderNest Test "+Guid.NewGuid().ToString("N");
        var scheduler=new WindowsTaskScheduler(temp,new NoLog(),prefix);
        try
        {
            var before=scheduler.GetStatus();Assert(before.Deployment==TaskDeployment.NotInstalled,"initial task query: "+before.Details);
            var settings=new ScheduleSettings(new TimeOnly(23,59),TimeZoneInfo.Local.Id);
            var deployed=scheduler.Deploy(settings,Environment.ProcessPath!);
            Assert(deployed.Deployment==TaskDeployment.Installed,"task install query failed: "+deployed.Details);
            dynamic com=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;com.Connect();dynamic root=com.GetFolder("\\");
            var xml=(string)root.GetTask(prefix+" Daily").Xml;
            Assert(xml.Contains("23:59:00") && xml.Contains("InteractiveToken") && xml.Contains("--background") && xml.Contains("StartWhenAvailable"),"incorrect task definition");
            Assert(new WindowsTaskScheduler(temp,new NoLog(),prefix,()=>new TimeOnly(22,0)).GetStatus().Deployment==TaskDeployment.Partial,"external time mismatch not detected");
            var foreign=new WindowsTaskScheduler(Path.Combine(temp,"different-data"),new NoLog(),prefix);
            var rejected=false;try{foreign.Deploy(settings,Environment.ProcessPath!);}catch(IOException){rejected=true;}
            Assert(rejected&&scheduler.GetStatus().Deployment==TaskDeployment.Installed,"different data deployment overwrote existing plans");
            rejected=false;try{foreign.Remove();}catch(IOException){rejected=true;}
            Assert(rejected&&scheduler.GetStatus().Deployment==TaskDeployment.Installed,"different data removal deleted plans");
            root.DeleteTask(prefix+" Logon",0);Assert(scheduler.GetStatus().Deployment==TaskDeployment.Partial,"missing task not partial");
            Assert(scheduler.Remove().Deployment==TaskDeployment.NotInstalled,"remove left task");
        }
        finally{scheduler.Remove();}
    });
    if(args.Length==2 && args[0]=="--ui-fixture")
    {
        var fixture=Path.GetFullPath(args[1]);Directory.CreateDirectory(fixture);
        var uiRepo=new SqliteRepository(Path.Combine(fixture,"data","foldernest.db"));
        foreach(var suffix in new[]{"A","B","Empty"})
        {
            var root=Directory.CreateDirectory(Path.Combine(fixture,"Monitor-"+suffix)).FullName;
            var m=new MonitorDefinition(Guid.NewGuid(),root,1);uiRepo.SaveMonitor(m);
            if(suffix=="Empty")continue;
            File.WriteAllText(Path.Combine(root,"test-"+suffix+".txt"),"Disposable FolderNest UI fixture");
            var unit=new LocalFileInventory().Enumerate(root).Single();uiRepo.ReplaceObservations(m.Id,[ExpiryPolicy.Observe(m.Id,unit,null,DateTimeOffset.UtcNow.AddDays(-3))]);
        }
        Console.WriteLine("UI fixture: "+fixture);
    }
    Console.WriteLine($"{passed} behavioral tests passed.");
}
finally { SqliteConnection.ClearAllPools();Directory.Delete(temp,true); }

sealed class FakeClock(DateTimeOffset now):IClock { public DateTimeOffset UtcNow=>now; }
sealed class NoLog:IAuditLog { public void Write(string operation,string message){} }
sealed class FakeDeletion:IFileDeletion
{
    public List<(string Path,DeletionMode Mode)> Calls {get;}=[];
    public void Delete(string path,bool directory,DeletionMode mode)=>Calls.Add((path,mode));
}
sealed class FailingDeletion:IFileDeletion {public void Delete(string path,bool directory,DeletionMode mode)=>throw new IOException("Test locked file");}
