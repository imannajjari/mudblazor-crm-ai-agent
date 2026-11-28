window.crmAgentDriver = (function () {
    const elementMap = new Map();
    let activeRequests = 0;

    // Hook fetch to track in-flight HTTP requests for save settle
    if (!window.__crmAgentFetchHooked) {
        window.__crmAgentFetchHooked = true;
        const origFetch = window.fetch;
        window.fetch = function () {
            activeRequests++;
            try {
                return origFetch.apply(this, arguments).finally(() => {
                    activeRequests = Math.max(0, activeRequests - 1);
                });
            } catch (e) {
                activeRequests = Math.max(0, activeRequests - 1);
                throw e;
            }
        };
    }

    function isExcluded(el) {
        return !!(el && el.closest && el.closest('[data-agent-panel="true"]'));
    }

    function isVisible(el) {
        if (!el || isExcluded(el)) return false;
        const style = window.getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
        const rect = el.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0;
    }

    function cleanText(text) {
        return (text || '').replace(/\s+/g, ' ').trim();
    }

    // Semantic ids: derived from what the user sees, so a re-render keeps the same id for the
    // same control instead of shifting every positional f1/a1 after it.
    function slugify(text, max) {
        const s = cleanText(text).replace(/\u200c/g, '').toLowerCase()
            .replace(/[^\p{L}\p{N}]+/gu, '_')
            .replace(/^_+|_+$/g, '');
        return s.slice(0, max || 40).replace(/_+$/, '');
    }

    function claimId(base, used) {
        let id = base;
        for (let n = 2; used.has(id); n++) id = base + '_' + n;
        used.add(id);
        return id;
    }

    // The map is rebuilt by every snapshot, so an id in it is the one the model was shown. An id
    // that is missing or detached is not re-scanned into a different live element (a stale dialog
    // "Delete" must never land on the page behind it): the caller reports it and the model re-scans.
    function resolveElement(id) {
        const el = elementMap.get(id);
        return el && document.contains(el) ? el : null;
    }

    function classifyRisk(label) {
        const t = cleanText(label).toLowerCase();
        if (/\b(delete|remove|void)\b/.test(t) || t.includes('حذف')) {
            return 'destructive';
        }
        if (/\b(yes|confirm)\b/.test(t) || t === 'بله') return 'confirm';
        if (/\b(save|submit|add|create|update|ok)\b/.test(t) ||
            ['ذخیره', 'ثبت', 'افزودن', 'تایید', 'به‌روزرسانی', 'به روز رسانی'].some(k => t.includes(k))) {
            return 'save';
        }
        if (/\b(cancel|close|back|refresh|reload|search|filter|view|details|next|prev|previous)\b/.test(t) ||
            ['انصراف', 'بستن', 'بازگشت', 'جستجو', 'فیلتر', 'تازه‌سازی', 'بعدی', 'قبلی'].some(k => t.includes(k))) {
            return 'read';
        }
        return 'ask';
    }

    function sanitizeUrl(rawUrl) {
        try {
            const u = new URL(rawUrl, window.location.origin);
            const masked = new URLSearchParams();
            for (const [k, v] of u.searchParams.entries()) {
                masked.append(k, /^(token|code|secret|password|key|auth)$/i.test(k) ? '***' : v);
            }
            const qs = masked.toString();
            return u.pathname + (qs ? '?' + qs : '');
        } catch {
            return window.location.pathname;
        }
    }

    function getActiveRoot() {
        const dialogs = Array.from(document.querySelectorAll('.mud-dialog[role="dialog"]')).filter(isVisible);
        return (dialogs.length > 0 ? dialogs[dialogs.length - 1] : null)
            || document.querySelector('.mud-main-content')
            || document.body;
    }

    const SENSITIVE_LABEL_RE = /(password|token|secret|code|key|auth|رمز|عبور|کارت|شناسه)/i;
    function isSensitiveLabel(label) { return SENSITIVE_LABEL_RE.test(label || ''); }

    function captureSnapshot(maxRowsParam) {
        elementMap.clear();
        const maxRows = Math.min(50, Math.max(1, Number(maxRowsParam) || 15));
        const used = new Set();
        const dialogs = Array.from(document.querySelectorAll('.mud-dialog[role="dialog"]')).filter(isVisible);
        const topDialog = dialogs.length > 0 ? dialogs[dialogs.length - 1] : null;
        const root = topDialog || document.querySelector('.mud-main-content') || document.body;

        const headingEl = (topDialog ? topDialog.querySelector('.mud-dialog-title') : null)
            || root.querySelector('h1, h2, h3, .mud-typography-h3, .mud-typography-h4');

        const tabs = collectTabs(root, used, new Map());

        // Fields (inputs, selects, autocompletes, date pickers)
        const fields = [];
        let fieldIdx = 1;
        root.querySelectorAll('.mud-input-control').forEach(ctrl => {
            if (!isVisible(ctrl)) return;
            const input = ctrl.querySelector('input.mud-input-slot, textarea.mud-input-slot');
            if (!input) return;
            const labelEl = ctrl.querySelector('label.mud-input-label');
            const label = cleanText(labelEl ? labelEl.innerText : (input.getAttribute('placeholder') || input.getAttribute('aria-label') || ''));
            const fallback = () => 'field_' + (fieldIdx++);
            const id = claimId(slugify(input.getAttribute('name') || '') || slugify(label) || fallback(), used);
            const isSelect = !!ctrl.closest('.mud-select');
            const isAuto = !!ctrl.closest('.mud-autocomplete');
            const isDate = !!ctrl.closest('.mud-picker');
            elementMap.set(id, isSelect ? ctrl.closest('.mud-select') : (isAuto ? ctrl.closest('.mud-autocomplete') : input));
            const kind = isSelect ? 'select' : (isAuto ? 'autocomplete' : (isDate ? 'date' : (input.tagName === 'TEXTAREA' ? 'textarea' : (input.type || 'text'))));
            const isPassword = (input.type || '').toLowerCase() === 'password'
                || isSensitiveLabel(label) || isSensitiveLabel(input.getAttribute('name') || '') || isSensitiveLabel(id);
            fields.push({
                id,
                label: label || id,
                kind,
                value: isPassword ? '(hidden)' : (input.value || ''),
                maxLength: input.maxLength > 0 ? input.maxLength : null,
                required: input.hasAttribute('required') || ctrl.classList.contains('mud-input-required') || input.getAttribute('aria-required') === 'true' || label.includes('*'),
                invalid: ctrl.classList.contains('mud-input-error') || input.getAttribute('aria-invalid') === 'true',
                // MudSelect and non-editable MudDatePicker render a readonly inner input by design
                disabled: (isSelect || isDate) ? (ctrl.classList.contains('mud-disabled') || input.disabled) : (input.disabled || input.readOnly)
            });
        });

        // Checkboxes and Switches
        root.querySelectorAll('.mud-checkbox, .mud-switch').forEach(ctrl => {
            if (!isVisible(ctrl)) return;
            const input = ctrl.querySelector('input[type="checkbox"]');
            if (!input) return;
            const label = cleanText(ctrl.innerText || input.getAttribute('aria-label') || '');
            const fallback = () => 'field_' + (fieldIdx++);
            const id = claimId(slugify(input.getAttribute('name') || '') || slugify(label) || fallback(), used);
            elementMap.set(id, input);
            fields.push({
                id,
                label: label || id,
                kind: 'checkbox',
                value: input.checked ? 'true' : 'false',
                maxLength: null,
                required: input.hasAttribute('required') || input.getAttribute('aria-required') === 'true',
                invalid: false,
                disabled: input.disabled || ctrl.classList.contains('mud-disabled')
            });
        });

        // File inputs (exclude panel's own hidden attachment input)
        const fileInputs = [];
        let fileIdx = 1;
        root.querySelectorAll('input[type="file"]').forEach(input => {
            if (isExcluded(input)) return;
            const labelEl = input.id ? root.querySelector(`label[for="${input.id}"]`) : null;
            const label = cleanText((labelEl ? labelEl.innerText : '') || input.getAttribute('aria-label') || input.getAttribute('name') || input.id || 'File');
            const id = claimId('file_' + (slugify(label) || 'input' + (fileIdx++)), used);
            elementMap.set(id, input);
            fileInputs.push({
                id,
                label,
                accept: input.getAttribute('accept') || '',
                multiple: !!input.multiple,
                disabled: !!input.disabled
            });
        });

        // Grids
        const grids = [];
        let gridIdx = 1;
        root.querySelectorAll('.mud-table-root').forEach(table => {
            if (!isVisible(table)) return;
            const container = table.closest('.mud-table') || table.parentElement;
            const headers = Array.from(table.querySelectorAll('thead th'))
                .map(th => cleanText(th.innerText))
                .filter(Boolean);

            const sensitiveCol = headers.map(h => isSensitiveLabel(h));
            const allRows = Array.from(table.querySelectorAll('tbody tr'));
            const rows = allRows.slice(0, maxRows).map((tr, rIdx) => {
                const rawCells = Array.from(tr.querySelectorAll('td')).map(td => cleanText(td.innerText));
                const cells = rawCells.map((c, cIdx) => sensitiveCol[cIdx] ? '***' : c);
                const key = slugify(rawCells.find(Boolean) || '', 30) || 'r' + (rIdx + 1);
                const rowId = claimId('g' + gridIdx + '_' + key, used);
                elementMap.set(rowId, tr);
                return { rowId, cells };
            });

            const footerEl = container ? container.querySelector('.mud-table-pagination-caption') : null;
            grids.push({
                id: 'g' + (gridIdx++),
                headers,
                rows,
                rowsShown: rows.length,
                rowsCut: allRows.length > maxRows,
                footerCount: footerEl ? cleanText(footerEl.innerText) : null
            });
        });

        // Actions
        const actions = [];
        root.querySelectorAll('button, a.mud-button-root').forEach(btn => {
            if (!isVisible(btn)) return;
            if (btn.closest('.mud-tab')) return;
            const text = cleanText(btn.innerText || btn.getAttribute('title') || btn.getAttribute('aria-label') || '');
            if (!text) return;
            const prefix = btn.tagName === 'A' ? 'link_' : 'btn_';
            const id = claimId((btn.id || '').trim() || prefix + (slugify(text) || 'action'), used);
            elementMap.set(id, btn);
            actions.push({
                id,
                text,
                kind: btn.tagName === 'A' ? 'link' : 'button',
                risk: classifyRisk(text),
                disabled: btn.disabled || btn.classList.contains('mud-disabled')
            });
        });

        // Open combo options (if any popover is open)
        const lookupOptions = getOpenComboOptions();

        // Errors & Toasts
        const errors = Array.from(root.querySelectorAll('.mud-input-error .mud-input-helper-text, .validation-message'))
            .filter(isVisible)
            .map(el => cleanText(el.innerText))
            .filter(Boolean);

        const toasts = Array.from(document.querySelectorAll('.mud-snackbar .mud-snackbar-content-message'))
            .filter(isVisible)
            .map(el => cleanText(el.innerText))
            .filter(Boolean);

        const blocker = detectDeleteBlocker(root);

        return JSON.stringify({
            url: sanitizeUrl(window.location.href),
            title: cleanText(headingEl ? headingEl.innerText : document.title),
            inDialog: !!topDialog,
            dialogText: topDialog ? cleanText(topDialog.querySelector('.mud-dialog-content')?.innerText || '') : '',
            blocksDelete: blocker.blocksDelete,
            inUseSummary: blocker.inUseSummary,
            tabs,
            fields: fields.slice(0, 40),
            fileInputs: fileInputs.slice(0, 10),
            grids,
            actions: actions.slice(0, 40),
            lookupOptions,
            errors,
            toasts,
            navRoutes: getNavRouteList()
        });
    }

    function detectDeleteBlocker(root) {
        const candidates = Array.from(root.querySelectorAll('.mud-alert, .mud-dialog-content, .mud-error-text'))
            .filter(isVisible)
            .map(el => cleanText(el.innerText))
            .filter(Boolean);
        const blockerPattern = /\b(in use|cannot delete|cannot be deleted|referenced by|has related)\b|در حال استفاده|قابل حذف نیست|امکان حذف/i;
        const match = candidates.find(t => blockerPattern.test(t));
        return match ? { blocksDelete: true, inUseSummary: match.slice(0, 240) } : { blocksDelete: false, inUseSummary: null };
    }

    function getOpenComboOptions() {
        const popovers = Array.from(document.querySelectorAll('.mud-popover-open'))
            .filter(p => isVisible(p) && p.querySelector('.mud-list-item'));
        const options = [];
        popovers.forEach(p => {
            Array.from(p.querySelectorAll('.mud-list-item'))
                .filter(isVisible)
                .forEach(item => {
                    options.push({ index: options.length + 1, text: cleanText(item.innerText) });
                });
        });
        return options.slice(0, 30);
    }

    function captureVisibleText() {
        const root = getActiveRoot();
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
            acceptNode(node) {
                const parent = node.parentElement;
                if (!parent || !node.nodeValue || !node.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
                if (parent.closest('[data-agent-panel="true"], script, style, noscript')) return NodeFilter.FILTER_REJECT;
                if (!isVisible(parent)) return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
            }
        });
        const chunks = [];
        let total = 0;
        let n;
        while ((n = walker.nextNode())) {
            const s = cleanText(n.nodeValue);
            if (!s) continue;
            chunks.push(s);
            total += s.length + 1;
            if (total >= 30000) break;
        }
        return chunks.join('\n').slice(0, 30000);
    }

    function spotlightElement(el) {
        if (!el || typeof el.scrollIntoView !== 'function') return;
        try {
            el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
            const prevOutline = el.style.outline;
            const prevShadow = el.style.boxShadow;
            el.style.outline = '3px solid #594AE2';
            el.style.boxShadow = '0 0 12px rgba(89, 74, 226, 0.55)';
            setTimeout(() => {
                el.style.outline = prevOutline;
                el.style.boxShadow = prevShadow;
            }, 900);
        } catch { /* ignore visual highlight errors */ }
    }

    async function setField(id, value) {
        const el = resolveElement(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Field id not found in current view: ' + id });
        }

        const input = el.matches('input, textarea') ? el : el.querySelector('input.mud-input-slot, textarea.mud-input-slot');
        if (!input) {
            return JSON.stringify({ ok: false, error: 'Input element not found for id: ' + id });
        }
        const isDate = !!el.closest('.mud-picker') || !!input.closest('.mud-picker');
        const isAuto = el.classList?.contains('mud-autocomplete') || !!input.closest('.mud-autocomplete');
        if (input.disabled || (!isDate && input.readOnly)) {
            return JSON.stringify({ ok: false, error: 'Field is disabled or read-only: ' + id });
        }

        if (input.type === 'checkbox') {
            const norm = cleanText(String(value ?? '')).toLowerCase();
            const truthy = ['true', '1', 'yes', 'on', 'بله'].includes(norm);
            const falsy = ['false', '0', 'no', 'off', 'خیر'].includes(norm);
            if (!truthy && !falsy) {
                return JSON.stringify({ ok: false, error: `Invalid checkbox value '${value}'. Use true or false.` });
            }
            spotlightElement(input);
            if (input.checked !== truthy) {
                input.click();
                await waitForSettle(300);
            }
            return JSON.stringify({ ok: true, id, fieldValue: input.checked ? 'true' : 'false' });
        }

        const strVal = value == null ? '' : String(value);
        if (input.maxLength > 0 && strVal.length > input.maxLength) {
            return JSON.stringify({ ok: false, error: `Value exceeds maxLength (${input.maxLength}).` });
        }

        const isPassword = (input.type || '').toLowerCase() === 'password'
            || isSensitiveLabel(id) || isSensitiveLabel(input.name || '') || isSensitiveLabel(input.getAttribute('aria-label') || '');
        spotlightElement(input);
        input.focus();
        setNativeInputValue(input, strVal);

        if (isDate) {
            input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
            await new Promise(r => requestAnimationFrame(r));
            input.blur();
            await waitForSettle(400);
            return JSON.stringify({ ok: true, id, fieldValue: isPassword ? '(hidden)' : input.value });
        }

        if (isAuto) {
            await waitForSettle(400);
            const items = Array.from(document.querySelectorAll('.mud-popover-open .mud-list-item')).filter(isVisible);
            const q = cleanText(strVal).toLowerCase();
            const match = items.find(i => cleanText(i.innerText).toLowerCase() === q)
                || items.find(i => cleanText(i.innerText).toLowerCase().includes(q));
            if (match) {
                match.click();
                await waitForSettle(400);
            } else {
                input.blur();
                await waitForSettle(300);
            }
            return JSON.stringify({ ok: true, id, fieldValue: isPassword ? '(hidden)' : input.value });
        }

        await new Promise(r => requestAnimationFrame(r));
        input.blur();

        await waitForSettle(400);
        return JSON.stringify({ ok: true, id, fieldValue: isPassword ? '(hidden)' : input.value });
    }

    async function openCombo(id) {
        const el = resolveElement(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Combo id not found: ' + id });
        }
        const target = el.querySelector('.mud-input-slot') || el;
        target.click();
        await waitForSettle(500);
        const options = getOpenComboOptions();
        return JSON.stringify({ ok: true, id, lookupOptions: options });
    }

    async function pickOption(optionTextOrIndex, multiSelect, extraOptions) {
        const popovers = Array.from(document.querySelectorAll('.mud-popover-open')).filter(isVisible);
        const getVisibleItems = () => {
            const list = [];
            popovers.forEach(p => {
                p.querySelectorAll('.mud-list-item').forEach(item => {
                    if (isVisible(item)) list.push(item);
                });
            });
            return list;
        };

        const items = getVisibleItems();
        if (items.length === 0) {
            return JSON.stringify({ ok: false, error: 'No open combo options found. Call open_combo first.' });
        }

        const targets = Array.isArray(extraOptions) && extraOptions.length > 0
            ? extraOptions
            : [optionTextOrIndex];
        const picked = [];

        for (const rawTarget of targets) {
            const currentItems = getVisibleItems();
            const q = cleanText(String(rawTarget ?? '')).toLowerCase();
            if (!q) continue;
            let match = currentItems.find(i => cleanText(i.innerText).toLowerCase() === q);
            if (!match && (typeof rawTarget === 'number' || /^\d+$/.test(String(rawTarget)))) {
                const idx = Number(rawTarget) - 1;
                if (idx >= 0 && idx < currentItems.length) match = currentItems[idx];
            }
            if (!match) match = currentItems.find(i => cleanText(i.innerText).toLowerCase().includes(q));
            if (!match) {
                return JSON.stringify({ ok: false, error: 'Option not found: ' + rawTarget, picked });
            }
            picked.push(cleanText(match.innerText));
            match.click();
            await waitForSettle(350);
        }

        if (picked.length === 0) {
            return JSON.stringify({ ok: false, error: 'Option text or index is required.' });
        }
        if (multiSelect) {
            document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
            await waitForSettle(300);
        } else {
            await waitForSettle(150);
        }
        return JSON.stringify({ ok: true, picked: picked.length === 1 ? picked[0] : picked });
    }

    async function closeCombo() {
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await waitForSettle(300);
        return JSON.stringify({ ok: true });
    }

    async function clickAction(id) {
        const el = resolveElement(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Action id not found: ' + id });
        }
        if (el.disabled || el.classList.contains('mud-disabled')) {
            return JSON.stringify({ ok: false, error: 'Action is disabled: ' + id });
        }
        const label = cleanText(el.innerText || el.getAttribute('title') || id);
        spotlightElement(el);
        el.click();
        await waitForSettle(1500);
        return JSON.stringify({ ok: true, id, clicked: label });
    }

    async function selectTab(id) {
        const el = resolveElement(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Tab id not found in current view: ' + id });
        }
        el.click();
        await waitForSettle(600);
        return JSON.stringify({ ok: true, id });
    }

    async function selectGridRow(rowId) {
        const el = resolveElement(rowId);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Grid row id not found: ' + rowId });
        }
        el.click();
        await waitForSettle(400);
        return JSON.stringify({ ok: true, rowId });
    }

    function readGridHeaders(find) {
        const root = getActiveRoot();
        const tables = Array.from(root.querySelectorAll('.mud-table-root')).filter(isVisible);
        const headers = tables.flatMap(t => Array.from(t.querySelectorAll('thead th')).map(th => cleanText(th.innerText)).filter(Boolean));
        if (find) {
            const q = String(find).toLowerCase();
            const matches = headers.filter(h => h.toLowerCase().includes(q));
            return JSON.stringify({ ok: true, find, found: matches.length > 0, matches, headers });
        }
        return JSON.stringify({ ok: true, headers });
    }

    function getNavRouteList() {
        const blockedPrefixes = ['/logout', '/account', '/login', '/register', '/user', '/auditlog', '/import', '/emailtemplate'];
        const links = Array.from(document.querySelectorAll('.mud-navmenu a.mud-nav-link, a.mud-nav-link'));
        const seen = new Set();
        const routes = [];
        links.forEach(a => {
            const href = a.getAttribute('href');
            const text = cleanText(a.textContent || a.innerText);
            if (!href || href.startsWith('http') || href === '#') return;
            const normHref = href.startsWith('/') ? href : '/' + href;
            const low = normHref.split('?')[0].split('#')[0].replace(/\/+$/, '').toLowerCase() || '/';
            if (blockedPrefixes.some(p => low === p || low.startsWith(p + '/'))) return;
            const key = normHref + '|' + text;
            if (seen.has(key)) return;
            seen.add(key);
            routes.push({ href: normHref, text });
        });
        return routes;
    }

    function getNavRoutes() {
        return JSON.stringify(getNavRouteList());
    }

    function waitForSettle(maxMs) {
        const limit = maxMs || 1500;
        return new Promise(resolve => {
            const start = Date.now();
            function check() {
                const busy = activeRequests > 0 || Array.from(document.querySelectorAll('.mud-progress-linear, .mud-progress-circular'))
                    .some(el => !isExcluded(el) && isVisible(el));
                if (!busy && Date.now() - start >= 250) {
                    resolve();
                    return;
                }
                if (Date.now() - start >= limit) {
                    resolve();
                    return;
                }
                setTimeout(check, 100);
            }
            setTimeout(check, 150);
        });
    }

    function enablePanelResize(panel) {
        if (!panel) return;
        const clamp = (val, min, max) => Math.min(max, Math.max(min, val));
        panel.querySelectorAll('.agent-resize-handle').forEach(handle => {
            if (handle.dataset.resizeBound === '1') return;
            handle.dataset.resizeBound = '1';
            const dir = handle.dataset.resizeDir || '';
            handle.addEventListener('pointerdown', (e) => {
                if (e.button !== 0) return;
                e.preventDefault();
                e.stopPropagation();
                const rect = panel.getBoundingClientRect();
                const startX = e.clientX;
                const startY = e.clientY;
                const startLeft = rect.left;
                const startTop = rect.top;
                const startRight = rect.right;
                const startBottom = rect.bottom;
                const startWidth = rect.width;
                const startHeight = rect.height;

                panel.style.left = startLeft + 'px';
                panel.style.top = startTop + 'px';
                panel.style.right = 'auto';
                panel.style.bottom = 'auto';
                panel.style.width = startWidth + 'px';
                panel.style.height = startHeight + 'px';

                try { handle.setPointerCapture(e.pointerId); } catch { /* ignore */ }

                function onMove(ev) {
                    const dx = ev.clientX - startX;
                    const dy = ev.clientY - startY;
                    const minW = Math.min(340, window.innerWidth * 0.94);
                    const maxW = window.innerWidth * 0.94;
                    const minH = Math.min(420, window.innerHeight * 0.90);
                    const maxH = window.innerHeight * 0.90;

                    if (dir.includes('e')) {
                        const w = clamp(startWidth + dx, minW, Math.min(maxW, window.innerWidth - startLeft));
                        panel.style.width = w + 'px';
                    }
                    if (dir.includes('w')) {
                        const newLeft = clamp(startLeft + dx, Math.max(0, startRight - maxW), startRight - minW);
                        panel.style.left = newLeft + 'px';
                        panel.style.width = (startRight - newLeft) + 'px';
                    }
                    if (dir.includes('s')) {
                        const h = clamp(startHeight + dy, minH, Math.min(maxH, window.innerHeight - startTop));
                        panel.style.height = h + 'px';
                    }
                    if (dir.includes('n')) {
                        const newTop = clamp(startTop + dy, Math.max(0, startBottom - maxH), startBottom - minH);
                        panel.style.top = newTop + 'px';
                        panel.style.height = (startBottom - newTop) + 'px';
                    }
                }
                function onUp(ev) {
                    try { handle.releasePointerCapture(ev.pointerId); } catch { /* ignore */ }
                    handle.removeEventListener('pointermove', onMove);
                    handle.removeEventListener('pointerup', onUp);
                    handle.removeEventListener('pointercancel', onUp);
                    window.removeEventListener('blur', onUp);
                }
                handle.addEventListener('pointermove', onMove);
                handle.addEventListener('pointerup', onUp);
                handle.addEventListener('pointercancel', onUp);
                window.addEventListener('blur', onUp);
            });
        });
    }

    function enablePanelDrag() {
        const panel = document.querySelector('[data-testid="agent-panel"]');
        enablePanelResize(panel);
        const handle = document.querySelector('[data-testid="agent-drag-handle"]');
        if (!panel || !handle || handle.dataset.dragBound === '1') return;
        handle.dataset.dragBound = '1';

        handle.addEventListener('pointerdown', (e) => {
            if (e.button !== 0 || e.target.closest('button, input, select, a, .agent-resize-handle')) return;
            e.preventDefault();
            const rect = panel.getBoundingClientRect();
            const startLeft = rect.left;
            const startTop = rect.top;
            const startX = e.clientX;
            const startY = e.clientY;
            panel.style.left = startLeft + 'px';
            panel.style.top = startTop + 'px';
            panel.style.right = 'auto';
            panel.style.bottom = 'auto';

            try { handle.setPointerCapture(e.pointerId); } catch { /* ignore */ }

            function onMove(ev) {
                const box = panel.getBoundingClientRect();
                const maxLeft = Math.max(0, window.innerWidth - box.width);
                const maxTop = Math.max(0, window.innerHeight - box.height);
                const nextLeft = Math.min(maxLeft, Math.max(0, startLeft + (ev.clientX - startX)));
                const nextTop = Math.min(maxTop, Math.max(0, startTop + (ev.clientY - startY)));
                panel.style.left = nextLeft + 'px';
                panel.style.top = nextTop + 'px';
            }
            function onUp(ev) {
                try { handle.releasePointerCapture(ev.pointerId); } catch { /* ignore */ }
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
                handle.removeEventListener('pointercancel', onUp);
                window.removeEventListener('blur', onUp);
            }
            handle.addEventListener('pointermove', onMove);
            handle.addEventListener('pointerup', onUp);
            handle.addEventListener('pointercancel', onUp);
            window.addEventListener('blur', onUp);
        });
    }

    let lastSavedFilters = [];

    async function clearFilters() {
        const root = getActiveRoot();
        const inputs = Array.from(root.querySelectorAll('.mud-table-toolbar input, .mud-table-filter input, thead input'))
            .filter(el => isVisible(el) && !el.disabled && !el.readOnly && (el.value || '').length > 0);
        const saved = inputs.map(el => ({ el, value: el.value }));
        if (saved.length > 0) lastSavedFilters = saved;
        let cleared = 0;
        let succeeded = false;
        try {
            for (const { el } of saved) {
                setNativeInputValue(el, '');
                cleared++;
            }
            if (cleared > 0) await waitForSettle(600);
            succeeded = true;
            return JSON.stringify({ ok: true, cleared, canRestore: saved.length > 0 });
        } catch (err) {
            return JSON.stringify({ ok: false, error: err?.message || 'Failed to clear filters.' });
        } finally {
            if (!succeeded) {
                for (const { el, value } of saved) {
                    if (document.contains(el)) setNativeInputValue(el, value);
                }
            }
        }
    }

    async function restoreFilters() {
        let restored = 0;
        for (const { el, value } of lastSavedFilters) {
            if (document.contains(el) && !el.disabled) {
                setNativeInputValue(el, value);
                restored++;
            }
        }
        if (restored > 0) await waitForSettle(600);
        return JSON.stringify({ ok: true, restored });
    }

    function scanMenus() {
        const routes = getNavRouteList();
        const groups = Array.from(document.querySelectorAll('.mud-nav-group button.mud-nav-link, .mud-nav-group-header'))
            .map(el => cleanText(el.innerText))
            .filter(Boolean);
        return JSON.stringify({ ok: true, routes, groups, openMenuItems: getOpenComboOptions() });
    }

    function scanContextMenu() {
        const items = getOpenComboOptions();
        return JSON.stringify({ ok: items.length > 0, items });
    }

    function collectTabs(root, used, knownIds) {
        const tabs = [];
        root.querySelectorAll('.mud-tab').forEach(el => {
            if (!isVisible(el)) return;
            const title = cleanText(el.innerText);
            const id = knownIds.get(el) || claimId('tab_' + (slugify(title) || 'tab'), used);
            elementMap.set(id, el);
            tabs.push({
                id,
                title,
                active: el.classList.contains('mud-tab-active'),
                hasError: !!el.querySelector('.mud-error-text'),
                disabled: el.classList.contains('mud-disabled')
            });
        });
        return tabs;
    }

    function scanTabs() {
        const knownIds = new Map([...elementMap].map(([id, el]) => [el, id]));
        const tabs = collectTabs(getActiveRoot(), new Set(elementMap.keys()), knownIds);
        return JSON.stringify({ ok: true, tabs });
    }

    async function scrollGrid(direction) {
        const root = getActiveRoot();
        const container = Array.from(root.querySelectorAll('.mud-table-container')).find(isVisible)
            || Array.from(root.querySelectorAll('.mud-table-root')).find(isVisible)?.parentElement;
        if (!container) return JSON.stringify({ ok: false, error: 'No visible grid container found.' });

        const step = Math.max(200, container.clientHeight * 0.8);
        if (direction === 'top') container.scrollTop = 0;
        else if (direction === 'bottom') container.scrollTop = container.scrollHeight;
        else if (direction === 'up') container.scrollTop = Math.max(0, container.scrollTop - step);
        else container.scrollTop = Math.min(container.scrollHeight, container.scrollTop + step);

        await waitForSettle(250);
        return JSON.stringify({ ok: true, direction, scrollTop: container.scrollTop, scrollHeight: container.scrollHeight });
    }

    async function selectTreeNode(label) {
        const q = cleanText(String(label || '')).toLowerCase();
        if (!q) return JSON.stringify({ ok: false, error: 'Tree node label is required.' });
        const root = getActiveRoot();
        const contents = Array.from(root.querySelectorAll('.mud-treeview-item-content')).filter(isVisible);
        const nodes = contents.length > 0 ? contents : Array.from(root.querySelectorAll('.mud-treeview-item')).filter(isVisible);
        const match = nodes.find(n => cleanText(n.innerText).toLowerCase() === q)
            || nodes.find(n => cleanText(n.innerText).toLowerCase().includes(q));
        if (!match) return JSON.stringify({ ok: false, error: 'Tree node not found: ' + label });
        spotlightElement(match);
        match.click();
        await waitForSettle(400);
        return JSON.stringify({ ok: true, selected: cleanText(match.innerText) });
    }

    function searchSettings(query) {
        const q = cleanText(String(query || '')).toLowerCase();
        if (!q) return JSON.stringify({ ok: false, error: 'Search query is required.' });
        const root = getActiveRoot();
        const routes = getNavRouteList().filter(r =>
            r.text.toLowerCase().includes(q) || r.href.toLowerCase().includes(q));
        const tabs = Array.from(root.querySelectorAll('.mud-tab')).filter(isVisible)
            .map(t => cleanText(t.innerText)).filter(t => t.toLowerCase().includes(q));
        const labels = Array.from(root.querySelectorAll('.mud-input-label, button, a.mud-button-root')).filter(isVisible)
            .map(el => cleanText(el.innerText || el.getAttribute('aria-label') || ''))
            .filter(t => t && t.toLowerCase().includes(q));
        return JSON.stringify({ ok: true, query, routes: routes.slice(0, 15), tabs, elements: [...new Set(labels)].slice(0, 20) });
    }

    function capturePicture() {
        try {
            const root = getActiveRoot();
            const rect = root.getBoundingClientRect();
            const w = Math.min(1024, Math.max(200, Math.round(rect.width)));
            const h = Math.min(768, Math.max(200, Math.round(rect.height)));
            const clone = root.cloneNode(true);
            clone.querySelectorAll('[data-agent-panel="true"], script, style, input[type="password"]').forEach(el => el.remove());
            clone.querySelectorAll('.mud-input-control').forEach(ctrl => {
                const lbl = cleanText(ctrl.querySelector('label')?.innerText || '');
                if (/password|secret|token|apikey|رمز\s*عبور|کلمه\s*عبور/i.test(lbl)) {
                    ctrl.querySelectorAll('input, textarea').forEach(inp => inp.remove());
                }
            });
            const serialized = new XMLSerializer().serializeToString(clone);
            const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}"><foreignObject width="100%" height="100%">${serialized}</foreignObject></svg>`;
            const img = new Image();
            return new Promise(resolve => {
                const timer = setTimeout(() => resolve(''), 1200);
                img.onload = () => {
                    clearTimeout(timer);
                    try {
                        const canvas = document.createElement('canvas');
                        canvas.width = w;
                        canvas.height = h;
                        const ctx = canvas.getContext('2d');
                        ctx.fillStyle = '#ffffff';
                        ctx.fillRect(0, 0, w, h);
                        ctx.drawImage(img, 0, 0);
                        const dataUrl = canvas.toDataURL('image/png');
                        resolve(dataUrl.replace(/^data:image\/png;base64,/, ''));
                    } catch {
                        resolve('');
                    }
                };
                img.onerror = () => { clearTimeout(timer); resolve(''); };
                img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
            });
        } catch {
            return Promise.resolve('');
        }
    }

    async function copyText(text) {
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                await navigator.clipboard.writeText(String(text || ''));
                return true;
            }
        } catch { /* fallback below */ }
        const ta = document.createElement('textarea');
        ta.value = String(text || '');
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        let ok = false;
        try { ok = document.execCommand('copy'); } catch { ok = false; }
        ta.remove();
        return ok;
    }

    function savePanelState(key, value) {
        try { localStorage.setItem(String(key), String(value)); } catch { /* storage quota/disabled */ }
    }

    function loadPanelState(key) {
        try { return localStorage.getItem(String(key)) || ''; } catch { return ''; }
    }

    function clearPanelState(key) {
        try { localStorage.removeItem(String(key)); } catch { /* ignore */ }
    }

    function bindFileDrop(dotNetRef) {
        const panel = document.querySelector('[data-testid="agent-panel"]');
        if (!panel || panel.dataset.dropBound === '1') return;
        panel.dataset.dropBound = '1';
        panel.addEventListener('dragover', (e) => {
            if (e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files')) {
                e.preventDefault();
                panel.classList.add('agent-panel--dragover');
            }
        });
        panel.addEventListener('dragleave', () => panel.classList.remove('agent-panel--dragover'));
        panel.addEventListener('drop', async (e) => {
            panel.classList.remove('agent-panel--dragover');
            if (!e.dataTransfer || !e.dataTransfer.files || e.dataTransfer.files.length === 0) return;
            e.preventDefault();
            const files = Array.from(e.dataTransfer.files).slice(0, 5);
            for (const f of files) {
                if (f.size === 0 || f.size > 10 * 1024 * 1024) {
                    await dotNetRef.invokeMethodAsync('OnFileDropError', f.name, f.size).catch(() => {});
                    continue;
                }
                try {
                    const buf = await f.arrayBuffer();
                    const bytes = new Uint8Array(buf);
                    const chunks = [];
                    for (let i = 0; i < bytes.length; i += 8192) {
                        chunks.push(String.fromCharCode(...bytes.subarray(i, i + 8192)));
                    }
                    await dotNetRef.invokeMethodAsync('OnFileDropped', f.name, btoa(chunks.join('')));
                } catch {
                    await dotNetRef.invokeMethodAsync('OnFileDropError', f.name, f.size).catch(() => {});
                }
            }
        });
    }

    function setNativeInputValue(input, strVal) {
        const nativeSetter = Object.getOwnPropertyDescriptor(
            input.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype,
            'value'
        )?.set;
        if (nativeSetter) nativeSetter.call(input, strVal);
        else input.value = strVal;
        input.dispatchEvent(new InputEvent('input', { bubbles: true, data: strVal }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    async function sortGrid(column, direction) {
        const root = getActiveRoot();
        const q = cleanText(column).toLowerCase();
        if (!q) return JSON.stringify({ ok: false, error: 'Column name is required.' });
        const ths = Array.from(root.querySelectorAll('.mud-table-root thead th')).filter(isVisible);
        const target = ths.find(th => cleanText(th.innerText).toLowerCase() === q)
            || ths.find(th => cleanText(th.innerText).toLowerCase().includes(q));
        if (!target) return JSON.stringify({ ok: false, error: 'Column not found: ' + column });

        const clickTarget = target.querySelector('.mud-table-sort-label') || target;
        spotlightElement(clickTarget);
        const expected = direction ? (String(direction).toLowerCase() === 'desc' ? 'descending' : 'ascending') : null;
        const maxClicks = expected ? 3 : 1;
        for (let i = 0; i < maxClicks; i++) {
            clickTarget.click();
            await waitForSettle(500);
            const current = (target.getAttribute('aria-sort') || '').toLowerCase();
            if (!expected || current === expected) {
                return JSON.stringify({ ok: true, column: cleanText(target.innerText), direction: current || 'toggled' });
            }
        }
        return JSON.stringify({ ok: false, error: 'Failed to reach requested sort direction: ' + direction });
    }

    async function uploadFileToInput(id, fileName, base64, contentType) {
        const el = resolveElement(id);
        const input = el && el.matches && el.matches('input[type="file"]') ? el : el?.querySelector?.('input[type="file"]');
        if (!input || !document.contains(input)) {
            return JSON.stringify({ ok: false, error: 'File input id not found in current snapshot: ' + id });
        }
        let bin;
        try {
            bin = atob(base64 || '');
        } catch {
            return JSON.stringify({ ok: false, error: 'Invalid base64 file content.' });
        }
        const arr = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
        const file = new File([arr], fileName || 'upload.bin', { type: contentType || 'application/octet-stream' });
        const dt = new DataTransfer();
        dt.items.add(file);
        input.files = dt.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
        await waitForSettle(600);
        return JSON.stringify({ ok: true, fileName: file.name });
    }

    async function rightClick(id) {
        const el = resolveElement(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Element id not found: ' + id });
        }
        spotlightElement(el);
        const rect = el.getBoundingClientRect();
        el.dispatchEvent(new MouseEvent('contextmenu', {
            bubbles: true,
            cancelable: true,
            button: 2,
            clientX: rect.left + rect.width / 2,
            clientY: rect.top + rect.height / 2
        }));
        await waitForSettle(400);
        return JSON.stringify({ ok: true, id, menuOptions: getOpenComboOptions() });
    }

    let activeRecognition = null;
    let voiceGeneration = 0;

    function startVoiceInput(dotNetRef) {
        const SpeechRec = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SpeechRec) {
            return JSON.stringify({ supported: false, error: 'Speech recognition is not supported in this browser.' });
        }
        const gen = ++voiceGeneration;
        try {
            if (activeRecognition) {
                try { activeRecognition.abort(); } catch { /* ignore abort error */ }
            }
            const rec = new SpeechRec();
            rec.lang = 'fa-IR';
            rec.interimResults = false;
            rec.maxAlternatives = 1;
            const safeInvoke = (method, ...args) => {
                if (dotNetRef && gen === voiceGeneration) {
                    dotNetRef.invokeMethodAsync(method, ...args).catch(() => { /* panel disposed */ });
                }
            };
            rec.onresult = (ev) => safeInvoke('OnVoiceResult', ev?.results?.[0]?.[0]?.transcript || '');
            rec.onerror = (ev) => safeInvoke('OnVoiceError', ev?.error || 'voice_error');
            rec.onend = () => {
                if (gen === voiceGeneration) activeRecognition = null;
                safeInvoke('OnVoiceEnded');
            };
            activeRecognition = rec;
            rec.start();
            return JSON.stringify({ supported: true, started: true });
        } catch (err) {
            return JSON.stringify({ supported: true, started: false, error: err?.message || 'Voice start failed.' });
        }
    }

    function stopVoiceInput() {
        voiceGeneration++;
        try {
            if (activeRecognition) activeRecognition.abort();
        } catch { /* ignore stop errors */ }
        activeRecognition = null;
        return JSON.stringify({ ok: true });
    }

    function djb2(s) {
        let h = 5381;
        for (let i = 0; i < s.length; i++) h = (((h << 5) + h) + s.charCodeAt(i)) | 0;
        return h >>> 0;
    }

    // Compact identity of the current screen state (route + dialog + field ids/values),
    // sent with every tool result so the client can detect no-op repeat loops.
    function screenFingerprint() {
        try {
            const dialogs = Array.from(document.querySelectorAll('.mud-dialog[role="dialog"]')).filter(isVisible);
            const topDialog = dialogs.length > 0 ? dialogs[dialogs.length - 1] : null;
            const scope = topDialog || document.querySelector('.mud-main-content') || document.body;
            const titleEl = (topDialog && topDialog.querySelector('.mud-dialog-title'))
                || document.querySelector('h1, h2, h3, .mud-typography-h3, .mud-typography-h4');
            const title = cleanText(titleEl ? titleEl.innerText : '');
            const parts = [];
            scope.querySelectorAll('input, select, textarea').forEach(el => {
                if (el.closest('[data-agent-panel], .agent-panel')) return;
                const id = (el.id || el.name || el.getAttribute('aria-label') || '').trim();
                if (!id) return;
                const type = (el.type || '').toLowerCase();
                let v;
                if (type === 'checkbox') v = String(!!el.checked);
                else if (type === 'password' || isSensitiveLabel(id)) v = '';
                else v = String(el.value || '').slice(0, 32);
                parts.push(id + '=' + v);
            });
            const head = djb2((topDialog ? 'd:' : 'p:') + (window.location.pathname || '') + '|' + title).toString(36);
            const body = djb2(parts.join('&')).toString(36);
            return head + ':' + body;
        } catch { return 'fp:na'; }
    }

    const api = {
        captureSnapshot,
        captureVisibleText,
        capturePicture,
        setField,
        openCombo,
        pickOption,
        closeCombo,
        clickAction,
        selectTab,
        selectGridRow,
        readGridHeaders,
        clearFilters,
        restoreFilters,
        sortGrid,
        rightClick,
        scanMenus,
        scanContextMenu,
        scanTabs,
        scrollGrid,
        selectTreeNode,
        searchSettings,
        uploadFileToInput,
        startVoiceInput,
        stopVoiceInput,
        getNavRoutes,
        waitForSettle,
        enablePanelDrag,
        copyText,
        savePanelState,
        loadPanelState,
        clearPanelState,
        bindFileDrop
    };
    for (const key of Object.keys(api)) {
        const fn = api[key];
        if (typeof fn !== 'function') continue;
        api[key] = async (...callArgs) => {
            const res = await fn(...callArgs);
            if (typeof res !== 'string') return res;
            try {
                const parsed = JSON.parse(res);
                if (parsed && typeof parsed === 'object' && !Array.isArray(parsed) && parsed.fingerprint === undefined)
                    parsed.fingerprint = screenFingerprint();
                return JSON.stringify(parsed);
            } catch { return res; }
        };
    }
    return api;
})();
