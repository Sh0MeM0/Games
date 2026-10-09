import { contextBridge, ipcRenderer } from 'electron';
import type { Action, EditStatus } from '../shared/types';

contextBridge.exposeInMainWorld('archive', {
  settings: () => ipcRenderer.invoke('settings'),
  login: (server: string, username: string, password: string) => ipcRenderer.invoke('login', server, username, password),
  logout: () => ipcRenderer.invoke('logout'),
  action: (data: Action) => ipcRenderer.invoke('action', data),
  edits: () => ipcRenderer.invoke('edits'),
  onEdits: (callback: (edits: EditStatus[]) => void) => {
    const listener = (_event: Electron.IpcRendererEvent, edits: EditStatus[]) => callback(edits);
    ipcRenderer.on('edits-changed', listener);
    return () => ipcRenderer.removeListener('edits-changed', listener);
  },
});
