function initAddSheetForm(dialog) {
    const form = dialog.querySelector('.sheet-entry-form');
    const rackSelect = document.getElementById('new-sheet-rack');
    const slotSelect = document.getElementById('new-sheet-slot');
    const materialSelect = document.getElementById('new-sheet-material');
    const widthInput = form.querySelector('[name="NewSheet.UsableWidthIn"]');
    const lengthInput = form.querySelector('[name="NewSheet.UsableLengthIn"]');
    const quarantineInput = form.querySelector('[name="NewSheet.IsQuarantined"]');
    const notesInput = form.querySelector('[name="NewSheet.Notes"]');
    const submitButton = form.querySelector('button[type="submit"]');
    const errorEl = document.getElementById('add-sheet-error');
    const logWrap = document.getElementById('add-sheet-log');
    const logList = document.getElementById('add-sheet-log-list');

    const clearItemFields = () => {
        materialSelect.value = '';
        materialSelect.dispatchEvent(new Event('change'));
        slotSelect.value = '';
        widthInput.value = '';
        lengthInput.value = '';
        quarantineInput.checked = false;
        notesInput.value = '';
    };

    form.addEventListener('submit', async event => {
        event.preventDefault();
        errorEl.hidden = true;
        submitButton.disabled = true;
        submitButton.textContent = 'Adding…';

        try {
            const response = await fetch(form.action, { method: 'post', body: new FormData(form) });
            const result = await response.json();
            if (result.success) {
                const row = document.createElement('li');
                const size = result.usableWidthIn && result.usableLengthIn
                    ? ` — ${result.usableWidthIn} × ${result.usableLengthIn}`
                    : '';
                row.textContent = `${result.itemCode} — Slot ${result.slotCode} — ${result.materialDisplayName}${size}`;
                logList.appendChild(row);
                logWrap.hidden = false;
                clearItemFields();
                slotSelect.focus();
            } else {
                errorEl.textContent = result.error;
                errorEl.hidden = false;
            }
        } catch {
            errorEl.textContent = 'Could not reach the server — check your connection and try again.';
            errorEl.hidden = false;
        } finally {
            submitButton.disabled = false;
            submitButton.textContent = 'Add sheet';
        }
    });

    dialog.addEventListener('close', () => {
        logList.replaceChildren();
        logWrap.hidden = true;
    });
}
