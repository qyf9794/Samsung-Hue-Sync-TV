(function () {
  'use strict';
  var service = 'HueSyncTV1.service', base = 'http://127.0.0.1:18765';
  var status = document.getElementById('status'), detail = document.getElementById('detail');
  var areas = [], current = 0, attempts = 0, areaName = document.getElementById('area');
  var sensitivity = 3, sensitivityNames = {'1':'低','3':'标准','5':'高'}, currentState = 'offline', pairing = false, pendingConfig = null, coreReady = false;
  function showSensitivity() { document.getElementById('sensitivity').textContent = sensitivityNames[sensitivity]; }
  function request(method, path, callback, payload) {
    var xhr = new XMLHttpRequest();
    xhr.open(method, base + path, true);
    xhr.setRequestHeader('X-Hue-Control', window.HUE_CONTROL_TOKEN);
    if (payload) xhr.setRequestHeader('Content-Type', 'application/json');
    xhr.timeout = path === '/pair' ? 12000 : 2500;
    xhr.onload = function () {
      try { callback(xhr.status === 200 ? JSON.parse(xhr.responseText) : {state:'error', detail:'服务请求失败'}); }
      catch (e) { callback({state:'error', detail:'服务响应无效'}); }
    };
    xhr.onerror = xhr.ontimeout = function () { callback({state:'offline', detail:'同步服务未连接'}); };
    xhr.send(payload ? JSON.stringify(payload) : null);
  }
  function render(data) {
    var labels = {idle:'已停止', starting:'正在启动', running:'同步中', stopping:'正在停止', error:'无法同步', offline:'服务未连接'};
    currentState = data.state;
    status.textContent = labels[data.state] || '状态未知';
    detail.textContent = data.detail || '';
  }
  var localPort, pending = {}, nextRequest = 0;
  function initializePort() {
    if (localPort) return;
    localPort = tizen.messageport.requestTrustedLocalMessagePort('hue-ui');
    localPort.addMessagePortListener(function (items) {
      var result = {}; items.forEach(function (item) { result[item.key] = item.value; });
      coreReady = !(result.detail && result.detail.indexOf('控制令牌') !== -1);
      var level = Number(result.sensitivity);
      if (sensitivityNames[level]) { sensitivity = level; showSensitivity(); }
      if (pending[result.id]) { var item = pending[result.id];clearTimeout(item.timer);delete pending[result.id];render(result);if (item.callback) item.callback(result); }
    });
  }
  function engine(command, area, level, callback, configuration) {
    try {
      initializePort();
      var id = String(++nextRequest), remote = tizen.messageport.requestTrustedRemoteMessagePort('HueEngine1.service', 'hue-engine');
      pending[id] = {timer:setTimeout(function () { delete pending[id];coreReady=false;var failure={state:'offline', detail:'同步核心无响应'};render(failure);if(callback) callback(failure); }, 2500),callback:callback};
      remote.sendMessage([{key:'id',value:id},{key:'token',value:window.HUE_CONTROL_TOKEN},{key:'command',value:command},{key:'area',value:area || ''},{key:'level',value:level ? String(level) : ''},{key:'configuration',value:configuration ? JSON.stringify(configuration) : ''}], localPort);
    } catch (e) { coreReady=false;var failure={state:'offline', detail:'同步核心未连接：' + (e.name || '未知错误')};render(failure);if(callback) callback(failure); }
  }
  function refresh() { if (!pairing) engine('status'); }
  function send(command, area) { engine(command, area); setTimeout(refresh, 800); }
  function showArea() { areaName.textContent = areas.length ? areas[current].name : '没有可用区域'; }
  function loadAreas() {
    request('GET', '/areas', function (r) {
      if (Array.isArray(r.areas) && r.areas.length) {
        areas = r.areas;
        var saved = ''; try { saved = localStorage.getItem('hueArea') || ''; } catch (e) {}
        current = 0;
        areas.forEach(function (a, i) { if ((saved && a.id === saved) || (!saved && a.name === 'TV area')) current = i; });
        showArea();
      } else if (attempts++ < 8) { setTimeout(loadAreas, 1500); }
      else { areaName.textContent = '读取失败'; render(r); }
    });
  }
  function step(n) {
    if (!areas.length) { attempts = 0; loadAreas(); return; }
    current = (current + n + areas.length) % areas.length;
    try { localStorage.setItem('hueArea', areas[current].id); } catch (e) {}
    showArea();
  }
  document.getElementById('previous').onclick = function () { step(-1); };
  document.getElementById('next').onclick = function () { step(1); };
  document.getElementById('less').onclick = function () { engine('sensitivity', '', Math.max(1, sensitivity - 2)); };
  document.getElementById('more').onclick = function () { engine('sensitivity', '', Math.min(5, sensitivity + 2)); };
  document.getElementById('start').onclick = function () { if (areas.length) send('start', areas[current].id); else { attempts=0; loadAreas(); } };
  document.getElementById('stop').onclick = function () { send('stop'); };
  function savePairing(config) {
    engine('configure','',0,function (result) {
      if (result.state !== 'idle' || result.detail !== 'Bridge 已配对') { pairing=false;render({state:'error',detail:'核心未保存配对信息，按配对按钮可重试'});return; }
      request('POST','/bridge-config',function (saved) {
        pairing=false;
        if (!saved.saved) { render({state:'error',detail:'界面未保存配对信息，按配对按钮可重试'});return; }
        pendingConfig=null;document.getElementById('pair').textContent='配对 Bridge';areas=[];attempts=0;render({state:'idle',detail:'Bridge 配对成功'});loadAreas();
      },config);
    },config);
  }
  document.getElementById('pair').onclick = function () {
    if (pairing) return;
    if (!coreReady) { detail.textContent='请等待同步核心连接后再配对';return; }
    if (currentState === 'running' || currentState === 'starting' || currentState === 'stopping') { detail.textContent='请先停止同步，再配对 Bridge';return; }
    pairing=true;
    if (pendingConfig) { savePairing(pendingConfig);return; }
    var host=document.getElementById('bridge-ip').value.trim();
    render({state:'starting',detail:'正在配对 Bridge…'});
    request('POST','/pair',function (result) {
      if (!result.configuration) { pairing=false;render({state:'error',detail:result.error || result.detail || '配对失败'});return; }
      pendingConfig=result.configuration;document.getElementById('pair').textContent='继续配对';savePairing(pendingConfig);
    },{host:host});
  };
  var bridgeInput = document.getElementById('bridge-ip'), lastInputBlur = 0;
  bridgeInput.addEventListener('blur', function () { lastInputBlur=Date.now(); });
  document.addEventListener('keydown', function (event) {
    var active=document.activeElement, editing=active===bridgeInput || event.target===bridgeInput;
    var justLeftInput=lastInputBlur>0 && Date.now()-lastInputBlur<800;
    // Samsung IME uses Done 65376 and Cancel 65385. Leave arrows to the IME while editing.
    if ((event.keyCode===65376 || event.keyCode===65385 || event.keyCode===10009) && (editing || justLeftInput)) {
      if (editing) bridgeInput.blur();
      lastInputBlur=Date.now();
      document.getElementById('pair').focus();
      return;
    }
    if (editing) return;
    if (event.keyCode === 37 || event.keyCode === 39) {
      var order=['previous','next','less','more','bridge-ip','pair','start','stop'], idx=order.indexOf(document.activeElement.id), delta=event.keyCode===37?-1:1;
      document.getElementById(order[(idx+delta+order.length)%order.length]).focus();
      event.preventDefault();
    } else if (event.keyCode === 38 || event.keyCode === 40) {
      var vertical={previous:['previous','less'],next:['next','more'],less:['previous','bridge-ip'],more:['next','pair'],pair:['more','stop'],start:['pair','start'],stop:['pair','stop']};
      var targets=vertical[active && active.id];
      if (targets) { document.getElementById(targets[event.keyCode===38?0:1]).focus();event.preventDefault(); }
    } else if (event.keyCode === 10009) { try { tizen.application.getCurrentApplication().exit(); } catch (e) {} }
  });
  try {
    tizen.application.launchAppControl(new tizen.ApplicationControl('http://tizen.org/appcontrol/operation/pick'), service, function () { setTimeout(refresh, 600); setTimeout(loadAreas, 800); }, function () { render({state:'offline', detail:'无法启动同步服务'}); });
    tizen.application.launchAppControl(new tizen.ApplicationControl('http://tizen.org/appcontrol/operation/pick'), 'HueEngine1.service', function () { setTimeout(refresh, 1500); }, function () { render({state:'offline', detail:'无法启动同步核心'}); });
  } catch (e) { render({state:'offline', detail:'请在电视上运行此应用'}); }
  function loadPendingPair(attempt) {
    if (pairing || pendingConfig) return;
    request('GET','/pending-pair',function (result) {
      if (pairing || pendingConfig) return;
      if (result.configuration) { pendingConfig=result.configuration;document.getElementById('bridge-ip').value=pendingConfig.host;document.getElementById('pair').textContent='继续配对';detail.textContent='上次配对未完成，请选择“继续配对”'; }
      else if (result.state === 'offline' && attempt < 5) setTimeout(function () { loadPendingPair(attempt+1); },1500);
    });
  }
  setTimeout(function () { loadPendingPair(0); },1000);
  setInterval(refresh, 3000);
}());
