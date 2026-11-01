'use strict';
const assert = require('assert');
const EventEmitter = require('events');
const fs = require('fs');
const vm = require('vm');
const actualPairing = require('./pairing');
const token = 'c'.repeat(64);
const config = {host:'192.168.1.20',username:'valid-user',clientkey:'a'.repeat(32),certificate_sha256:'b'.repeat(64)};
const files = {}, logs = [];
let handler;
const fakeFs = {
  existsSync:path => Object.prototype.hasOwnProperty.call(files,path),
  appendFileSync:(path,value) => logs.push(value),
  writeFileSync:(path,value) => { files[path]=value; },
  renameSync:(from,to) => { files[to]=files[from];delete files[from]; },
  unlinkSync:path => { if (!(path in files)) throw new Error('missing');delete files[path]; },
  readFileSync:path => { if (!(path in files)) throw new Error('missing');return files[path]; }
};
const fakeHttp = {createServer(fn){handler=fn;return {on(){},listen(port,host,cb){cb();}};}};
const pairing = Object.assign({},actualPairing,{pair:(host,cb) => cb(null,config)});
const fakeProcess = {version:'test',on(){}};
const sandbox = {module:{exports:{}},process:fakeProcess,Buffer,require(name){
  if (name==='http') return fakeHttp;if (name==='fs') return fakeFs;
  if (name==='../control-token.json') return {token};if (name==='./pairing') return pairing;
  return require(name);
}};
vm.runInNewContext(fs.readFileSync(__dirname+'/index.js','utf8'),sandbox);
sandbox.module.exports.onStart();
function request(method,url,body,key=token) {
  const req=new EventEmitter(), res=new EventEmitter();
  req.method=method;req.url=url;req.headers={'x-hue-control':key};req.destroy=()=>{};
  res.writeHead=code => {res.statusCode=code;};res.end=data => {res.body=JSON.parse(data);};
  handler(req,res);
  if (body) {req.emit('data',Buffer.from(JSON.stringify(body)));req.emit('end');}
  return res;
}
assert.strictEqual(request('POST','/pair',{host:config.host},'wrong').statusCode,403);
assert.strictEqual(request('POST','/pair',{host:config.host}).body.configuration.username,config.username);
const pending='/opt/usr/home/owner/apps_rw/HueSyncTV1/data/hue-bridge-pending.json';
const saved='/opt/usr/home/owner/apps_rw/HueSyncTV1/data/hue-bridge.json';
assert(files[pending]);assert(!files[saved]);
assert.strictEqual(request('GET','/pending-pair').body.configuration.host,config.host);
assert.strictEqual(request('POST','/bridge-config',{host:'127.0.0.1'}).body.error,'无法保存界面 Bridge 配置');
assert(files[pending]);assert(!files[saved]);
assert.strictEqual(request('POST','/bridge-config',config).body.saved,true);
assert(files[saved]);assert(!files[pending]);
assert(!logs.join('').includes(config.clientkey));
console.log('Service pairing tests passed: token authorization, recovery staging and atomic save.');
