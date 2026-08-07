// Minimal DOM/browser shim to smoke-test that core.js + repos.js + search.js
// load without throwing and register their views against the App contract.
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'src', 'McpPrivateLibrary', 'wwwroot');

// ---- tiny fake DOM -------------------------------------------------------
function makeEl(tag) {
  return {
    tagName: (tag || 'div').toUpperCase(),
    children: [], attributes: {}, style: {}, classList: {
      _s: new Set(),
      add(...c){c.forEach(x=>this._s.add(x));}, remove(...c){c.forEach(x=>this._s.delete(x));},
      toggle(c,on){ if(on===undefined) on=!this._s.has(c); on?this._s.add(c):this._s.delete(c); return on;},
      contains(c){return this._s.has(c);}
    },
    _html: '', hidden: false, _listeners: {},
    set innerHTML(v){ this._html = v; }, get innerHTML(){ return this._html; },
    setAttribute(k,v){ this.attributes[k]=v; }, getAttribute(k){ return this.attributes[k] ?? null; },
    removeAttribute(k){ delete this.attributes[k]; },
    addEventListener(t,fn){ (this._listeners[t]=this._listeners[t]||[]).push(fn); },
    appendChild(c){ this.children.push(c); return c; },
    querySelector(){ return makeEl('div'); },
    querySelectorAll(){ return []; },
    closest(){ return null; },
    focus(){}, scrollIntoView(){},
  };
}

const els = {};
['view-home','view-repos','view-search','view-keys'].forEach(id => { els[id] = makeEl('section'); });

global.window = {
  location: { hash: '' },
  addEventListener: () => {},
  App: undefined,
};
global.document = {
  readyState: 'complete',
  getElementById: (id) => els[id] || null,
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: makeEl,
  body: makeEl('body'),
};
global.console = console;
global.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve([]) });

function load(rel) {
  const code = fs.readFileSync(path.join(root, rel), 'utf8');
  // eslint-disable-next-line no-eval
  (0, eval)(code);
}

let ok = true;
try {
  load('js/core.js');
  if (!global.window.App) throw new Error('core.js did not define window.App');
  const api = global.window.App;
  ['fetchJson','esc','pct','setAlert','fmtTime','badgeState','snippet','onView','navigate','views']
    .forEach(k => { if (typeof api[k] === 'undefined') throw new Error('App.'+k+' missing'); });

  load('js/repos.js');
  load('js/search.js');
  load('js/keys.js');

  // Drive the router: activate each view; handlers should run without throwing.
  ['home','repos','search','keys'].forEach(name => {
    global.window.location.hash = '#/' + name;
    // core.js registers hashchange on window.addEventListener (noop here), so call navigate:
    api.navigate(name);
  });

  console.log('OK: core + repos + search + keys loaded; views registered:', Object.keys(api.views).join(', '));
} catch (e) {
  ok = false;
  console.error('FAIL:', e.message);
}
process.exit(ok ? 0 : 1);
