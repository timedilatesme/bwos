using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
namespace Bwos {
internal static class NextTests {
  private static int assertions;
  static void Check(bool ok,string message){assertions++;if(!ok)throw new Exception(message);}
  static Dictionary<string,object> Command(string id,string kind){return new Dictionary<string,object>{{"id",id},{"kind",kind}};}
  public static void Main(){
#if BWOS_NEXT
    Check(new Config().Port==47923,"Preview endpoint remains isolated");Check(ShellManager.ShellTitle=="BWOS Next","Preview title is isolated");
#else
    Check(new Config().Port==47913,"Primary preserves existing endpoint");Check(ShellManager.ShellTitle=="BWOS","Primary owns the default shell title");
#endif
    var actions=new CoreActions();var home=actions.Submit(Command("home","home")) as Dictionary<string,object>;
    Check(Json.Str(home,"status","")=="accepted","Acceptance is not completion");
    actions.Submit(Command("home","open"));
    var batch=actions.Claim() as Dictionary<string,object>;Check(Json.List(batch,"commands").Count==1,"Duplicate IDs do not dispatch twice");
    Check(Json.List(actions.Claim() as Dictionary<string,object>,"commands").Count==0,"A claim cannot reexecute dispatched work");
    actions.Ack(new Dictionary<string,object>{{"id","home"},{"ok",true}});
    Check(Json.Str(actions.Submit(Command("home","home")) as Dictionary<string,object>,"status","")=="completed","Duplicate returns acknowledged result");
    actions.Home();Thread.Sleep(2050);var records=actions.Describe() as Dictionary<string,object>[];
    Check(records.Any(r=>Json.Str(r,"status","")=="timeout"),"Undelivered Home expires");
    Check(Json.List(actions.Claim() as Dictionary<string,object>,"commands").Count==0,"Expired action is never dispatched");
    Check(!Json.Bool(actions.Submit(Command("bad","script")) as Dictionary<string,object>,"ok",true),"Unknown action rejected");
    var busy=new CoreActions();for(int i=0;i<128;i++)busy.Submit(Command("active"+i,"home"));
    Check(!Json.Bool(busy.Submit(Command("overflow","home")) as Dictionary<string,object>,"ok",true),"Bounded active queue rejects overflow");
    var delegated=new PlaybackService();string seen=null;delegated.ControlAction=(id,a,v)=>{seen=id+":"+a+":"+v;return new Dictionary<string,object>{{"ok",true}};};delegated.Control("42","seekForward","10");Check(seen=="42:seekForward:10","All native playback enters the shared core");
    Check(VoiceCommands.Parse("Hey console, go home").Action=="home","Default wake command preserved");
    var native=new CoreActions();int nativeCalls=0;var finished=new ManualResetEvent(false);native.NativeAction=body=>{Interlocked.Increment(ref nativeCalls);finished.Set();return new Dictionary<string,object>{{"ok",true}};};native.Submit(Command("native","native"));Check(finished.WaitOne(1000),"Native action executes away from navigation");native.Submit(Command("native","native"));Thread.Sleep(20);Check(nativeCalls==1,"Native duplicate executes exactly once");
    var cancelled=new CoreActions();cancelled.Submit(Command("cancelled","home"));var cancel=Command("cancel","cancel");cancel["target"]="cancelled";cancelled.Submit(cancel);Check((cancelled.Describe() as Dictionary<string,object>[]).Any(r=>Json.Str(r,"id","")=="cancelled" && Json.Str(r,"status","")=="cancelled"),"Cancellation changes the original acknowledgement");
    var text=new CoreActions();var typed=Command("typed","text");typed["value"]="private input";text.Submit(typed);Check(!(text.Describe() as Dictionary<string,object>[])[0].ContainsKey("value"),"Diagnostics redact remote text");
    var remote=new PhoneRemote(new ShellManager(new Config()),47934);remote.ActionControl=(a,id)=>id;Check(remote.Press("home","network-id")=="network-id","Phone/viewer request identity reaches broker");remote.TextControl=(t,a,id)=>id+":"+t;Check(remote.Type("हिंदी","","text-id")=="text-id:हिंदी","Unicode text and identity reach broker");
    bool woke;VoiceCommands.StripWake("हे कंसोल",out woke);Check(woke,"Hindi wake phrase preserved");
    var stream=new ScreenStream();var started=stream.Start("http://127.0.0.1:47924");string session=Json.Str(started,"session","");
    Check(session.Length>0,"Screen session available");
    stream.Exchange(true,session,"stop",new Dictionary<string,object>());
    Check(!Json.Bool(stream.Exchange(true,session,"poll",new Dictionary<string,object>()),"ok",true),"Disconnect revokes signaling");
    Console.WriteLine(assertions+" replacement host assertions passed");
  }
}
}
