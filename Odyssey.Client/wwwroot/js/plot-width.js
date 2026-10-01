// ResizeObserver bridge for OdsLineChart / OdsStepChart (Odyssey Design System · LineChart, StepChart).
// The charts size their axis text in SVG user units, so the viewBox has to track the plot's real
// pixel width (1 unit = 1px) or a three-up card scales a 10px label down to about 3px (issue #274).
// Reports the rounded width whenever it changes; a zero width (display:none) is never reported.
export function observe(el, dotNetRef) {
    if (!el) return null;
    let last = 0;
    let live = true;
    const measure = () => {
        if (!live) return;
        const w = Math.round(el.getBoundingClientRect().width);
        if (w > 0 && w !== last) {
            last = w;
            // A resize can land after the component is gone; its rejection is not an app error.
            dotNetRef.invokeMethodAsync('OnPlotWidth', w).catch(() => { });
        }
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return { disconnect: () => { live = false; } };
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return { disconnect: () => { live = false; ro.disconnect(); } };
}
