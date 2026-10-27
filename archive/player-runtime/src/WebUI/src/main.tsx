import { lazy, Suspense } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';

// Separate entry components ensure Player does not load a diagnostic API client.
const Page = location.pathname === '/dev' ? lazy(() => import('./DevPage')) : lazy(() => import('./PlayerPage'));
createRoot(document.getElementById('root')!).render(<Suspense fallback={<p>正在加载…</p>}><Page /></Suspense>);
