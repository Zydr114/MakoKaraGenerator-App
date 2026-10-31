import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { installMdui } from './ui/mdui';
import { App } from './App';
import './editor.css';

installMdui();

const root = document.getElementById('root');
if (!root) throw new Error('找不到 #root 挂载点。');

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
