// Screenshots open full size in a lightbox; a click or Escape closes it.
const lightbox = document.querySelector(".lightbox");
const image = lightbox.querySelector("img");

for (const figure of document.querySelectorAll("[data-zoom]")) {
    figure.addEventListener("click", () => {
        const source = figure.querySelector("img");
        image.src = source.src;
        image.alt = source.alt;
        lightbox.hidden = false;
    });
}

const close = () => {
    lightbox.hidden = true;
    image.removeAttribute("src");
};
lightbox.addEventListener("click", close);
document.addEventListener("keydown", e => {
    if (e.key === "Escape" && !lightbox.hidden)
        close();
});

// ---------------------------------------------------------------- motion
// Everything below is decoration: with reduced motion asked for, the page stays still and shows everything at once.
const still = matchMedia("(prefers-reduced-motion: reduce)").matches;

// the menu bar frosts over once the page scrolls, and a line along the top shows how far down you are
const nav = document.querySelector(".nav");
const progress = document.querySelector(".progress");

function onScroll() {
    nav.classList.toggle("scrolled", scrollY > 24);
    const room = document.documentElement.scrollHeight - innerHeight;
    progress.style.setProperty("--progress", room > 0 ? (scrollY / room).toFixed(4) : "0");
}
addEventListener("scroll", onScroll, { passive: true });
onScroll();

// sections come in as they reach the screen: features from their picture's side, grids one after another
function markReveals() {
    for (const showcase of document.querySelectorAll(".showcase")) {
        const copy = showcase.querySelector(".showcase-copy");
        const shot = showcase.querySelector(".shot");
        const flip = showcase.classList.contains("flip");
        copy?.classList.add("reveal", flip ? "from-right" : "from-left");
        shot?.classList.add("reveal", flip ? "from-left" : "from-right");
        if (shot)
            shot.style.setProperty("--delay", ".12s");
    }
    for (const group of document.querySelectorAll(".grid, .keys, .gallery, .stack, .platforms")) {
        [...group.children].forEach((child, i) => {
            child.classList.add("reveal", "zoom");
            child.style.setProperty("--delay", `${Math.min(i, 11) * 0.05}s`);
        });
    }
    for (const heading of document.querySelectorAll(".section h2, .section-eyebrow, .section-lede"))
        heading.classList.add("reveal");
}

function onReveal(entries, observer) {
    for (const entry of entries.filter(x => x.isIntersecting)) {
        entry.target.classList.add("in");
        observer.unobserve(entry.target);
    }
}

// the app in the hero tilts toward the pointer, and settles flat as you scroll past it
const shot = document.querySelector(".hero-shot .window");
let pointer = null;
let tiltFrame = 0;

function tilt() {
    tiltFrame = 0;
    const settle = Math.min(scrollY / 500, 1);
    const [x, y] = pointer ?? [0, 0];
    shot.style.setProperty("--ry", `${(-7 + x * 6) * (1 - settle)}deg`);
    shot.style.setProperty("--rx", `${(3 - y * 4) * (1 - settle)}deg`);
}

function onPointerMove(e) {
    pointer = [e.clientX / innerWidth - 0.5, e.clientY / innerHeight - 0.5];
    tiltFrame ||= requestAnimationFrame(tilt);
}

function onTiltScroll() {
    tiltFrame ||= requestAnimationFrame(tilt);
}

// the keycaps press themselves, one after another, while the keyboard section is on screen
const keys = [...document.querySelectorAll(".key")];
let keyTimer = 0;
let keyIndex = 0;

function pressNext() {
    keys.forEach(k => k.classList.remove("pressed"));
    keys[keyIndex % keys.length].classList.add("pressed");
    keyIndex += 1;
}

function onKeysVisible(entries) {
    const visible = entries.some(x => x.isIntersecting);
    if (visible && !keyTimer)
        keyTimer = setInterval(pressNext, 700);
    else if (!visible && keyTimer) {
        clearInterval(keyTimer);
        keyTimer = 0;
        keys.forEach(k => k.classList.remove("pressed"));
    }
}

if (!still) {
    markReveals();
    const reveals = new IntersectionObserver(onReveal, { rootMargin: "0px 0px -8% 0px", threshold: 0.12 });
    for (const element of document.querySelectorAll(".reveal"))
        reveals.observe(element);

    if (matchMedia("(hover: hover) and (min-width: 961px)").matches) {
        addEventListener("pointermove", onPointerMove, { passive: true });
        addEventListener("scroll", onTiltScroll, { passive: true });
    }

    if (keys.length > 0)
        new IntersectionObserver(onKeysVisible, { threshold: 0.3 }).observe(document.querySelector(".keys"));
}
