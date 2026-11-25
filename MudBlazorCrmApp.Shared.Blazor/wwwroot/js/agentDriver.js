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

    function classifyRisk(label) {
        const t = cleanText(label).toLowerCase();
        if (/\b(delete|remove|void)\b/.test(t) || t.includes('حذف')) return 'destructive';
        if (/\b(yes|confirm)\b/.test(t) || t === 'بله') return 'confirm';
        if (/\b(save|submit|add|create|update|ok)\b/.test(t) ||
            ['ذخیره', 'ثبت', 'افزودن', 'ویرایش', 'تایید'].some(k => t.includes(k))) {
            return 'save';
        }
        return 'read';
    }

    function captureSnapshot() {
        elementMap.clear();
        const dialogs = Array.from(document.querySelectorAll('.mud-dialog[role="dialog"]')).filter(isVisible);
        const topDialog = dialogs.length > 0 ? dialogs[dialogs.length - 1] : null;
        const root = topDialog || document.querySelector('.mud-main-content') || document.body;

        const headingEl = (topDialog ? topDialog.querySelector('.mud-dialog-title') : null)
            || root.querySelector('h1, h2, h3, .mud-typography-h3, .mud-typography-h4');

        // Tabs
        const tabs = [];
        let tabIdx = 1;
        root.querySelectorAll('.mud-tab').forEach(el => {
            if (!isVisible(el)) return;
            const id = 't' + (tabIdx++);
            elementMap.set(id, el);
            tabs.push({
                id,
                title: cleanText(el.innerText),
                active: el.classList.contains('mud-tab-active'),
                hasError: !!el.querySelector('.mud-error-text')
            });
        });

        // Fields
        const fields = [];
        let fieldIdx = 1;
        root.querySelectorAll('.mud-input-control').forEach(ctrl => {
            if (!isVisible(ctrl)) return;
            const input = ctrl.querySelector('input.mud-input-slot, textarea.mud-input-slot');
            if (!input) return;
            const labelEl = ctrl.querySelector('label.mud-input-label');
            const label = cleanText(labelEl ? labelEl.innerText : (input.getAttribute('placeholder') || input.getAttribute('aria-label') || ''));
            const id = 'f' + (fieldIdx++);
            const isSelect = !!ctrl.closest('.mud-select');
            elementMap.set(id, isSelect ? ctrl.closest('.mud-select') : input);
            fields.push({
                id,
                label: label || id,
                kind: isSelect ? 'select' : (input.tagName === 'TEXTAREA' ? 'textarea' : (input.type || 'text')),
                value: input.value || '',
                maxLength: input.maxLength > 0 ? input.maxLength : null,
                required: input.hasAttribute('required') || ctrl.classList.contains('mud-input-required'),
                invalid: ctrl.classList.contains('mud-input-error') || input.getAttribute('aria-invalid') === 'true',
                // MudSelect renders a readonly inner input by design; only treat readOnly as disabled for non-select inputs
                disabled: isSelect ? (ctrl.classList.contains('mud-disabled') || input.disabled) : (input.disabled || input.readOnly)
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

            const allRows = Array.from(table.querySelectorAll('tbody tr'));
            const maxRows = 15;
            const rows = allRows.slice(0, maxRows).map((tr, rIdx) => {
                const rowId = 'r' + gridIdx + '_' + (rIdx + 1);
                elementMap.set(rowId, tr);
                return {
                    rowId,
                    cells: Array.from(tr.querySelectorAll('td')).map(td => cleanText(td.innerText))
                };
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
        let actIdx = 1;
        root.querySelectorAll('button, a.mud-button-root').forEach(btn => {
            if (!isVisible(btn)) return;
            if (btn.closest('.mud-tab')) return;
            const text = cleanText(btn.innerText || btn.getAttribute('title') || btn.getAttribute('aria-label') || '');
            if (!text) return;
            const id = 'a' + (actIdx++);
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

        return JSON.stringify({
            url: window.location.pathname + window.location.search,
            title: cleanText(headingEl ? headingEl.innerText : document.title),
            inDialog: !!topDialog,
            tabs,
            fields: fields.slice(0, 40),
            grids,
            actions: actions.slice(0, 40),
            lookupOptions,
            errors,
            toasts
        });
    }

    function getOpenComboOptions() {
        const popovers = Array.from(document.querySelectorAll('.mud-popover-open')).filter(isVisible);
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
        const dialogs = Array.from(document.querySelectorAll('.mud-dialog[role="dialog"]')).filter(isVisible);
        const root = (dialogs.length > 0 ? dialogs[dialogs.length - 1] : null)
            || document.querySelector('.mud-main-content')
            || document.body;
        const clone = root.cloneNode(true);
        clone.querySelectorAll('[data-agent-panel="true"], script, style').forEach(el => el.remove());
        const text = (clone.innerText || '').replace(/\n{3,}/g, '\n\n').trim();
        return text.slice(0, 30000);
    }

    async function setField(id, value) {
        const el = elementMap.get(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Field id not found in current view: ' + id });
        }

        const input = el.matches('input, textarea') ? el : el.querySelector('input.mud-input-slot, textarea.mud-input-slot');
        if (!input) {
            return JSON.stringify({ ok: false, error: 'Input element not found for id: ' + id });
        }
        if (input.disabled || input.readOnly) {
            return JSON.stringify({ ok: false, error: 'Field is disabled or read-only: ' + id });
        }

        const strVal = value == null ? '' : String(value);
        if (input.maxLength > 0 && strVal.length > input.maxLength) {
            return JSON.stringify({ ok: false, error: `Value exceeds maxLength (${input.maxLength}).` });
        }

        input.focus();
        const nativeSetter = Object.getOwnPropertyDescriptor(
            input.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype,
            'value'
        )?.set;
        if (nativeSetter) {
            nativeSetter.call(input, strVal);
        } else {
            input.value = strVal;
        }
        input.dispatchEvent(new InputEvent('input', { bubbles: true, data: strVal }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
        await new Promise(r => requestAnimationFrame(r));
        input.blur();

        await waitForSettle(400);
        return JSON.stringify({ ok: true, id, fieldValue: input.value });
    }

    async function openCombo(id) {
        const el = elementMap.get(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Combo id not found: ' + id });
        }
        const target = el.querySelector('.mud-input-slot') || el;
        target.click();
        await waitForSettle(500);
        const options = getOpenComboOptions();
        return JSON.stringify({ ok: true, id, lookupOptions: options });
    }

    async function pickOption(optionTextOrIndex) {
        const popovers = Array.from(document.querySelectorAll('.mud-popover-open')).filter(isVisible);
        const items = [];
        popovers.forEach(p => {
            p.querySelectorAll('.mud-list-item').forEach(item => {
                if (isVisible(item)) items.push(item);
            });
        });

        if (items.length === 0) {
            return JSON.stringify({ ok: false, error: 'No open combo options found. Call open_combo first.' });
        }

        const q = cleanText(String(optionTextOrIndex)).toLowerCase();
        if (!q) {
            return JSON.stringify({ ok: false, error: 'Option text or index is required.' });
        }
        let match = items.find(i => cleanText(i.innerText).toLowerCase() === q);
        if (!match && (typeof optionTextOrIndex === 'number' || /^\d+$/.test(String(optionTextOrIndex)))) {
            const idx = Number(optionTextOrIndex) - 1;
            if (idx >= 0 && idx < items.length) match = items[idx];
        }
        if (!match) {
            match = items.find(i => cleanText(i.innerText).toLowerCase().includes(q));
        }

        if (!match) {
            return JSON.stringify({ ok: false, error: 'Option not found: ' + optionTextOrIndex });
        }

        const chosenText = cleanText(match.innerText);
        match.click();
        await waitForSettle(500);
        return JSON.stringify({ ok: true, picked: chosenText });
    }

    async function closeCombo() {
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await waitForSettle(300);
        return JSON.stringify({ ok: true });
    }

    async function clickAction(id) {
        const el = elementMap.get(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Action id not found: ' + id });
        }
        if (el.disabled || el.classList.contains('mud-disabled')) {
            return JSON.stringify({ ok: false, error: 'Action is disabled: ' + id });
        }
        const label = cleanText(el.innerText || el.getAttribute('title') || id);
        el.click();
        await waitForSettle(1500);
        return JSON.stringify({ ok: true, id, clicked: label });
    }

    async function selectTab(id) {
        const el = elementMap.get(id);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Tab id not found in current view: ' + id });
        }
        el.click();
        await waitForSettle(600);
        return JSON.stringify({ ok: true, id });
    }

    async function selectGridRow(rowId) {
        const el = elementMap.get(rowId);
        if (!el || !document.contains(el)) {
            return JSON.stringify({ ok: false, error: 'Grid row id not found: ' + rowId });
        }
        el.click();
        await waitForSettle(400);
        return JSON.stringify({ ok: true, rowId });
    }

    function readGridHeaders(find) {
        const dialogs = Array.from(document.querySelectorAll('.mud-dialog[role="dialog"]')).filter(isVisible);
        const root = (dialogs.length > 0 ? dialogs[dialogs.length - 1] : null)
            || document.querySelector('.mud-main-content')
            || document.body;
        const tables = Array.from(root.querySelectorAll('.mud-table-root')).filter(isVisible);
        const headers = tables.flatMap(t => Array.from(t.querySelectorAll('thead th')).map(th => cleanText(th.innerText)).filter(Boolean));
        if (find) {
            const q = String(find).toLowerCase();
            const matches = headers.filter(h => h.toLowerCase().includes(q));
            return JSON.stringify({ ok: true, find, found: matches.length > 0, matches, headers });
        }
        return JSON.stringify({ ok: true, headers });
    }

    function getNavRoutes() {
        const blocked = new Set(['/logout', '/account/changepassword', '/login', '/register', '/user']);
        const links = Array.from(document.querySelectorAll('.mud-nav-menu a.mud-nav-link'));
        const seen = new Set();
        const routes = [];
        links.forEach(a => {
            const href = a.getAttribute('href');
            const text = cleanText(a.innerText);
            if (!href || href.startsWith('http') || href === '#') return;
            const normHref = href.startsWith('/') ? href : '/' + href;
            if (blocked.has(normHref.toLowerCase())) return;
            const key = normHref + '|' + text;
            if (seen.has(key)) return;
            seen.add(key);
            routes.push({ href: normHref, text });
        });
        return JSON.stringify(routes);
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

    return {
        captureSnapshot,
        captureVisibleText,
        setField,
        openCombo,
        pickOption,
        closeCombo,
        clickAction,
        selectTab,
        selectGridRow,
        readGridHeaders,
        getNavRoutes,
        waitForSettle
    };
})();
