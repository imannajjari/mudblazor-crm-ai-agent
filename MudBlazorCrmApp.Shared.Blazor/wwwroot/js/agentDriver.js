window.crmAgentDriver = (function () {
    const elementMap = new Map();

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
        const t = (label || '').toLowerCase();
        if (t.includes('delete') || t.includes('remove') || t.includes('حذف')) return 'destructive';
        if (t.includes('save') || t.includes('submit') || t.includes('add') || t.includes('create') ||
            t.includes('update') || t.includes('ذخیره') || t.includes('ثبت') || t.includes('افزودن') || t.includes('ویرایش')) {
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
            elementMap.set(id, input);
            fields.push({
                id,
                label: label || id,
                kind: ctrl.closest('.mud-select') ? 'select' : (input.tagName === 'TEXTAREA' ? 'textarea' : (input.type || 'text')),
                value: input.value || '',
                required: input.hasAttribute('required') || ctrl.classList.contains('mud-input-required'),
                invalid: ctrl.classList.contains('mud-input-error') || input.getAttribute('aria-invalid') === 'true',
                disabled: input.disabled || input.readOnly
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
            const rows = allRows.slice(0, maxRows).map(tr =>
                Array.from(tr.querySelectorAll('td')).map(td => cleanText(td.innerText))
            );

            const footerEl = container ? container.querySelector('.mud-table-pagination-caption') : null;
            const gridObj = {
                id: 'g' + (gridIdx++),
                headers,
                rows,
                rowsShown: rows.length,
                rowsCut: allRows.length > maxRows,
                footerCount: footerEl ? cleanText(footerEl.innerText) : null
            };
            grids.push(gridObj);
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
            errors,
            toasts
        });
    }

    function captureVisibleText() {
        const root = document.querySelector('.mud-main-content') || document.body;
        const clone = root.cloneNode(true);
        clone.querySelectorAll('[data-agent-panel="true"], script, style').forEach(el => el.remove());
        const text = (clone.innerText || '').replace(/\n{3,}/g, '\n\n').trim();
        return text.slice(0, 30000);
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

    function waitForSettle(maxMs) {
        const limit = maxMs || 1500;
        return new Promise(resolve => {
            const start = Date.now();
            function check() {
                const busy = Array.from(document.querySelectorAll('.mud-progress-linear, .mud-progress-circular'))
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
        selectTab,
        waitForSettle
    };
})();
