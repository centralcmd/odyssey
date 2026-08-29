// Global ⌘K / Ctrl+K listener for the navigation command palette. Registered by MainLayout with a
// .NET object reference; toggles the palette from anywhere. One shell instance is live at a time.
let handler = null;

export function register(dotNetRef) {
    unregister();
    handler = (e) => {
        if ((e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OnCommandKey');
        }
    };
    window.addEventListener('keydown', handler);
}

export function unregister() {
    if (handler) {
        window.removeEventListener('keydown', handler);
        handler = null;
    }
}
