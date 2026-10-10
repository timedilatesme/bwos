import assert from 'node:assert/strict';
import {ScreenPeer} from '../ui/js/screen-peer.js';

class Connection {
  constructor(options){assert.deepEqual(options,{iceServers:[]});this.remoteDescription=null;this.tracks=[];this.ice=[];}
  addTrack(track){this.tracks.push(track);}
  async createOffer(){return {type:'offer',sdp:'screen-and-audio'};}
  async createAnswer(){return {type:'answer',sdp:'accepted'};}
  async setLocalDescription(description){this.localDescription=description;this.onicecandidate?.({candidate:{toJSON:()=>({candidate:'lan-candidate'})}});}
  async setRemoteDescription(description){this.remoteDescription=description;}
  async addIceCandidate(candidate){assert.ok(this.remoteDescription,'ICE is applied only after SDP');this.ice.push(candidate);}
  close(){this.closed=true;}
}
globalThis.RTCPeerConnection=Connection;
const hostMessages=[],viewerMessages=[],received=[];
const video={kind:'video'},audio={kind:'audio'};
const host=new ScreenPeer({stream:{getTracks:()=>[video,audio]},send:async(type,data)=>hostMessages.push({type,data})});
const viewer=new ScreenPeer({send:async(type,data)=>viewerMessages.push({type,data}),received:track=>received.push(track)});
await host.offer();await host.outgoing;
assert.deepEqual(host.pc.tracks,[video,audio],'Both screen and system-audio tracks are added');
assert.equal(hostMessages[0].type,'candidate','Exercise ICE arriving before the offer');
await viewer.receive(hostMessages);await viewer.outgoing;
assert.equal(viewer.pc.ice.length,1,'Receiver retains early ICE until offer arrives');
await host.receive(viewerMessages);
assert.equal(host.pc.remoteDescription.type,'answer');
assert.equal(host.pc.ice.length,1,'Sender retains early ICE until answer arrives');
viewer.pc.ontrack({track:audio});viewer.pc.ontrack({track:video});
assert.deepEqual(received,[audio,video],'Incoming audio and video are delivered separately');
host.close();viewer.close();
assert.ok(host.pc.closed && viewer.pc.closed,'Both connections close');
const before=hostMessages.length;await host.send(host.signal,'candidate',{});
assert.equal(hostMessages.length,before,'Closed peer sends no further signaling');
console.log('PASS: LAN-only WebRTC negotiation, early ICE, screen/audio tracks, and peer cleanup');
