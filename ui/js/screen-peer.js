// Both ends use the browser's encrypted WebRTC media transport. No public STUN
// or TURN servers: this feature is intended for devices on the same LAN.
export class ScreenPeer {
  constructor({stream,send,received=()=>{},state=()=>{},failed=()=>{}}) {
    this.pc=new RTCPeerConnection({iceServers:[]});
    this.pending=[];this.closed=false;this.outgoing=Promise.resolve();this.failed=failed;
    this.pc.onicecandidate=event=>{if(event.candidate)this.send(send,'candidate',event.candidate.toJSON());};
    this.pc.ontrack=event=>received(event.track);
    this.pc.onconnectionstatechange=()=>state(this.pc.connectionState);
    if(stream)for(const track of stream.getTracks())this.pc.addTrack(track,stream);
    this.signal=send;
  }
  send(send,type,data) {
    this.outgoing=this.outgoing.then(()=>this.closed?undefined:send(type,JSON.stringify(data))).catch(error=>{if(!this.closed)this.failed(error);});
    return this.outgoing;
  }
  async offer() {
    await this.pc.setLocalDescription(await this.pc.createOffer());
    await this.send(this.signal,'offer',this.pc.localDescription);
  }
  async receive(messages) {
    for(const message of messages) {
      if(this.closed)return;
      const data=JSON.parse(message.data);
      if(message.type==='candidate') {
        if(this.pc.remoteDescription)await this.pc.addIceCandidate(data);else this.pending.push(data);
      } else {
        await this.pc.setRemoteDescription(data);
        for(const candidate of this.pending)await this.pc.addIceCandidate(candidate);
        this.pending=[];
        if(message.type==='offer') {
          await this.pc.setLocalDescription(await this.pc.createAnswer());
          await this.send(this.signal,'answer',this.pc.localDescription);
        }
      }
    }
  }
  close() {this.closed=true;this.pc.onicecandidate=null;this.pc.ontrack=null;this.pc.close();this.pending=[];}
}
