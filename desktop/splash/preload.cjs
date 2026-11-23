'use strict';

const { contextBridge, ipcRenderer } = require('electron');

let latestState = null;
const listeners = new Set();

ipcRenderer.on('mako-splash:state', (_event, state) => {
  latestState = state;
  for (const listener of listeners) {
    try {
      listener(state);
    } catch { /* ignore listener errors in isolated renderer */ }
  }
});

contextBridge.exposeInMainWorld(
  'makoSplash',
  Object.freeze({
    onState(listener) {
      if (typeof listener !== 'function') throw new TypeError('listener required');
      listeners.add(listener);
      if (latestState) {
        try {
          listener(latestState);
        } catch { /* ignore listener errors in isolated renderer */ }
      }
      return () => {
        listeners.delete(listener);
      };
    },
    getInitialState() {
      return latestState;
    },
  }),
);
