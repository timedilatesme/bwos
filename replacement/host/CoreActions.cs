using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
namespace Bwos {
    // Small bounded mailbox. Acceptance is distinct from adapter acknowledgement.
    internal sealed class CoreActions {
        private readonly object gate = new object();
        private readonly Dictionary<string, Dictionary<string, object>> records = new Dictionary<string, Dictionary<string, object>>();
        private readonly Dictionary<string, DateTime> deadlines = new Dictionary<string, DateTime>();
        public Func<Dictionary<string,object>,object> NativeAction;
        private void Expire() {
            foreach (var id in records.Keys.ToArray()) {
                var r = records[id];
                if ((Json.Str(r,"status","")=="accepted" || Json.Str(r,"status","")=="dispatched") && DateTime.UtcNow >= deadlines[id]) {r["status"]="timeout";r["ok"]=false;r["error"]="Browser action deadline exceeded";}
            }
        }
        public object Submit(Dictionary<string, object> body) {
            lock(gate) {
                Expire();string id=Json.Str(body,"id",Guid.NewGuid().ToString("N"));
                if(id.Length==0 || id.Length>100)return WebServer.Err("Invalid action identity");
                if(records.ContainsKey(id))return new Dictionary<string, object>(records[id]);
                string kind=Json.Str(body,"kind","");
                if(!new[]{"home","open","playback","navigate","text","cancel","native"}.Contains(kind))return WebServer.Err("Unknown action");
                if(Json.Serialize(body).Length>5000)return WebServer.Err("Action too large");
                if(kind=="cancel") {
                    Dictionary<string,object> target;
                    if(records.TryGetValue(Json.Str(body,"target",""),out target) && new[]{"accepted","dispatched"}.Contains(Json.Str(target,"status",""))) {target["status"]="cancelled";target["ok"]=false;target["error"]="Action cancelled";}
                }
                if(records.Count>=128) {
                    string old=records.Keys.FirstOrDefault(k=>Json.Str(records[k],"status","")!="accepted" && Json.Str(records[k],"status","")!="dispatched");
                    if(old==null)return WebServer.Err("Action capacity reached");records.Remove(old);deadlines.Remove(old);
                }
                var r=new Dictionary<string, object>(body);r["id"]=id;r["ok"]=true;r["status"]="accepted";r["timeout"]=kind=="home"?2000:4000;
                deadlines[id]=DateTime.UtcNow.AddMilliseconds(Convert.ToInt32(r["timeout"]));records[id]=r;
                if(kind=="native") {
                    r["status"]="dispatched";
                    ThreadPool.QueueUserWorkItem(delegate {
                        lock(gate){Expire();if(Json.Str(r,"status","")!="dispatched")return;}
                        object outcome;
                        try{outcome=NativeAction==null?WebServer.Err("Native actions unavailable"):NativeAction(new Dictionary<string,object>(body));}
                        catch(Exception ex){outcome=WebServer.Err(ex.Message);}
                        var result=outcome as Dictionary<string,object> ?? new Dictionary<string,object>{{"ok",true}};
                        lock(gate){Expire();if(Json.Str(r,"status","")=="dispatched"){r["ok"]=Json.Bool(result,"ok",true);r["status"]=Json.Bool(result,"ok",true)?"completed":"failed";r["error"]=Json.Str(result,"error","");r["message"]=Json.Str(result,"message","");}}
                    });
                }
                return new Dictionary<string, object>(r);
            }
        }
        public object Claim() {lock(gate){Expire();var batch=records.Values.Where(r=>Json.Str(r,"status","")=="accepted").ToArray();foreach(var r in batch){r["status"]="dispatched";r["timeout"]=Math.Max(50,(int)(deadlines[Json.Str(r,"id","")]-DateTime.UtcNow).TotalMilliseconds);}return new Dictionary<string,object>{{"commands",batch}};}}
        public object Ack(Dictionary<string,object> body) {lock(gate){Expire();Dictionary<string,object> r;if(!records.TryGetValue(Json.Str(body,"id",""),out r))return WebServer.Err("Unknown action");if(Json.Str(r,"status","")=="dispatched"){r["ok"]=Json.Bool(body,"ok",false);r["status"]=Json.Bool(body,"ok",false)?"completed":"failed";r["error"]=Json.Str(body,"error","").Substring(0,Math.Min(180,Json.Str(body,"error","").Length));}return new Dictionary<string,object>(r);}}
        public object Describe(){lock(gate){Expire();return records.Values.Select(r=>{var copy=new Dictionary<string,object>(r);if(Json.Str(copy,"kind","")=="text")copy.Remove("value");return copy;}).ToArray();}}
        public Dictionary<string,object> Wait(string id) {
            for(int i=0;i<85;i++) {
                lock(gate){Expire();Dictionary<string,object> r;if(!records.TryGetValue(id,out r))return WebServer.Err("Action no longer available");if(!new[]{"accepted","dispatched"}.Contains(Json.Str(r,"status","")))return new Dictionary<string,object>(r);}
                Thread.Sleep(50);
            }
            return WebServer.Err("Action was not acknowledged. Try again.");
        }
        public void Home(){Submit(new Dictionary<string,object>{{"kind","home"}});}
    }
}
