// IntersectionObserver bridge for OdsInfiniteList (Odyssey Design System · card-list paging).
// A component registers its end-of-list sentinel by id; when the sentinel scrolls into view
// (with a 160px lookahead margin, matching the DS) the .NET component is told to append the next
// batch. Observers are keyed by id so each list manages its own, and are disconnected on dispose.
const observers = new Map();

export function observe(id, sentinel, dotNetRef) {
    unobserve(id);
    if (!sentinel || typeof IntersectionObserver === 'undefined') return;
    const io = new IntersectionObserver((entries) => {
        if (entries.some((e) => e.isIntersecting)) {
            dotNetRef.invokeMethodAsync('OnSentinelVisible');
        }
    }, { rootMargin: '160px' });
    io.observe(sentinel);
    observers.set(id, io);
}

export function unobserve(id) {
    const io = observers.get(id);
    if (io) {
        io.disconnect();
        observers.delete(id);
    }
}
