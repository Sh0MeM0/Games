const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('desktop', Object.freeze({
  beginExport: () => ipcRenderer.invoke('export:begin'),
  writeImage: (token, name, bytes) => ipcRenderer.invoke('export:write', { token, name, bytes }),
  endExport: token => ipcRenderer.invoke('export:end', { token }),
  openOutput: () => ipcRenderer.invoke('export:open')
}));
