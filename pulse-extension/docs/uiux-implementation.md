# UI/UX implementation and local acceptance · 2026-09-17

## Action popup sizing correction · 2026-09-20

The compact layout introduced viewport-based root bounds (`100vw` and `100dvh`). In a 40 × 40 initial frame, the previous rules measured the body as 40 × 40 and clipped the footer. The earlier 480px/360px webpage checks did not test Chrome's action-popup auto-sizing; they must not be read as native popup acceptance.

The native compact HTML/body now establish an intrinsic 480px width and a 360–600px height range before the task UI loads. Only the visibly labelled ordinary webpage preview follows its viewport. Content still scrolls inside the popup while the brand and footer retain space. Expanded Tasks pages are outside these rules.

The local `popup-sizing.html` fixture loads the real popup inside 25 × 25, 40 × 40, and normal-size frames, with the preview banner omitted but the connection explicitly labelled UI sample. It checks intrinsic dimensions, usable content height, and the footer bounds. The corrected 25px/40px cases measured 480 × 600 with a 479px content area; at 480 × 600 only the content needed scrolling and the footer remained visible. This is a controlled browser layout regression, not a direct test of the installed Chrome toolbar popup. Native Chrome first-open/OS-scale verification remains unestablished by the available browser surface.

## Popup refinement · 2026-09-20

The toolbar popup follows the approved compact design: no update timestamp or top action toolbar, connection status beside the brand, inline Tasks/counts, a one-line idle state, and two recent result previews. Open extension and Refresh share the bottom action group; Open Pulse stays a secondary link. The bottom bar remains visible while longer lists scroll. Settings and complete filtering remain in the expanded extension page.

View all opens finished history in a new tab. Its numeric label is used only when the existing Host returned a complete, readable history snapshot; a paginated or partial snapshot does not establish a total. This includes unlinked historical records instead of silently summing only PR and Issue counts. Initial loading keeps unknown counts, and a failed refresh preserves prior records with an explicit Last known state.

Validation for this refinement: 340 extension source tests, TypeScript checking and production/development builds passed. Local browser fixtures verified the 480px and 360px layouts, content-only scrolling with a visible footer, one-line idle state, two recent rows, Refresh, new-tab extension/history entry, and retained expanded-page controls. The preview now models `chrome.tabs.create` with a real local tab and labels its compact connection as sample data. No real task, GitHub write, PowerToys build or Host reinstall was performed.

This records the plugin implementation of the approved UI/UX design. The browser checks below used compiled product source pages. The larger design gallery is a separate local review artifact, not a runtime dependency.

## Implemented behavior

| Surface | Result |
| --- | --- |
| All result families | Conclusion, recommended next step, prepared content, shared primary/More actions footer, and expandable evidence. No recommendation is explicit. Workflow completion is separate from assessment. Partial or unread reports cannot establish zero findings. |
| PR feedback | All confirmed findings initially selected as ordinary feedback. Code suggestions require an explicit choice. Selection, text, mode, location and replacement drafts persist per run/target/revision. Independent manual comments have separate drafts. A final confirmation shows exact account, target, SHA, body and suggestions before sending. |
| Candidate / Draft PR | Verified Issue candidate enters Create Draft PR. An editable source workspace preserves title/body; preparation fixes source/base/SHA and forces Draft in the Host. Prepared payloads remain immutable. Unknown actions recover the same operation. |
| Verification and saved plans | Review saved requirements first, then acknowledge scope/environment and Start verification. Original target/SHA/scenarios and pending-request identity remain bound. Opening preparation starts no task. |
| Tasks | All/PR/Issue target filters, task-type/search, stable rows, separate read/handled status, honest historical targetless records, and disconnected-state preservation. |
| New-task admission | Frontend and Host require a correctly typed target. Historical parsing, exact request replay, lookup, conflict detection, index recovery and deletion tombstones remain compatible. |
| Settings | Grouped overview/editing, all-field dirty status, keep/discard on reload, acknowledged/unknown save handling, retained GitHub selection after scan failure, prompt preview race protection. |

## Confirmed validation

- Extension source suite: **316/316 passed**. The final recommendation-model and activity-anchor adjustments passed their focused **38/38** suite afterward.
- Extension TypeScript compilation to `.tmp/extension-ui`: passed.
- Host final Release build: **0 warnings, 0 errors**. Host automated executable: **all scenarios passed**, including targetless lookup compatibility and fixed-Draft edited-content identity.
- Native Messaging smoke suite: **10/10 passed**, using the compiled Host and isolated test records.

Browser interaction was performed through the Codex in-app browser. The extension preview disables Host/model/GitHub writes by default and labels all session fixtures.

- Ten representative result families checked at **390 × 844** after loading: feedback, no recommendation, missing/ready verification prerequisites, Draft candidate, Issue information request, incomplete analysis, report-page failure/reload, unknown action and historical output. No horizontal overflow or duplicate IDs observed.
- Feedback: deselecting a finding changed the complete preview; manual comment started blank; returning and reloading preserved the original draft and selection; Back from final confirmation did not submit.
- Verification: first click opened saved full SHA/scenarios/expected results/prerequisites; Start was disabled until acknowledgment.
- Draft: title/body survived reload; explicit preparation produced frozen content; a session-only simulated creation displayed the completion receipt.
- Report failure remained readable until explicit reload; reloading restored all **26** fixture findings.
- Settings: dirty edits survived Keep editing; session-only save produced an acknowledgment. Tasks retained only All/PR/Issue target choices.
- Task activity links opened the correct new browser tab, expanded the evidence container and focused the execution log.

## Scope of this evidence

This is product-source UI testing with fixtures, plus offline protocol/Host tests. It is not acceptance of an installed Chrome/Edge extension, a real model task, a GitHub write, or the actual CJK PowerToys runtime case. Those actions were not performed.

The existing publication protocol creates a Draft PR from an **existing remote branch**. It does not commit, push or create a fork for unpublished local files. The Draft workspace names that requirement and retains the user's edits; no publication success is fabricated.

Task search is explicitly over the loaded runs; it does not claim a new Host-wide recovery index. Historical records retain their original identity. The Host suite also records its existing `BACKGROUND_JOB_RESTRICTED` environment limit: full job escape was not verified, and those fixtures used the supervised test launcher.

## Local review

Use the [local validation and preview commands](../README.md#local-validation-and-ui-preview) to serve `http://127.0.0.1:4186/popup.html?expanded=1`. The fixture selector covers the result states above.

The preview binds only to 127.0.0.1. Logs and generated outputs belong in ignored temporary directories. Browser viewport overrides were reset after acceptance. Fixture settings and drafts do not alter installed extension settings.
