using AutoExtract.Core;
using System.IO.Compression;

if (args.Length >= 2 && args[0] == "--diagnose-extract")
{
    var workspace = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "diagnostics", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(workspace, "input"); var output = Path.Combine(workspace, "payload");
    Directory.CreateDirectory(input); Directory.CreateDirectory(output);
    var snapshot = Path.Combine(input, Path.GetFileName(args[1]));
    Console.WriteLine("Copying diagnostic snapshot; original is read-only. Workspace: " + workspace);
    File.Copy(args[1], snapshot);
    using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var engine = new BandizipEngine(() => "");
    try
    {
        await engine.TestAsync(snapshot, workspace, cancel.Token); Console.WriteLine("PASS diagnostic integrity");
        await engine.ExtractAsync(snapshot, output, workspace, cancel.Token); Console.WriteLine("PASS diagnostic extraction"); return 0;
    }
    catch (Exception e) { Console.WriteLine(e.Message); return 1; }
}
if (args.Contains("--sandbox-probe"))
{
    File.WriteAllText(args[1], "inside");
    try { File.WriteAllText(args[2], "ESCAPED"); return 91; } catch (UnauthorizedAccessException) { return 0; }
}
var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0; var failures = new List<string>();
async Task Test(string name, Func<string, Task> run)
{
    var dir = Path.Combine(root, (passed + failures.Count).ToString()); Directory.CreateDirectory(dir);
    try { await run(dir); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception e) { failures.Add(name); Console.WriteLine("FAIL " + name + " : " + e); }
}
void Assert(bool ok, string why = "assertion failed") { if (!ok) throw new Exception(why); }
string Zip(string dir, string name = "일반 압축.zip", string text = "hello 한국어")
{
    var file = Path.Combine(dir, name);
    using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
    using var writer = new StreamWriter(archive.CreateEntry("문서 & 파일.txt").Open()); writer.Write(text); return file;
}
(ArchiveExtractionService service, FakeEngine engine, FakeRecycle recycle, SettingsService storage) Setup(string dir)
{
    var engine = new FakeEngine(); var recycle = new FakeRecycle(); var storage = new SettingsService(Path.Combine(dir, "config"));
    return (new ArchiveExtractionService(engine, recycle, storage), engine, recycle, storage);
}
await Test("일반 ZIP / 한글 / 특수문자 / 원본 유지", async d => { var (s,e,_,_) = Setup(d); var p = Zip(d); await s.ProcessAsync(p, false, default); Assert(s.State.History[0].Status == "성공", s.State.History[0].Detail); Assert(File.Exists(p) && File.Exists(Path.Combine(d, "일반 압축", "문서 & 파일.txt"))); Assert(e.Extracts == 1); });
await Test("손상 ZIP 원본 보존", async d => { var(s,e,_,_) = Setup(d); var p = Path.Combine(d,"bad.zip"); File.WriteAllText(p,"bad"); await s.ProcessAsync(p,true,default); Assert(s.State.History[0].Status=="실패" && File.Exists(p) && e.Extracts==0); });
await Test("다운로드 writer 잠금 / 변경 후 처리", async d => { var(s,e,_,_) = Setup(d); var p=Zip(d); using(var writer=new FileStream(p,FileMode.Open,FileAccess.Write,FileShare.Read)) { await s.ProcessAsync(p,false,default); Assert(e.Tests==0); } await s.ProcessAsync(p,false,default); Assert(e.Tests==1); });
await Test("검사 실패 동일 내용 억제 / 내용 변경 재시도", async d => { var(s,e,_,_) = Setup(d); var p=Zip(d); e.FailTest=true; await s.ProcessAsync(p,false,default); await s.ProcessAsync(p,false,default); Assert(e.Tests==1); File.Delete(p); Zip(d,text:"changed"); e.FailTest=false; await s.ProcessAsync(p,false,default); Assert(e.Tests==2 && e.Extracts==1); });
await Test("동일 파일 동시 요청 중복 방지", async d => { var(s,e,_,_) = Setup(d); e.Delay=150; var p=Zip(d); await Task.WhenAll(Enumerable.Range(0,12).Select(_=>s.ProcessAsync(p,false,default))); Assert(e.Tests==1 && e.Extracts==1); });
await Test("기존 폴더와 파일명 충돌", async d => { var(s,_,_,_) = Setup(d); var p=Zip(d); Directory.CreateDirectory(Path.Combine(d,"일반 압축")); File.WriteAllText(Path.Combine(d,"일반 압축 (2)"),"existing"); await s.ProcessAsync(p,false,default); Assert(Directory.Exists(Path.Combine(d,"일반 압축 (3)"))); });
await Test("추출 실패 원본 유지", async d => { var(s,e,_,_) = Setup(d); var p=Zip(d); e.FailExtract=true; await s.ProcessAsync(p,true,default); Assert(File.Exists(p) && s.State.History[0].Status=="실패"); });
await Test("휴지통 실패 결과 보존 / 원본 경로 복구", async d => { var(s,_,r,_) = Setup(d); r.Fail=true; var p=Zip(d); await s.ProcessAsync(p,true,default); Assert(File.Exists(p) && Directory.Exists(Path.Combine(d,"일반 압축"))); Assert(s.State.History[0].Status=="성공 · 경고",s.State.History[0].Detail); });
await Test("여러 감시 폴더 / 하위 폴더", async d => { var(s,e,_,_) = Setup(d); var a=Path.Combine(d,"a"); var b=Path.Combine(d,"b","sub"); Directory.CreateDirectory(a); Directory.CreateDirectory(b); Zip(a); Zip(b); await using var w=new FolderWatcherService(s); await w.StartAsync(new AppSettings{RecycleOriginal=false,Folders=[new(){Path=a},new(){Path=Path.Combine(d,"b"),IncludeSubdirectories=true}]}); for(int i=0;i<100 && e.Extracts<2;i++) await Task.Delay(50); await w.StopAsync(); Assert(e.Extracts==2); });
await Test("설정 재시작 복원", d => { var store=new SettingsService(d); store.Save("settings.json",new AppSettings{RecycleOriginal=false,AutoStart=true,Paused=true,Folders=[new(){Path=d,Enabled=false,IncludeSubdirectories=true}],BandizipPath="test"}); var s=new SettingsService(d).Load<AppSettings>("settings.json"); Assert(s.Paused && s.AutoStart && !s.RecycleOriginal && s.Folders[0].IncludeSubdirectories && !s.Folders[0].Enabled && s.BandizipPath=="test"); return Task.CompletedTask; });
await Test("성공 기록 재시작 중복 억제", async d => { var(s,e,r,store)=Setup(d); var p=Zip(d); await s.ProcessAsync(p,false,default); var restarted=new ArchiveExtractionService(e,r,store); await restarted.ProcessAsync(p,false,default); Assert(e.Extracts==1); });
await Test("결과 폴더 삭제 / 동일 압축 재다운로드 / 기록 삭제 후 재시작", async d =>
{
    var(s,e,r,store)=Setup(d); var p=Zip(d);
    await s.ProcessAsync(p,false,default);
    var output=s.State.History[0].Output!;
    var bytes=File.ReadAllBytes(p);
    Directory.Delete(output,true); File.Delete(p); File.WriteAllBytes(p,bytes);
    await s.ClearHistoryAsync();
    var restarted=new ArchiveExtractionService(e,r,store);
    await restarted.ProcessAsync(p,false,default);
    Assert(e.Extracts==2 && File.Exists(Path.Combine(output,"문서 & 파일.txt")));
    await restarted.ProcessAsync(p,false,default); Assert(e.Extracts==2);
});
await Test("이전 버전 성공 기록의 출력 경로 복원", async d =>
{
    var(s,e,r,store)=Setup(d); var p=Zip(d);
    Directory.CreateDirectory(Path.Combine(d,"일반 압축"));
    await s.ProcessAsync(p,false,default);
    var output=s.State.History[0].Output!;
    s.State.CompletedOutputs.Clear(); store.Save("state.json",s.State);
    var restarted=new ArchiveExtractionService(e,r,store);
    await restarted.ProcessAsync(p,false,default); Assert(e.Extracts==1);
    Directory.Delete(output,true);
    await restarted.ProcessAsync(p,false,default); Assert(e.Extracts==2);
});
await Test("임시 다운로드 파일 대기", async d => { var(s,e,_,_) = Setup(d); var p=Zip(d); File.WriteAllText(p+".crdownload",""); await s.ProcessAsync(p,false,default); Assert(e.Tests==0); File.Delete(p+".crdownload"); await s.ProcessAsync(p,false,default); Assert(e.Tests==1); });
await Test("분할 최초 볼륨 / 정확한 family", d => { var detector=new ArchiveDetectionService(); Assert(detector.IsArchive("foo.part01.rar") && !detector.IsArchive("foo.part02.rar") && detector.IsArchive("foo.7z.001") && !detector.IsArchive("foo.z01")); foreach(var n in new[]{"foo.part01.rar","foo.part02.rar","foo2.part02.rar"}) File.WriteAllText(Path.Combine(d,n),""); Assert(detector.Family(Path.Combine(d,"foo.part01.rar")).Length==2); return Task.CompletedTask; });
await Test("ZIP 경로 순회 차단", async d => { var(s,e,_,_) = Setup(d); var p=Path.Combine(d,"evil.zip"); using(var zip=ZipFile.Open(p,ZipArchiveMode.Create)) { using var w=new StreamWriter(zip.CreateEntry("../outside.txt").Open()); w.Write("bad"); } await s.ProcessAsync(p,true,default); Assert(File.Exists(p) && e.Extracts==0 && s.State.History[0].Status=="실패"); });
await Test("처리 도중 원본 변경 / 교체 차단", async d => { var(s,e,_,_) = Setup(d); var p=Zip(d); bool denied=false; e.OnTest=()=> { try { File.WriteAllText(p,"corrupt"); } catch(IOException) { denied=true; } }; await s.ProcessAsync(p,false,default); Assert(denied && s.State.History[0].Status=="성공"); });
await Test("처리 취소 원본 보존", async d => { var(s,e,_,_) = Setup(d); var p=Zip(d); e.Delay=1000; using var c=new CancellationTokenSource(100); await s.ProcessAsync(p,true,c.Token); Assert(File.Exists(p) && !Directory.Exists(Path.Combine(d,"일반 압축"))); });
await Test("안전한 핸들 이동 / 성공 시 원본 처리", async d => { var(s,_,r,_) = Setup(d); var p=Zip(d); await s.ProcessAsync(p,true,default); Assert(r.Calls==1 && !File.Exists(p) && s.State.History[0].Status=="성공",s.State.History[0].Detail); });
await Test("설정 손상 시 안전하게 중단", d => { File.WriteAllText(Path.Combine(d,"settings.json"),"{"); try { new SettingsService(d).Load<AppSettings>("settings.json"); throw new Exception("must reject"); } catch(InvalidDataException){} return Task.CompletedTask; });
await Test("제한 프로세스 외부 쓰기 거부", async d => { var stage=Path.Combine(d,"work"); Directory.CreateDirectory(stage); var outside=Path.Combine(d,"outside.txt"); File.WriteAllText(outside,"original"); var inside=Path.Combine(stage,"inside.txt"); var script=Path.Combine(stage,"probe.cmd"); File.WriteAllText(script,$"@echo off\r\necho inside>\"{inside}\"\r\necho ESCAPED>\"{outside}\"\r\n"); var code=await RestrictedProcess.RunAsync(Path.Combine(Environment.SystemDirectory,"cmd.exe"),["/d","/c",script],stage,default); Assert(File.Exists(inside) && File.ReadAllText(inside).Trim()=="inside" && File.ReadAllText(outside)=="original", "sandbox exit="+code); });
await Test("실제 Bandizip 무결성 검사 및 ZIP 추출", async d => { var exe=BandizipEngine.FindExecutable() ?? throw new Exception("bz.exe unavailable"); var store=new SettingsService(Path.Combine(d,"config")); var s=new ArchiveExtractionService(new BandizipEngine(()=>exe),new FakeRecycle(),store); var p=Zip(d); await s.ProcessAsync(p,false,default); Assert(s.State.History[0].Status=="성공",s.State.History[0].Detail); Assert(File.ReadAllText(Path.Combine(d,"일반 압축","문서 & 파일.txt"))=="hello 한국어"); });
await Test("분할 형식 간 family 혼합 방지", d => { var detector=new ArchiveDetectionService(); foreach(var n in new[]{"foo.part1.rar","foo.part2.rar","foo.7z.001","foo.7z.002","foo.zip","foo.z01","foo.vol1.egg","foo.vol2.egg","foo.alz","foo.a00"}) File.WriteAllText(Path.Combine(d,n),""); foreach(var n in new[]{"foo.part1.rar","foo.7z.001","foo.zip","foo.vol1.egg","foo.alz"}) Assert(detector.Family(Path.Combine(d,n)).Length==2,n); Assert(!detector.IsArchive("foo.vol2.egg") && !detector.IsArchive("foo.a00")); return Task.CompletedTask; });
await Test("휴지통 작업 중단 원본 복구 / 충돌 보존", d => { var(s,_,_,_)=Setup(d); var originals=Path.Combine(d,".AutoExtract-Working",Guid.NewGuid().ToString("N"),"originals"); Directory.CreateDirectory(originals); File.WriteAllText(Path.Combine(originals,"recover.zip"),"original"); File.WriteAllText(Path.Combine(originals,"conflict.zip"),"old"); File.WriteAllText(Path.Combine(d,"conflict.zip"),"new"); s.RecoverOriginals(d); Assert(File.ReadAllText(Path.Combine(d,"recover.zip"))=="original"); Assert(File.ReadAllText(Path.Combine(d,"conflict.zip"))=="new" && File.Exists(Path.Combine(originals,"conflict.zip"))); return Task.CompletedTask; });
async Task CreateBz(string[] arguments)
{
    var info=new System.Diagnostics.ProcessStartInfo(BandizipEngine.FindExecutable()!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(var a in arguments) info.ArgumentList.Add(a);
    using var p=System.Diagnostics.Process.Start(info)!;
    var stdout=p.StandardOutput.ReadToEndAsync(); var stderr=p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync(); await Task.WhenAll(stdout,stderr); if(p.ExitCode!=0) throw new Exception("fixture bz exit="+p.ExitCode+" "+await stderr);
}
await Test("실제 암호 ZIP 자동 실패 / 원본 보존", async d => { var source=Path.Combine(d,"source.txt"); File.WriteAllText(source,"secret"); var archive=Path.Combine(d,"encrypted.zip"); await CreateBz(["c","-y","-p:secret",archive,source]); var s=new ArchiveExtractionService(new BandizipEngine(()=>""),new FakeRecycle(),new SettingsService(Path.Combine(d,"config"))); using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(20)); await s.ProcessAsync(archive,true,cancel.Token); Assert(s.State.History[0].Status=="실패" && File.Exists(archive),s.State.History[0].Detail); });
await Test("암호 DPAPI 저장 / 재시작 복원 / 손상 거부", d =>
{
    const string secret = "한글 암호 \"quoted\" & trailing\\";
    var store = new SettingsService(d);
    store.Save("settings.json", new AppSettings { Passwords = [new() { Name = "테스트", ProtectedValue = PasswordProtection.Protect(secret) }] });
    var json = File.ReadAllText(Path.Combine(d, "settings.json"));
    Assert(!json.Contains(secret) && !json.Contains("quoted"));
    var restored = store.Load<AppSettings>("settings.json");
    Assert(PasswordProtection.Unprotect(restored.Passwords[0].ProtectedValue) == secret);
    var refused = false;
    try { PasswordProtection.Unprotect(Convert.ToBase64String(new byte[24])); }
    catch (System.Security.Cryptography.CryptographicException) { refused = true; }
    Assert(refused);
    return Task.CompletedTask;
});
await Test("실제 암호 ZIP / 틀린 암호 후 저장 암호 / 재시도", async d =>
{
    const string password = "한글 \"quoted\" & trailing\\";
    var source = Path.Combine(d, "source.txt"); File.WriteAllText(source, "encrypted content");
    var archive = Path.Combine(d, "saved.zip");
    await CreateBz(["c", "-y", "-p:" + password, archive, source]);
    string[] passwords = ["wrong"];
    var recycle = new FakeRecycle();
    var service = new ArchiveExtractionService(new BandizipEngine(() => "", () => passwords), recycle, new SettingsService(Path.Combine(d, "config")));
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    await service.ProcessAsync(archive, true, cancel.Token);
    Assert(service.State.History[0].Status == "실패" && File.Exists(archive) && recycle.Calls == 0);
    passwords = ["wrong", password]; service.ResetFailures();
    await service.ProcessAsync(archive, false, cancel.Token);
    Assert(service.State.History[0].Status == "성공", service.State.History[0].Detail);
    Assert(File.Exists(archive) && File.ReadAllText(Path.Combine(d, "saved", "source.txt")) == "encrypted content");
    Assert(!File.ReadAllText(Path.Combine(d, "config", "state.json")).Contains("quoted"));
    foreach (var log in Directory.GetFiles(d, "*.log", SearchOption.AllDirectories)) Assert(!File.ReadAllText(log).Contains(password), "암호가 로그에 노출됨");
});
await Test("실제 헤더 암호화 7z / 목록과 추출 암호 전달", async d =>
{
    var source = Path.Combine(d, "source.txt"); File.WriteAllText(source, "hidden name content");
    var archive = Path.Combine(d, "hidden.7z");
    // Bandizip encrypts 7z filenames automatically when a password is supplied.
    await CreateBz(["c", "-y", "-fmt:7z", "-p:header-secret", archive, source]);
    var service = new ArchiveExtractionService(new BandizipEngine(() => "", () => new[] { "wrong", "header-secret" }), new FakeRecycle(), new SettingsService(Path.Combine(d, "config")));
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    await service.ProcessAsync(archive, false, cancel.Token);
    Assert(service.State.History[0].Status == "성공", service.State.History[0].Detail);
    Assert(File.ReadAllText(Path.Combine(d, "hidden", "source.txt")) == "hidden name content");
});
await Test("실제 분할 7z 추출 / 모든 볼륨 처리", async d => { var source=Path.Combine(d,"source.bin"); var data=new byte[128*1024]; System.Security.Cryptography.RandomNumberGenerator.Fill(data); File.WriteAllBytes(source,data); await CreateBz(["c","-y","-fmt:7z","-v:32KB",Path.Combine(d,"split.7z"),source]); var first=Path.Combine(d,"split.7z.001"); var r=new FakeRecycle(); var s=new ArchiveExtractionService(new BandizipEngine(()=>""),r,new SettingsService(Path.Combine(d,"config"))); await s.ProcessAsync(first,true,default); Assert(s.State.History[0].Status=="성공",s.State.History[0].Detail); Assert(r.Calls>=2 && Directory.GetFiles(d,"split.7z.*").Length==0); Assert(File.ReadAllBytes(Path.Combine(d,"split","source.bin")).SequenceEqual(data)); });
await Test("실제 TAR 추출", async d => { var source=Path.Combine(d,"source.txt"); File.WriteAllText(source,"tar content"); var archive=Path.Combine(d,"normal.tar"); await CreateBz(["c","-y","-fmt:tar",archive,source]); var s=new ArchiveExtractionService(new BandizipEngine(()=>""),new FakeRecycle(),new SettingsService(Path.Combine(d,"config"))); await s.ProcessAsync(archive,false,default); Assert(s.State.History[0].Status=="성공",s.State.History[0].Detail); });
await Test("작업 입력 복사본 변조 시 완료 거부", async d => { var(s,e,_,r)=Setup(d); var p=Zip(d); e.OnExtract=snapshot=>File.WriteAllText(snapshot,"changed snapshot"); await s.ProcessAsync(p,true,default); Assert(s.State.History[0].Status=="실패" && File.Exists(p) && !Directory.Exists(Path.Combine(d,"일반 압축")),s.State.History[0].Detail); });
await Test("실제 ZIP의 명시적 디렉터리 없는 다단계 경로 추출", async d =>
{
    var archive=Path.Combine(d,"nested.zip");
    using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)) { using var w=new StreamWriter(zip.CreateEntry("상위 폴더/하위 폴더/파일.txt").Open()); w.Write("nested content"); }
    var s=new ArchiveExtractionService(new BandizipEngine(()=>""),new FakeRecycle(),new SettingsService(Path.Combine(d,"config")));
    await s.ProcessAsync(archive,false,default);
    Assert(s.State.History[0].Status=="성공",s.State.History[0].Detail);
    Assert(File.ReadAllText(Path.Combine(d,"nested","상위 폴더","하위 폴더","파일.txt"))=="nested content");
});
foreach (var format in new[] { "zip", "7z", "tar" })
await Test($"실제 {format} 빈 폴더 및 0바이트 파일 추출", async d =>
{
    var source = Path.Combine(d, "source");
    foreach (var name in new[] { "crashReport/attachments", "crashReport/reports", "node_modules/@electron", "빈 폴더/하위 폴더" })
        Directory.CreateDirectory(Path.Combine(source, name));
    File.WriteAllText(Path.Combine(source, "empty.txt"), "");
    File.WriteAllText(Path.Combine(source, "normal.txt"), "content");
    var archive = Path.Combine(d, "empty-directories." + format);
    await CreateBz(["c", "-y", "-fmt:" + format, archive, source]);
    var service = new ArchiveExtractionService(new BandizipEngine(() => ""), new FakeRecycle(), new SettingsService(Path.Combine(d, "config")));
    await service.ProcessAsync(archive, false, default);
    Assert(service.State.History[0].Status == "성공", service.State.History[0].Detail);
    var output = Path.Combine(d, "empty-directories", "source");
    foreach (var name in new[] { "crashReport/attachments", "crashReport/reports", "node_modules/@electron", "빈 폴더/하위 폴더" })
        Assert(Directory.Exists(Path.Combine(output, name)), name);
    Assert(File.Exists(Path.Combine(output, "empty.txt")) && new FileInfo(Path.Combine(output, "empty.txt")).Length == 0);
    Assert(File.ReadAllText(Path.Combine(output, "normal.txt")) == "content" && File.Exists(archive));
});
await Test("실제 TAR 경로 순회가 추출 대상 밖에 파일을 생성하지 않음", async d =>
{
    var archive=Path.Combine(d,"traversal.tar");
    using(var f=File.Create(archive)) using(var tar=new System.Formats.Tar.TarWriter(f))
    { using var content=new MemoryStream(System.Text.Encoding.UTF8.GetBytes("unsafe")); tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile,"../outside.txt"){DataStream=content}); }
    var s=new ArchiveExtractionService(new BandizipEngine(()=>""),new FakeRecycle(),new SettingsService(Path.Combine(d,"config")));
    await s.ProcessAsync(archive,false,default);
    Assert(File.Exists(archive) && !File.Exists(Path.Combine(d,"outside.txt")),s.State.History[0].Detail);
    var workRoot=Path.Combine(d,".AutoExtract-Working");
    if(Directory.Exists(workRoot)) foreach(var work in Directory.EnumerateDirectories(workRoot)) Assert(!File.Exists(Path.Combine(work,"outside.txt")));
});
await Test("제한 프로세스는 payload 쓰기만 허용하고 입력 변경 거부", async d =>
{
    var workspace=Path.Combine(d,"work"); var payload=Path.Combine(workspace,"payload"); Directory.CreateDirectory(payload);
    var original=Path.Combine(workspace,"input.txt"); File.WriteAllText(original,"original");
    var result=Path.Combine(payload,"result.txt"); var script=Path.Combine(workspace,"probe.cmd");
    File.WriteAllText(script,$"@echo off\r\necho result>\"{result}\"\r\necho tampered>\"{original}\"\r\n");
    await RestrictedProcess.RunAsync(Path.Combine(Environment.SystemDirectory,"cmd.exe"),["/d","/c",script],workspace,default,writableDirectory:payload);
    Assert(File.ReadAllText(original)=="original" && File.ReadAllText(result).Trim()=="result");
});
await Test("정상 완료 후 원본 유지 모드에서도 임시 루트 전체 정리", async d =>
{
    var(s,_,_,_)=Setup(d); var p=Zip(d); await s.ProcessAsync(p,false,default);
    Assert(s.State.History[0].Status=="성공" && File.Exists(p) && Directory.Exists(Path.Combine(d,"일반 압축")),s.State.History[0].Detail);
    Assert(!Directory.Exists(Path.Combine(d,".AutoExtract-Working")));
});
await Test("정상 휴지통 처리 후 작업 폴더 정리", async d =>
{
    var(s,_,r,_)=Setup(d); var p=Zip(d); await s.ProcessAsync(p,true,default);
    Assert(r.Calls==1 && !File.Exists(p) && !Directory.Exists(Path.Combine(d,".AutoExtract-Working")),s.State.History[0].Detail);
});
await Test("다른 실패 작업은 성공 작업 정리 중에도 보존", async d =>
{
    var(s,e,_,_)=Setup(d); var a=Zip(d,"bad.zip");
    e.OnTest=()=>
    {
        var work=Directory.GetDirectories(Path.Combine(d,".AutoExtract-Working")).Single();
        File.WriteAllText(Path.Combine(work,"bz-x.log"),"failure details");
        File.WriteAllText(Path.Combine(work,"payload","partial.bin"),"partial content");
    };
    e.FailExtract=true; await s.ProcessAsync(a,false,default);
    var workRoot=Path.Combine(d,".AutoExtract-Working"); var failedWork=Directory.GetDirectories(workRoot).Single();
    e.FailExtract=false; e.OnTest=null; var b=Zip(d,"good.zip"); await s.ProcessAsync(b,false,default);
    Assert(s.State.History[0].Status=="성공" && Directory.GetDirectories(workRoot).Single()==failedWork);
    Assert(!Directory.Exists(Path.Combine(failedWork,"input")) && !Directory.Exists(Path.Combine(failedWork,"payload")) && File.Exists(a));
    Assert(Directory.GetFileSystemEntries(failedWork).Length==1 && File.ReadAllText(Path.Combine(failedWork,"bz-x.log"))=="failure details");
});
await Test("휴지통 실패 시 원본 복원 후 임시 복사본 정리", async d =>
{
    var(s,_,r,_)=Setup(d); r.Fail=true; var p=Zip(d); await s.ProcessAsync(p,true,default);
    Assert(s.State.History[0].Status=="성공 · 경고" && File.Exists(p) && !Directory.Exists(Path.Combine(d,".AutoExtract-Working")));
});
await Test("재시작 시 이전 실패 작업의 로그와 복구 원본만 보존", d =>
{
    var(s,_,_,_)=Setup(d);
    var work=Path.Combine(d,".AutoExtract-Working",Guid.NewGuid().ToString("N"));
    foreach(var name in new[]{"input","payload","scratch","originals"})
    {
        Directory.CreateDirectory(Path.Combine(work,name));
        File.WriteAllText(Path.Combine(work,name,"data.bin"),name);
    }
    File.WriteAllText(Path.Combine(work,"bz-x.log"),"details");
    File.WriteAllText(Path.Combine(d,"data.bin"),"existing replacement");
    s.RecoverOriginals(d);
    Assert(File.ReadAllText(Path.Combine(work,"bz-x.log"))=="details");
    Assert(File.ReadAllText(Path.Combine(work,"originals","data.bin"))=="originals");
    Assert(File.ReadAllText(Path.Combine(d,"data.bin"))=="existing replacement");
    foreach(var name in new[]{"input","payload","scratch"}) Assert(!Directory.Exists(Path.Combine(work,name)));
    return Task.CompletedTask;
});
await Test("작업 폴더에 복구할 원본이 남으면 정리 거부", async d =>
{
    var(s,e,_,_)=Setup(d); var p=Zip(d);
    e.OnExtract=snapshot=> { var originals=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(snapshot))!,"originals"); Directory.CreateDirectory(originals); File.WriteAllText(Path.Combine(originals,"recover.zip"),"retain me"); };
    await s.ProcessAsync(p,false,default); var workspace=Directory.GetDirectories(Path.Combine(d,".AutoExtract-Working")).Single();
    Assert(s.State.History[0].Status=="성공 · 경고" && File.ReadAllText(Path.Combine(workspace,"originals","recover.zip"))=="retain me");
    Assert(!Directory.Exists(Path.Combine(workspace,"input")));
});
await Test("예상하지 않은 작업 파일은 정리하지 않음", async d =>
{
    var(s,e,_,_)=Setup(d); var p=Zip(d);
    e.OnExtract=snapshot=>File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(snapshot))!,"important.txt"),"retain me");
    await s.ProcessAsync(p,false,default); var workspace=Directory.GetDirectories(Path.Combine(d,".AutoExtract-Working")).Single();
    Assert(s.State.History[0].Status=="성공 · 경고" && File.Exists(Path.Combine(workspace,"important.txt")) && File.Exists(p));
});
await Test("기록 전체 삭제는 재시작 후에도 유지하고 중복 처리 정보 보존", async d =>
{
    var(s,e,r,store)=Setup(d); var p=Zip(d); await s.ProcessAsync(p,false,default); bool notified=false; s.HistoryCleared+=()=>notified=true;
    await s.ClearHistoryAsync(); Assert(notified && s.State.History.Count==0);
    var restarted=new ArchiveExtractionService(e,r,store); Assert(restarted.State.History.Count==0 && restarted.State.Completed.Count==1);
    Assert(restarted.IsExcluded(Path.Combine(d,"일반 압축")) && Directory.Exists(Path.Combine(d,"일반 압축")));
    await restarted.ProcessAsync(p,false,default); Assert(e.Extracts==1 && restarted.State.History.Count==0);
});
await Test("기록 삭제 저장 실패 시 기존 내역 복구", async d =>
{
    var(s,_,_,store)=Setup(d); var p=Zip(d); await s.ProcessAsync(p,false,default);
    var statePath=Path.Combine(store.Root,"state.json"); File.SetAttributes(statePath,FileAttributes.ReadOnly);
    try { await s.ClearHistoryAsync(); throw new Exception("must fail"); }
    catch(Exception e) when(e is IOException or UnauthorizedAccessException) { Assert(s.State.History.Count==1 && s.State.Completed.Count==1); }
    finally { File.SetAttributes(statePath,FileAttributes.Normal); }
    Assert(store.Load<AppState>("state.json").History.Count==1);
});
await Test("진행 중 작업과 기록 삭제를 병행해도 새 완료 내역은 유지", async d =>
{
    var(s,e,_,store)=Setup(d); var p=Zip(d); e.Delay=200;
    var work=s.ProcessAsync(p,false,default); await Task.Delay(50); await s.ClearHistoryAsync(); await work;
    Assert(s.State.History.Count==1 && s.State.History[0].Status=="성공" && store.Load<AppState>("state.json").History.Count==1);
});
Console.WriteLine($"RESULT: {passed} passed, {failures.Count} failed. Artifacts: {root}");
return failures.Count==0 ? 0 : 1;

sealed class FakeRecycle : IFileRecycleService
{
    public bool Fail; public int Calls;
    public void Recycle(string path) { Calls++; if(Fail) throw new IOException("simulated recycle failure"); File.Delete(path); }
}
sealed class FakeEngine : IArchiveEngine
{
    public int Tests, Extracts, Delay; public bool FailTest, FailExtract; public Action? OnTest; public Action<string>? OnExtract;
    public async Task TestAsync(string path,string workspace,CancellationToken token) { Interlocked.Increment(ref Tests); OnTest?.Invoke(); if(Delay>0) await Task.Delay(Delay,token); if(FailTest) throw new InvalidDataException("simulated bad archive"); using var zip=ZipFile.OpenRead(path); foreach(var e in zip.Entries) { using var s=e.Open(); await s.CopyToAsync(Stream.Null,token); } }
    public Task ExtractAsync(string path,string destination,string workspace,CancellationToken token) { Interlocked.Increment(ref Extracts); if(FailExtract) throw new IOException("simulated disk full"); ZipFile.ExtractToDirectory(path,destination); OnExtract?.Invoke(path); return Task.CompletedTask; }
}
