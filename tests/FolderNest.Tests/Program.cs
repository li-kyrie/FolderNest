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
    Console.WriteLine($"{passed} behavioral tests passed.");
}
finally { SqliteConnection.ClearAllPools();Directory.Delete(temp,true); }

sealed class FakeClock(DateTimeOffset now):IClock { public DateTimeOffset UtcNow=>now; }
sealed class NoLog:IAuditLog { public void Write(string operation,string message){} }
