'use strict';
var https = require('https');
var crypto = require('crypto');

function validHost(host) {
  if (typeof host !== 'string' || !/^(?:\d{1,3}\.){3}\d{1,3}$/.test(host)) return false;
  var parts = host.split('.'), p = parts.map(Number);
  if (p.some(function (n,i) { return n < 0 || n > 255 || String(n) !== parts[i]; })) return false;
  return p[0] === 10 || (p[0] === 172 && p[1] >= 16 && p[1] <= 31) ||
    (p[0] === 192 && p[1] === 168) || (p[0] === 169 && p[1] === 254);
}
function validConfig(c) {
  return c && validHost(c.host) && typeof c.username === 'string' && /^[a-zA-Z0-9_-]{8,128}$/.test(c.username) &&
    typeof c.clientkey === 'string' && /^[a-fA-F0-9]{32}$/.test(c.clientkey) &&
    typeof c.certificate_sha256 === 'string' && /^[a-fA-F0-9]{64}$/.test(c.certificate_sha256);
}
function parseResponse(host, fingerprint, data) {
  var items = JSON.parse(data);
  if (!Array.isArray(items) || !items.length) throw new Error('Bridge 返回的配对信息无效');
  if (items[0].error) {
    if (items[0].error.type === 101) throw new Error('请先按下 Bridge 按钮，然后再次点击配对');
    throw new Error('Bridge 拒绝配对，请检查 Bridge 状态');
  }
  var success = items[0].success || {};
  var config = {host:host, username:success.username, clientkey:success.clientkey, certificate_sha256:fingerprint};
  if (!validConfig(config)) throw new Error('Bridge 未返回完整的配对信息');
  return config;
}
function pair(host, callback) {
  if (!validHost(host)) return callback(new Error('请输入有效的局域网 IPv4 地址'));
  var done = false;
  function finish(error, config) { if (!done) { done = true; callback(error, config); } }
  var body = JSON.stringify({devicetype:'tv_sync#samsung_tv',generateclientkey:true});
  var req = https.request({host:host,port:443,path:'/api',method:'POST',rejectUnauthorized:false,agent:false,
    headers:{'Content-Type':'application/json','Content-Length':Buffer.byteLength(body),'Connection':'close'}}, function (res) {
    var chunks = [], size = 0, fingerprint;
    try {
      var cert = res.socket.getPeerCertificate(true);
      if (!cert || !cert.raw) throw new Error('无法读取 Bridge 证书');
      fingerprint = crypto.createHash('sha256').update(cert.raw).digest('hex');
    } catch (e) { req.destroy();return finish(new Error('无法读取 Bridge 证书')); }
    if (res.statusCode !== 200) { res.resume();return finish(new Error('Bridge 配对请求失败')); }
    res.on('data', function (part) { size += part.length;if (size > 65536) { req.destroy();finish(new Error('Bridge 响应过大')); } else chunks.push(part); });
    res.on('error', function () { finish(new Error('Bridge 连接中断')); });
    res.on('end', function () { try { finish(null,parseResponse(host,fingerprint,Buffer.concat(chunks).toString('utf8'))); } catch (e) { finish(new Error(e.message || 'Bridge 响应无效')); } });
  });
  req.setTimeout(8000,function () { req.destroy();finish(new Error('连接超时，请检查 Bridge 地址和网络')); });
  req.on('error',function () { finish(new Error('无法连接 Bridge，请检查地址和网络')); });
  req.end(body);
}
module.exports = {validHost:validHost,validConfig:validConfig,parseResponse:parseResponse,pair:pair};
