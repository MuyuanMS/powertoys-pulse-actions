# PowerToys Pulse visual baseline

The extension follows the Primer light style in `powertoys-pulse`, particularly `src/app/providers.tsx`, `src/components/AppShell.tsx`, `PrsDashboard.tsx`, and `triage/TriageAction.tsx`. It does not import the React/Primer runtime.

- Segoe UI system typography, a white sidebar, and a light gray workspace; the desktop sidebar is 260px wide.
- The PowerToys logo is reused from `public/powertoys-logo.png` and shown at 24px. PowerToys is dark gray; Pulse is accent blue.
- Primer light colors: foreground `#1f2328`, muted `#59636e`, accent `#0969da`, background `#f6f8fa`, and border `#d1d9e0`.
- Cards use 8px corners and 1px borders. Small buttons and form fields use 6px corners. Selected tabs use a light blue background.
- Tasks, Pull requests, Issues, details, and settings share the same navigation and page structure. Narrow windows hide the sidebar and retain a compact brand bar and page navigation.
- Preview banners and simulated-action labels appear only in the local preview.

Navigation is created by `src/ui.ts`. Design variables and component styles are in `public/styles.css`.
