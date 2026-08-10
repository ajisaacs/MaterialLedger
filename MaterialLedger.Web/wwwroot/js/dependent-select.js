function initDependentSelect(trigger, dependent, datasetKey) {
    const allOptions = Array.from(dependent.options).filter(option => option.dataset[datasetKey] !== undefined);
    const selected = allOptions.find(option => option.value === dependent.value);
    if (selected) trigger.value = selected.dataset[datasetKey];
    const filter = resetSelection => {
        const previousValue = dependent.value;
        allOptions.forEach(option => option.remove());
        for (const option of allOptions) {
            if (option.dataset[datasetKey] === trigger.value) dependent.appendChild(option);
        }
        dependent.disabled = !trigger.value;
        dependent.value = resetSelection ? '' : previousValue;
    };
    trigger.addEventListener('change', () => filter(true));
    filter(false);
}
