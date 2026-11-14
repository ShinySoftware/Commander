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
