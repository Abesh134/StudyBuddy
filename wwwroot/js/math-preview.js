(function () {
    "use strict";

    const DELIMITERS = [
        { left: "$$", right: "$$", display: true },
        { left: "\\[", right: "\\]", display: true },
        { left: "\\(", right: "\\)", display: false },
        { left: "$", right: "$", display: false }
    ];

    function renderElement(element, attempt) {
        if (!element) {
            return;
        }

        if (typeof window.renderMathInElement === "function") {
            try {
                window.renderMathInElement(element, {
                    delimiters: DELIMITERS,
                    throwOnError: false,
                    strict: false,
                    trust: false,
                    ignoredTags: ["script", "noscript", "style", "textarea", "pre", "code"]
                });
            } catch {
                // Keep raw LaTeX text visible if KaTeX fails on malformed expressions.
            }
            return;
        }

        // If deferred KaTeX scripts are still loading, retry briefly a few times.
        if (attempt < 10) {
            window.setTimeout(function () {
                renderElement(element, attempt + 1);
            }, 120);
        }
    }

    window.studyBuddyMath = {
        render: function (element) {
            renderElement(element, 0);
        }
    };
})();

