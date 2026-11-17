// Rows And Cards Marked For It Answer Enter And Space
document.addEventListener("keydown", (event) => {
    if ((event.key === "Enter" || event.key === " ") && event.target instanceof HTMLElement && event.target.hasAttribute("data-keyclick")) {
        event.preventDefault();
        event.target.click();
    }
});

// Pointer Or Keys, So A Picked Select Keeps No Ring
for (const [type, input] of [["pointerdown", "pointer"], ["keydown", "keys"]]) {
    document.addEventListener(type, () => (document.documentElement.dataset.input = input), true);
}

// A Focused Trim Field Holds Plain Seconds, Selected
document.addEventListener("focusin", (event) => {
    const field = event.target instanceof HTMLInputElement ? event.target.closest("[data-seconds]") : null;
    if (!field) return;
    event.target.dataset.shown = event.target.value;
    event.target.value = field.dataset.seconds;
    event.target.select();
});

// Left Untouched It Shows Its Time Again
document.addEventListener("focusout", (event) => {
    const field = event.target instanceof HTMLInputElement ? event.target.closest("[data-seconds]") : null;
    if (field && event.target.value === field.dataset.seconds) event.target.value = event.target.dataset.shown;
});

// Its Set Here Icon Wins Over A Half Typed Time
document.addEventListener(
    "mousedown",
    (event) => {
        const target = event.target instanceof Element ? event.target : null;
        const input = target?.closest(".mud-input-adornment") ? target.closest(".trim-field")?.querySelector("input") : null;
        if (!input || input !== document.activeElement) return;
        input.value = input.dataset.shown;
        input.blur();
    },
    true
);

// Escape Closes An Open Menu, Focus Returns To Its Button
window.addEventListener(
    "keydown",
    (event) => {
        if (event.key !== "Escape") return;
        const activator = document.querySelector('.mud-menu :is(button, .mud-menu-activator)[aria-expanded="true"]');
        if (!activator) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        activator.click();
        activator.focus?.();
    },
    true
);

// Fine Lines Snap To Screen Pixels, Following Scale Changes
const trackDevicePixel = () => {
    const ratio = devicePixelRatio;
    document.documentElement.style.setProperty("--device-pixel", `${1 / ratio}px`);
    // A Narrow Range, The Ratio Is Rarely An Exact Decimal
    matchMedia(`(min-resolution: ${ratio - 0.001}dppx) and (max-resolution: ${ratio + 0.001}dppx)`).addEventListener("change", trackDevicePixel, { once: true });
};
trackDevicePixel();

// Resting On A Card Plays Its Clip, Silently
(() => {
    let previewed = null;

    const stopPreview = () => {
        if (!previewed) return;
        const { holder, timer, frame, video, bar } = previewed;
        previewed = null;
        clearTimeout(timer);
        cancelAnimationFrame(frame);
        delete holder.dataset.playing;
        if (!video) return;
        // After Its Fade, Then The File Is Let Go
        setTimeout(() => {
            video.removeAttribute("src");
            video.load();
            video.remove();
            bar.remove();
        }, 200);
    };

    const startPreview = (holder) => {
        const entry = { holder, frame: 0 };
        previewed = entry;
        // A Pointer Only Passing Over Loads Nothing
        entry.timer = setTimeout(() => {
            const video = document.createElement("video");
            video.muted = true;
            video.loop = true;
            video.src = holder.dataset.clip;
            const bar = document.createElement("div");
            bar.className = "clip-preview-bar";
            const fill = bar.appendChild(document.createElement("div"));
            holder.append(video, bar);
            Object.assign(entry, { video, bar });
            // Left Before It Played, The Picture Stays
            video.addEventListener("playing", () => previewed === entry && (holder.dataset.playing = ""), { once: true });
            video.play().catch(() => {});
            const follow = () => {
                if (previewed !== entry) return;
                if (!holder.isConnected) return stopPreview();
                if (video.duration) fill.style.transform = `scaleX(${video.currentTime / video.duration})`;
                entry.frame = requestAnimationFrame(follow);
            };
            entry.frame = requestAnimationFrame(follow);
        }, 250);
    };

    // Anywhere On The Card, Never On Its Buttons
    let pointer = null;
    const hover = (event) => {
        // Cards Scrolling Under A Still Pointer Start Nothing, Saving Frames
        const moved = pointer?.x !== event.clientX || pointer?.y !== event.clientY;
        pointer = { x: event.clientX, y: event.clientY };
        const target = event.target instanceof Element ? event.target : null;
        const card = target && !target.closest("button") ? target.closest(".hover-card") : null;
        const holder = card?.querySelector(".clip-preview") ?? null;
        if (holder === (previewed?.holder ?? null)) return;
        stopPreview();
        if (holder && moved && !matchMedia("(prefers-reduced-motion: reduce)").matches) startPreview(holder);
    };
    document.addEventListener("pointerover", hover);
    document.addEventListener("pointermove", hover);

    // Opening It, Leaving Or Hiding The Window Ends One
    document.addEventListener("pointerdown", stopPreview);
    document.addEventListener("visibilitychange", stopPreview);
    document.documentElement.addEventListener("pointerleave", stopPreview);
})();

// Tooltips Stay Inside The Window, Their Arrow On The Button
(() => {
    const margin = 8;
    const keepInside = (tip) => {
        // Layout Sizes, So The Opening Scale Never Misleads
        const left = parseFloat(tip.style.left);
        if (Number.isNaN(left)) return;
        const right = left + tip.offsetWidth;
        const shift = right > innerWidth - margin ? innerWidth - margin - right : left < margin ? margin - left : 0;
        if (tip.style.getPropertyValue("--tip-shift") !== `${shift}px`) tip.style.setProperty("--tip-shift", `${shift}px`);
    };
    const observer = new MutationObserver((records) => {
        for (const { target } of records) {
            if (target instanceof HTMLElement && target.classList.contains("mud-tooltip") && target.classList.contains("mud-popover-open")) keepInside(target);
        }
    });
    // Only The Popovers Are Watched, Never The Busy Player
    const watch = () => {
        const provider = document.querySelector(".mud-popover-provider");
        if (!provider) return void setTimeout(watch, 500);
        observer.observe(provider, { subtree: true, attributes: true, attributeFilter: ["class", "style"] });
    };
    watch();
})();
