'use strict';
var http = require('http');
var fs = require('fs');
var tls = require('tls');
var crypto = require('crypto');
var pairing = require('./pairing');
var token = require('../control-token.json').token;
var server = null, state = 'idle', detail = '准备就绪', areas = [];
var config = '/opt/usr/home/owner/apps_rw/HueSyncTV1/data/hue-bridge.json';
var pendingConfigPath = '/opt/usr/home/owner/apps_rw/HueSyncTV1/data/hue-bridge-pending.json';
var logPath = '/opt/usr/home/owner/apps_rw/HueSyncTV1/data/service-diagnostic.log';
function diag(message) {
  try {
    if (fs.existsSync(logPath) && fs.statSync(logPath).size > 32768) fs.unlinkSync(logPath);
    fs.appendFileSync(logPath, new Date().toISOString() + ' ' + message + '\n');
  } catch (ignored) {}
}
process.on('uncaughtException', function (error) { state = 'error'; detail = '同步服务出现错误'; diag('uncaught ' + error.name + ': ' + error.message); });

function authorized(value) {
  if (typeof value !== 'string' || typeof token !== 'string' || value.length !== token.length) return false;
  var diff = 0, i;
  for (i = 0; i < value.length; i++) diff |= value.charCodeAt(i) ^ token.charCodeAt(i);
  return diff === 0;
}
function respond(res, code) {
  res.writeHead(code, {'Content-Type':'application/json', 'Cache-Control':'no-store', 'Access-Control-Allow-Origin':'*'});
  res.end(JSON.stringify({state:state, detail:detail}));
}
function jsonReply(res, body) {
  res.writeHead(200, {'Content-Type':'application/json','Cache-Control':'no-store','Access-Control-Allow-Origin':'*'});
  res.end(JSON.stringify(body));
}
function readJson(req, callback) {
  var parts = [], size = 0, done = false;
  req.on('data', function (chunk) { size += chunk.length;if (size > 16384) { done = true;req.destroy(); } else parts.push(chunk); });
  req.on('end', function () {
    if (done) return;
    try { callback(null,JSON.parse(Buffer.concat(parts).toString('utf8'))); }
    catch (error) { callback(new Error('请求格式无效')); }
  });
}
function saveBridgeConfig(path, value) {
  if (!pairing.validConfig(value)) throw new Error('Bridge 配置无效');
  var temp = path + '.' + crypto.randomBytes(12).toString('hex') + '.tmp';
  try { fs.writeFileSync(temp, JSON.stringify(value), {mode:384,flag:'wx'});fs.renameSync(temp,path); }
  finally { try { if (fs.existsSync(temp)) fs.unlinkSync(temp); } catch (ignored) {} }
}
function engine(method, endpoint, res) {
  var done = false, body = '';
  function fail(error) {
    if (done) return; done = true;
    state = 'offline'; detail = '同步核心未连接，请重新打开应用';
    diag('engine ' + error.message); respond(res, 200);
  }
  var q = http.request({host:'127.0.0.1',port:18766,path:endpoint,method:method,headers:{'X-Hue-Control':token}}, function (response) {
    response.on('data', function (part) { body += part.toString(); if (body.length > 4096) { q.destroy(); fail(new Error('response too large')); } });
    response.on('error', fail);
    response.on('end', function () {
      if (done) return;
      try {
        var item = JSON.parse(body);
        if (response.statusCode !== 200 || typeof item.state !== 'string') throw new Error('invalid engine response');
        done = true; state = item.state; detail = item.detail || '';
        respond(res, 200);
      } catch (error) { fail(error); }
    });
  });
  q.setTimeout(4000, function () { q.destroy(); fail(new Error('timeout')); });
  q.on('error', fail); q.end();
}
function loadAreas(res) {
  var settings;
  try {
    settings = JSON.parse(fs.readFileSync(config, 'utf8'));
    if (!/^(?:\d{1,3}\.){3}\d{1,3}$/.test(settings.host) || !/^[a-fA-F0-9]{64}$/.test(settings.certificate_sha256) || !/^[a-zA-Z0-9_-]{8,128}$/.test(settings.username)) throw new Error('invalid bridge configuration');
  } catch (error) { state = 'error'; detail = '尚未配置 Hue Bridge'; diag('bridge configuration ' + error.message); return respond(res, 200); }
  var done = false, chunks = [], size = 0;
  var socket = tls.connect({host:settings.host, port:443, rejectUnauthorized:false}, function () {
    try {
      var cert = socket.getPeerCertificate(true), actual = crypto.createHash('sha256').update(cert.raw).digest('hex');
      if (actual.toLowerCase() !== settings.certificate_sha256.toLowerCase()) throw new Error('bridge certificate mismatch');
      socket.write('GET /clip/v2/resource/entertainment_configuration HTTP/1.1\r\nHost: ' + settings.host + '\r\nhue-application-key: ' + settings.username + '\r\nAccept: application/json\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n');
    } catch (error) { finish(error); socket.destroy(); }
  });
  socket.setTimeout(8000, function () { finish(new Error('bridge timeout')); socket.destroy(); });
  socket.on('data', function (chunk) { size += chunk.length; if (size > 1048576) { finish(new Error('bridge response too large')); socket.destroy(); } else chunks.push(chunk); });
  socket.on('error', function (error) { finish(error); });
  socket.on('close', function () { if (!done) finish(new Error('bridge connection closed')); });
  socket.on('end', function () {
    try {
      var raw = Buffer.concat(chunks), split = raw.indexOf('\r\n\r\n');
      if (split < 0) throw new Error('incomplete bridge response');
      var headers = raw.slice(0, split).toString('ascii'), body = raw.slice(split + 4);
      if (!/^HTTP\/1\.[01] 200\b/.test(headers)) throw new Error('bridge returned an error');
      if (/\r\ntransfer-encoding:\s*chunked/i.test(headers)) {
        var offset = 0, parts = [];
        while (true) {
          var end = body.indexOf('\r\n', offset); if (end < 0) throw new Error('invalid chunk');
          var length = parseInt(body.slice(offset, end).toString('ascii').split(';')[0], 16); if (!isFinite(length)) throw new Error('invalid chunk size');
          offset = end + 2; if (length === 0) break; if (offset + length + 2 > body.length) throw new Error('incomplete chunk');
          parts.push(body.slice(offset, offset + length)); offset += length + 2;
        }
        body = Buffer.concat(parts);
      }
      var result = JSON.parse(body.toString('utf8'));
      if (!Array.isArray(result.data) || (result.errors && result.errors.length)) throw new Error('invalid bridge data');
      areas = result.data.map(function (a) { return {id:a.id, name:a.metadata && a.metadata.name, status:a.status}; }).filter(function (a) { return typeof a.name === 'string' && typeof a.id === 'string' && /^[0-9a-fA-F-]{36}$/.test(a.id); });
      finish(null);
    } catch (error) { finish(error); }
  });
  function finish(error) {
    if (done) return; done = true;
    if (error) { state = 'error'; detail = '无法读取娱乐区域，请检查 Hue Bridge'; diag('bridge list ' + error.message); return respond(res, 200); }
    res.writeHead(200, {'Content-Type':'application/json','Cache-Control':'no-store','Access-Control-Allow-Origin':'*'});
    res.end(JSON.stringify({areas:areas}));
  }
}
function handler(req, res) {
  req.on('error', function (error) { diag('request error ' + error.message); });
  res.on('error', function (error) { diag('response error ' + error.message); });
  if (req.url !== '/status') diag('request ' + req.method + ' ' + req.url.split('?')[0]);
  if (req.method === 'OPTIONS') { res.writeHead(204, {'Access-Control-Allow-Origin':'*','Access-Control-Allow-Headers':'X-Hue-Control, Content-Type','Access-Control-Allow-Methods':'GET, POST, OPTIONS'}); res.end(); return; }
  if (!authorized(req.headers['x-hue-control'])) { respond(res, 403); return; }
  if (req.method === 'POST' && req.url === '/pair') return readJson(req, function (error, value) {
    if (error || !value || !pairing.validHost(value.host)) return jsonReply(res,{error:'请输入有效的局域网 IPv4 地址'});
    pairing.pair(value.host,function (failure, result) {
      if (failure) return jsonReply(res,{error:failure.message});
      try { saveBridgeConfig(pendingConfigPath,result);jsonReply(res,{configuration:result}); }
      catch (e) { diag('save pending bridge configuration ' + e.name);jsonReply(res,{error:'无法保存待完成的 Bridge 配对'}); }
    });
  });
  if (req.method === 'GET' && req.url === '/pending-pair') {
    try { var pending=JSON.parse(fs.readFileSync(pendingConfigPath,'utf8'));return jsonReply(res,pairing.validConfig(pending)?{configuration:pending}:{}); }
    catch (ignored) { return jsonReply(res,{}); }
  }
  if (req.method === 'POST' && req.url === '/bridge-config') return readJson(req, function (error, value) {
    try { if (error) throw error;saveBridgeConfig(config,value);try { fs.unlinkSync(pendingConfigPath); } catch (ignored) {}jsonReply(res,{saved:true}); }
    catch (failure) { diag('save bridge configuration ' + failure.name);jsonReply(res,{error:'无法保存界面 Bridge 配置'}); }
  });
  if (req.method === 'GET' && req.url === '/status') return engine('GET', '/status', res);
  if (req.method === 'GET' && req.url === '/areas') return loadAreas(res);
  var match = /^\/start\?area=([0-9a-fA-F-]{36})$/.exec(req.url);
  if (req.method === 'POST' && match) {
    if (!areas.some(function (a) { return a.id === match[1]; })) { state = 'error'; detail = '请先选择娱乐区域'; return respond(res, 200); }
    return engine('POST', '/start?area=' + encodeURIComponent(match[1]), res);
  }
  if (req.method === 'POST' && req.url === '/stop') return engine('POST', '/stop', res);
  respond(res, 404);
}
module.exports.onStart = function () {
  diag('onStart node ' + process.version);
  if (server) return;
  server = http.createServer(handler);
  server.on('error', function (error) { state = 'error'; detail = '同步服务无法启动'; diag('server error ' + error.message); });
  server.listen(18765, '127.0.0.1', function () { diag('server listening'); });
};
module.exports.onRequest = function () { diag('onRequest'); };
module.exports.onExit = function () { diag('onExit'); if (server) server.close(); server = null; };
