import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'

// Fonts ship with the bundle. A privacy proxy's dashboard does not phone a
// font CDN, and the wall display may well be offline.
import '@fontsource-variable/archivo/wdth.css'
import '@fontsource/ibm-plex-mono/400.css'
import '@fontsource/ibm-plex-mono/500.css'
import '@fontsource/ibm-plex-mono/600.css'
import '@fontsource/ibm-plex-sans/400.css'
import '@fontsource/ibm-plex-sans/500.css'
import '@fontsource/ibm-plex-sans/600.css'

import './styles/tokens.css'
import './styles/base.css'
import './styles/layout.css'
import './styles/header.css'
import './styles/band.css'
import './styles/traffic.css'
import './styles/inspector.css'
import './styles/vault.css'
import './styles/guide.css'
import './styles/setup.css'

const container = document.getElementById('root')
if (!container) throw new Error('missing #root')

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
