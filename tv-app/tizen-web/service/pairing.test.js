'use strict';
const assert = require('assert');
const https = require('https');
const EventEmitter = require('events');
const pairing = require('./pairing');

async function run() {
  assert(pairing.validHost('192.168.1.20'));
  assert(!pairing.validHost('192.168.001.20'));
  assert(!pairing.validHost('127.0.0.1'));
  assert(!pairing.validHost('example.com'));
  const fingerprint = 'a'.repeat(64);
  const success = JSON.stringify([{success:{username:'valid-user',clientkey:'b'.repeat(32)}}]);
  assert.strictEqual(pairing.parseResponse('192.168.1.20',fingerprint,success).username,'valid-user');
  assert.throws(() => pairing.parseResponse('192.168.1.20',fingerprint,JSON.stringify([{error:{type:101}}])),/按下/);
  assert.throws(() => pairing.parseResponse('192.168.1.20',fingerprint,JSON.stringify([{success:{username:'valid-user'}}])),/完整/);

  const original = https.request;
  try {
    let requestBody;
    https.request = (options, onResponse) => {
      assert.strictEqual(options.host,'192.168.1.20');
      assert.strictEqual(options.path,'/api');
      const request = new EventEmitter();
      request.setTimeout = () => {};
      request.destroy = () => {};
      request.end = body => {
        requestBody = JSON.parse(body);
        const response = new EventEmitter();
        response.statusCode = 200;
        response.socket = {getPeerCertificate:() => ({raw:Buffer.from('test certificate')})};
        onResponse(response);
        process.nextTick(() => { response.emit('data',Buffer.from(success));response.emit('end'); });
      };
      return request;
    };
    const result = await new Promise((resolve,reject) => pairing.pair('192.168.1.20',(error,value) => error ? reject(error) : resolve(value)));
    assert.strictEqual(requestBody.generateclientkey,true);
    assert.strictEqual(result.host,'192.168.1.20');
    assert.strictEqual(result.clientkey,'b'.repeat(32));
    assert.strictEqual(result.certificate_sha256,require('crypto').createHash('sha256').update('test certificate').digest('hex'));
  } finally { https.request = original; }
  console.log('Pairing tests passed: address validation, link-button errors, client key and certificate capture.');
}
run().catch(error => { console.error(error);process.exitCode=1; });
