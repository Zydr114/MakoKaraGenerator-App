import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { installTheme } from './theme/theme-store';
import { installMdui } from './ui/mdui';
import { App } from './App';
import './theme/tokens.css';
import './theme/base.css';
import './editor.css';

installMdui(installTheme());

const root = document.getElementById('root');
if (!root) throw new Error('找不到 #root 挂载点。');

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
