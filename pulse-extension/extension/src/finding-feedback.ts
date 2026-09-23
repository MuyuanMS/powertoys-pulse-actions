import { completedV3Findings } from './details-model.js';
import { readDraft, resultDraftScope, writeDraft } from './draft-store.js';
import type { DraftStorage } from './draft-store.js';
import { matchingReview, suggestionOriginal } from './review.js';
import { element } from './ui.js';
import type { OperationPreview, ResultFindingV3, Run, Suggestion } from './types.js';

type Location = Pick<Suggestion, 'id' | 'path' | 'line' | 'startLine' | 'side'>;
interface Draft { selected: boolean; inline: boolean; body: string; replacement?: string; location?: Location; original?: string }
interface Controls { selected: HTMLInputElement; inline?: HTMLInputElement; body: HTMLTextAreaElement; replacement?: HTMLTextAreaElement; note: HTMLElement; original?: HTMLElement; deletion?: HTMLElement }

function location(source: Suggestion): Location {
  return { id: source.id, path: source.path, line: source.line, startLine: source.startLine, side: source.side };
}
function sameLocation(left: Location, right: Suggestion): boolean {
  return left.id === right.id && left.path === right.path && left.line === right.line && left.startLine === right.startLine && left.side === right.side;
}
function restoredDrafts(value: unknown): Map<string, Draft> {
  const restored = new Map<string, Draft>();
  if (!Array.isArray(value)) return restored;
  for (const entry of value) {
    if (!Array.isArray(entry) || entry.length !== 2 || typeof entry[0] !== 'string' || !entry[1] || typeof entry[1] !== 'object') continue;
    const saved = entry[1] as Partial<Draft>;
    if (typeof saved.selected !== 'boolean' || typeof saved.inline !== 'boolean' || typeof saved.body !== 'string') continue;
    if (saved.replacement !== undefined && typeof saved.replacement !== 'string' || saved.original !== undefined && typeof saved.original !== 'string') continue;
    const anchor = saved.location;
    if (anchor !== undefined && (!anchor || typeof anchor.path !== 'string' || !Number.isSafeInteger(anchor.line) || anchor.side !== 'RIGHT' ||
      anchor.startLine !== undefined && !Number.isSafeInteger(anchor.startLine) || anchor.id !== undefined && typeof anchor.id !== 'string')) continue;
    restored.set(entry[0], { selected: saved.selected, inline: saved.inline, body: saved.body, replacement: saved.replacement, location: anchor, original: saved.original });
  }
  return restored;
}

/** Selection and user edits are separate from report data and from the chosen review decision. */
export function findingFeedbackController(changed: () => void, storage?: DraftStorage) {
  let run: Run | undefined; let preview: OperationPreview | undefined; let scope = ''; let locallySaved = false;
  let drafts = new Map<string, Draft>(); const controls = new Map<string, Controls>();
  const contexts = new Map<string, Map<string, Draft>>();
  const findings = (): ResultFindingV3[] => run ? completedV3Findings(run) : [];
  const persist = (): void => { if (scope) locallySaved = writeDraft(scope, [...drafts], storage); };
  const edited = (): void => { persist(); changed(); };
  const suggestion = (finding: ResultFindingV3): Suggestion | undefined => {
    if (!run || !preview || !finding.feedback.suggestionId) return undefined;
    const review = matchingReview(run.result, run.task.expectedHeadSha, preview);
    const source = review?.suggestions.find(item => item.id === finding.feedback.suggestionId);
    const saved = drafts.get(finding.id);
    return source && saved?.location && sameLocation(saved.location, source) && suggestionOriginal(preview, source) !== undefined ? source : undefined;
  };
  const sourceSuggestion = (finding: ResultFindingV3): Suggestion | undefined => run?.result?.review?.suggestions.find(item => item.id === finding.feedback.suggestionId);
  const state = (finding: ResultFindingV3): Draft => {
    let saved = drafts.get(finding.id);
    if (!saved) {
      const source = sourceSuggestion(finding);
      saved = { selected: finding.confirmed, inline: false, body: finding.feedback.body,
        replacement: typeof source?.replacement === 'string' ? source.replacement : undefined, location: source ? location(source) : undefined };
      drafts.set(finding.id, saved);
    }
    return saved;
  };
  const update = (finding: ResultFindingV3): void => {
    const nodes = controls.get(finding.id); if (!nodes) return;
    const saved = state(finding); const available = suggestion(finding);
    nodes.selected.checked = saved.selected; nodes.body.disabled = !saved.selected;
    if (nodes.inline) { nodes.inline.checked = saved.inline; nodes.inline.disabled = !saved.selected || (!available && !saved.inline); }
    if (nodes.replacement) nodes.replacement.disabled = !saved.selected || !saved.inline;
    if (nodes.original) nodes.original.textContent = saved.original ?? (available && preview ? suggestionOriginal(preview, available) : undefined) ?? 'Load the target to inspect the exact original lines.';
    if (nodes.deletion) nodes.deletion.hidden = !saved.inline || saved.replacement !== '';
    nodes.note.textContent = saved.inline && !available ? 'The saved inline position is unavailable. Your draft is retained. Choose ordinary comment or reload the target before submitting.'
      : saved.inline && saved.replacement === undefined ? 'Review the original lines and enter replacement code. An unknown replacement is not a deletion.'
        : saved.inline ? 'This finding will be sent as an inline code suggestion. Review the original lines and replacement.'
          : 'This finding will be sent as an ordinary comment. Code suggestions are optional and require an explicit choice.';
  };
  return {
    setContext(value: Run, target?: OperationPreview): void {
      const nextScope = resultDraftScope(value, 'finding-feedback');
      if (scope !== nextScope) {
        scope = nextScope; controls.clear();
        drafts = contexts.get(scope) ?? restoredDrafts(readDraft(scope, storage)); contexts.set(scope, drafts);
      }
      run = value; preview = target;
      for (const finding of findings()) { state(finding); update(finding); }
      persist();
    },
    render(finding: ResultFindingV3): HTMLElement {
      const saved = state(finding); const source = sourceSuggestion(finding);
      const wrapper = element('section', undefined, 'finding-feedback'); wrapper.dataset.findingId = finding.id;
      const label = element('label', undefined, 'checkbox'); const selected = element('input'); selected.type = 'checkbox'; selected.checked = saved.selected;
      label.append(selected, element('span', 'Include in feedback')); wrapper.append(label);
      const editor = element('details'); editor.append(element('summary', 'Edit feedback'));
      const bodyLabel = element('label', 'Comment'); const body = element('textarea'); body.rows = 4; body.value = saved.body; bodyLabel.append(body); editor.append(bodyLabel);
      let inline: HTMLInputElement | undefined; let replacement: HTMLTextAreaElement | undefined;
      if (source || saved.location) {
        const inlineLabel = element('label', undefined, 'checkbox'); inline = element('input'); inline.type = 'checkbox'; inline.checked = saved.inline;
        inlineLabel.append(inline, element('span', 'Include the code suggestion')); editor.append(inlineLabel);
        const replacementLabel = element('label', 'Replacement code'); replacement = element('textarea'); replacement.rows = 4; replacement.value = saved.replacement ?? ''; replacementLabel.append(replacement); editor.append(replacementLabel);
        inline.addEventListener('change', () => {
          saved.inline = inline!.checked;
          const available = suggestion(finding);
          if (saved.inline && available && preview) {
            saved.original = suggestionOriginal(preview, available);
            // Only a missing value is initialized. An explicitly saved empty value means deletion.
            if (saved.replacement === undefined) saved.replacement = typeof available.replacement === 'string' ? available.replacement : saved.original;
            replacement!.value = saved.replacement ?? '';
          }
          update(finding); edited();
        });
        replacement.addEventListener('input', () => { saved.replacement = replacement!.value; update(finding); edited(); });
      }
      let original: HTMLElement | undefined; let deletion: HTMLElement | undefined;
      if (source || saved.location) {
        const anchor = saved.location ?? location(source!); const originalSection = element('details');
        originalSection.append(element('summary', `Original lines · ${anchor.path}:${anchor.startLine ?? anchor.line}${anchor.startLine && anchor.startLine !== anchor.line ? `–${anchor.line}` : ''}`));
        original = element('pre', undefined, 'log'); originalSection.append(original); editor.append(originalSection);
        deletion = element('p', 'The empty replacement deletes the displayed original lines.', 'notice'); deletion.hidden = true; editor.append(deletion);
      }
      const note = element('p', undefined, 'muted fine'); editor.append(note); wrapper.append(editor);
      selected.addEventListener('change', () => { saved.selected = selected.checked; update(finding); edited(); });
      body.addEventListener('input', () => { saved.body = body.value; edited(); });
      controls.set(finding.id, { selected, inline, body, replacement, note, original, deletion }); update(finding); return wrapper;
    },
    compose(): { findingIds: string[]; body: string; suggestions: Suggestion[]; errors: string[] } {
      const findingIds: string[] = []; const ordinary: string[] = []; const suggestions: Suggestion[] = []; const errors: string[] = [];
      for (const finding of findings()) {
        const saved = state(finding); if (!saved.selected) continue;
        findingIds.push(finding.id);
        if (saved.inline) {
          const source = suggestion(finding);
          if (!source) { errors.push(`The inline position for “${finding.title}” is unavailable. Choose its ordinary comment before submitting.`); continue; }
          if (saved.replacement === undefined) { errors.push(`Review and enter replacement code for “${finding.title}” before submitting.`); continue; }
          suggestions.push({ ...source, body: saved.body, replacement: saved.replacement });
        } else if (saved.body.trim()) ordinary.push(saved.body);
        else errors.push(`Enter a comment for “${finding.title}” or deselect the finding.`);
      }
      return { findingIds, body: ordinary.join('\n\n'), suggestions, errors };
    },
    persistence(): 'local' | 'session' { return locallySaved ? 'local' : 'session'; },
    renderSelection(container: HTMLElement): void {
      container.replaceChildren(); const value = this.compose(); container.hidden = !value.findingIds.length;
      if (!value.findingIds.length) return;
      container.append(element('h4', `Selected feedback (${value.findingIds.length})`));
      if (value.body) container.append(element('pre', value.body, 'prewrap'));
      for (const item of value.suggestions) {
        const row = element('details'); row.append(element('summary', `${item.path}:${item.startLine ?? item.line}${item.startLine && item.startLine !== item.line ? `–${item.line}` : ''}`), element('pre', item.body, 'prewrap'), element('pre', item.replacement, 'log'));
        container.append(row);
      }
      for (const error of value.errors) container.append(element('p', error, 'notice'));
    },
  };
}
