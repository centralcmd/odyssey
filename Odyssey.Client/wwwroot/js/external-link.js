// Copy for OdsExternalLink (Odyssey Design System · ExternalLink, issue #473 §6.2). Returns whether the
// copy succeeded; on a failure the URL text is selected, so the person can copy it by hand.
export async function copy(text, el) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        try {
            if (el && window.getSelection) {
                const range = document.createRange();
                range.selectNodeContents(el);
                const sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(range);
            }
        } catch {
            // Selecting is a courtesy; the message still says what to do.
        }
        return false;
    }
}
