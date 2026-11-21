window.letsClip = (() => {
    // Changes Run Through Here Animate, Named Cards Slide
    const transition = (dotnet, method, fromTop) => {
        const scroller = document.querySelector(".page-scroll");
        const change = async () => {
            await dotnet.invokeMethodAsync(method).catch(() => {});
            // A New Order Or Filter Reads From Its Start
            if (fromTop) scroller?.scrollTo(0, 0);
        };
        // Only The Page Animates, A Whole Document Swallowed Every Click
        const scope = scroller ?? document.querySelector(".page");
        // No Transition Support Still Makes The Change
        if (!scope?.startViewTransition) return void change();
        // A Newer Change Skips This One, Which Rejects Ready
        scope.startViewTransition(change).ready.catch(() => {});
    };

    // Each Page Remembers Its Scroll, Clips Comes Back There
    const scrolled = new Map();
    document.addEventListener(
        "scroll",
        (event) => event.target instanceof Element && event.target.classList.contains("page-scroll") && scrolled.set(location.pathname, event.target.scrollTop),
        true
    );
    const restoreScroll = () => {
        const scroller = document.querySelector(".page-scroll");
        if (scroller) scroller.scrollTop = scrolled.get(location.pathname) ?? 0;
    };

    // A Dragged Card Hands Its File To Windows
    let dragger = null;
    const dragClips = (dotnet) => (dragger = dotnet);
    document.addEventListener("dragstart", (event) => {
        const card = event.target instanceof Element ? event.target.closest(".hover-card[data-file]") : null;
        if (!card || !dragger) return;
        event.preventDefault();
        dragger.invokeMethodAsync("DragFile", card.dataset.file).catch(() => {});
    });

    // The Last Card Nearing The View Asks For More Cards
    let listEnd = null;
    const watchEnd = (dotnet, method) => {
        listEnd?.disconnect();
        const last = document.querySelector(".clip-grid > :last-child");
        if (!last) return;
        const near = (entries) => entries.some((entry) => entry.isIntersecting) && dotnet.invokeMethodAsync(method).catch(() => {});
        listEnd = new IntersectionObserver(near, { root: last.closest(".page-scroll"), rootMargin: "100% 0px" });
        listEnd.observe(last);
    };

    // Scrolled Just Enough To Show The Whole Panel, Never Animated
    const reveal = (id) => document.getElementById(id)?.scrollIntoView({ block: "nearest" });

    // While Picking A Hotkey Every Press Goes To The Page
    let stopKeys = () => {};
    const watchKeys = (dotnet) => {
        stopKeys();
        const done = (keep) => dotnet.invokeMethodAsync("OnKeysDone", keep).catch(() => {});
        const listener = (event) => {
            if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return;
            event.preventDefault();
            event.stopPropagation();
            const plain = !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey;
            if (plain && event.key === "Escape") done(false);
            else if (plain && event.key === "Enter") done(true);
            else dotnet.invokeMethodAsync("OnKeys", event.code, event.key, event.ctrlKey, event.altKey, event.shiftKey, event.metaKey).catch(() => {});
        };
        const listening = new AbortController();
        const { signal } = listening;
        window.addEventListener("keydown", listener, { signal, capture: true });
        document.addEventListener("visibilitychange", () => document.hidden && done(false), { signal });
        stopKeys = () => {
            listening.abort();
            stopKeys = () => {};
        };
    };

    return { transition, dragClips, watchEnd, restoreScroll, reveal, watchKeys, stopKeys: () => stopKeys() };
})();
