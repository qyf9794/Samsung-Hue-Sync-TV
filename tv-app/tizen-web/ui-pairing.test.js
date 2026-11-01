'use strict';
const assert = require('assert');
const fs = require('fs');
const vm = require('vm');

function scenario(coreAccepts) {
  const names = ['status','detail','area','sensitivity','previous','next','less','more','start','stop','bridge-ip','pair'];
  const nodes = {};
  names.forEach(name => { nodes[name] = {id:name,textContent:'',value:'',focus(){},addEventListener(){}}; });
  nodes['bridge-ip'].value = '192.168.1.20';
  const sent = [], calls = [], scheduled = [];
  const config = {host:'192.168.1.20',username:'valid-user',clientkey:'a'.repeat(32),certificate_sha256:'b'.repeat(64)};
  let listener;
  class Xhr {
    open(method,path) { this.method=method;this.path=path; }
    setRequestHeader() {}
    send(body) {
      calls.push({path:this.path,body:body});
      this.status=200;
      if (this.path.endsWith('/pair')) this.responseText=JSON.stringify({configuration:config});
      else if (this.path.endsWith('/bridge-config')) this.responseText=JSON.stringify({saved:true});
      else this.responseText=JSON.stringify({areas:[]});
      this.onload();
    }
  }
  const local = {addMessagePortListener(fn){ listener=fn; }};
  const remote = {sendMessage(items){
    const msg={};items.forEach(item => { msg[item.key]=item.value; });sent.push(msg);
    const ok=msg.command==='status' || coreAccepts;
    const result = {id:msg.id,state:ok?'idle':'error',detail:msg.command==='status'?'准备就绪':ok?'Bridge 已配对':'无法保存 Bridge 配置',sensitivity:'3'};
    listener(Object.keys(result).map(key => ({key:key,value:result[key]})));
  }};
  const document = {getElementById:id => nodes[id],addEventListener(){},activeElement:nodes.start};
  const tizen = {messageport:{requestTrustedLocalMessagePort:() => local,requestTrustedRemoteMessagePort:() => remote},
    application:{launchAppControl(control,id,success){success();},getCurrentApplication(){return {exit(){}};}},ApplicationControl:function(){}};
  const context={document,tizen,XMLHttpRequest:Xhr,window:{HUE_CONTROL_TOKEN:'c'.repeat(64)},localStorage:{getItem(){return '';},setItem(){}},
    setTimeout(fn){scheduled.push(fn);return scheduled.length;},clearTimeout(){},setInterval(){}};
  vm.runInNewContext(fs.readFileSync(__dirname+'/app.js','utf8'),context);
  scheduled[0]();
  nodes.pair.onclick();
  const configured=sent.find(msg => msg.command==='configure');
  assert(configured);
  assert.strictEqual(JSON.parse(configured.configuration).host,config.host);
  assert.strictEqual(calls.filter(call => call.path.endsWith('/bridge-config')).length,coreAccepts?1:0);
  if (coreAccepts) assert.strictEqual(nodes.detail.textContent,'Bridge 配对成功');
  else assert.match(nodes.detail.textContent,/核心未保存/);
}
scenario(true);
scenario(false);
console.log('UI pairing tests passed: core acknowledgement precedes UI save; failed core save stops the flow.');
