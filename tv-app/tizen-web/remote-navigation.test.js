'use strict';
const assert=require('assert');
const fs=require('fs');
const vm=require('vm');
const ids=['status','detail','area','sensitivity','previous','next','less','more','start','stop','bridge-ip','pair'];
const nodes={}, listeners={};
let exits=0, now=10000;
const document={activeElement:null,getElementById:id=>nodes[id],addEventListener:(name,fn)=>{listeners[name]=fn;}};
ids.forEach(id=>{
  const events={};
  nodes[id]={id,textContent:'',value:'',addEventListener:(name,fn)=>{events[name]=fn;},
    focus(){if(document.activeElement && document.activeElement!==this) document.activeElement.blur();document.activeElement=this;},
    blur(){if(document.activeElement===this) document.activeElement=null;if(events.blur) events.blur();}};
});
const context={document,Date:{now:()=>now},window:{HUE_CONTROL_TOKEN:'x'},localStorage:{},XMLHttpRequest:function(){},
  tizen:{application:{launchAppControl(){},getCurrentApplication(){return {exit(){exits++;}};}},ApplicationControl:function(){}},
  setTimeout(){return 1;},clearTimeout(){},setInterval(){}};
vm.runInNewContext(fs.readFileSync(__dirname+'/app.js','utf8'),context);
function key(code,target=document.activeElement){let prevented=false;listeners.keydown({keyCode:code,target,preventDefault(){prevented=true;}});return prevented;}
nodes['bridge-ip'].focus();
assert.strictEqual(key(39),false,'arrows remain available to the IME');
assert.strictEqual(document.activeElement.id,'bridge-ip');
assert.strictEqual(key(65376),false,'IME Done is not cancelled');
assert.strictEqual(document.activeElement.id,'pair','IME Done moves to pair');
nodes['bridge-ip'].focus();now+=1000;key(65385);
assert.strictEqual(document.activeElement.id,'pair','IME Cancel moves to pair');
nodes['bridge-ip'].focus();now+=1000;key(10009);
assert.strictEqual(document.activeElement.id,'pair','Back moves from input to pair');
assert.strictEqual(exits,0,'Back while editing does not exit');
key(10009);assert.strictEqual(exits,0,'duplicate Back after IME is ignored');
now+=1000;key(10009);assert.strictEqual(exits,1,'Back outside input exits normally');
nodes.pair.focus();assert.strictEqual(key(40),true);assert.strictEqual(document.activeElement.id,'stop');
assert.strictEqual(key(38),true);assert.strictEqual(document.activeElement.id,'pair');
console.log('Remote navigation tests passed: IME Done/Cancel/Back and four-direction focus.');
