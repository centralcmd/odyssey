// IntersectionObserver bridge for the /accept-terms document reader (issue #354).
// A sentinel sits at the very end of the scrollable text; when it comes into view the .NET component
// is told the reader has reached the bottom, which enables Accept and retracts the "more below" fade.
// Mirrors infinite-scroll.js — same keyed-observer shape, disconnected on dispose.
//
// The observer fires on registration with the sentinel's current state, so a document short enough to
// fit without scrolling reports "at end" immediately rather than trapping the user behind a scroll
// they cannot perform.
const observers = new Map();

export function observe(id, sentinel, dotNetRef) {
    unobserve(id);
    if (!sentinel || typeof IntersectionObserver === 'undefined') {
        // No observer support: don't hold Accept hostage to a check we can't run.
        dotNetRef.invokeMethodAsync('OnReachedEnd');
        return;
    }

    const io = new IntersectionObserver((entries) => {
        if (entries.some((e) => e.isIntersecting)) {
            dotNetRef.invokeMethodAsync('OnReachedEnd');
        }
    });
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
