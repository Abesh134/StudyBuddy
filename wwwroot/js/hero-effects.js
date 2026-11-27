// Study Buddy — Cinematic Hero Parallax & Scroll Reveal Effects
// Pointer events attach ONLY to the hero container; throttled via rAF; zero per-frame Blazor calls.

const instances = new WeakMap();

export function initHeroEffects(heroEl) {
    if (!heroEl) {
        return;
    }

    disposeHeroEffects(heroEl);

    const reducedMotionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    const coarsePointerQuery = window.matchMedia('(pointer: coarse)');
    const parallaxLayers = Array.from(heroEl.querySelectorAll('[data-parallax-depth]'));

    let targetX = 0;
    let targetY = 0;
    let currentX = 0;
    let currentY = 0;
    let rafId = 0;
    let isInView = true;
    let isPaused = heroEl.classList.contains('sb-motion-paused');

    const canParallax = () =>
        !isPaused &&
        isInView &&
        !document.hidden &&
        !reducedMotionQuery.matches &&
        !coarsePointerQuery.matches;

    const applyOffsets = (nx, ny) => {
        for (const layer of parallaxLayers) {
            const depth = parseFloat(layer.dataset.parallaxDepth || '0.2');
            const px = (nx * depth * 28).toFixed(2);
            const py = (ny * depth * 22).toFixed(2);
            layer.style.setProperty('--px', `${px}px`);
            layer.style.setProperty('--py', `${py}px`);
        }
    };

    const resetOffsets = () => {
        targetX = 0;
        targetY = 0;
        currentX = 0;
        currentY = 0;
        if (rafId) {
            cancelAnimationFrame(rafId);
            rafId = 0;
        }
        applyOffsets(0, 0);
    };

    const tick = () => {
        rafId = 0;
        if (!canParallax()) {
            resetOffsets();
            return;
        }

        currentX += (targetX - currentX) * 0.1;
        currentY += (targetY - currentY) * 0.1;
        applyOffsets(currentX, currentY);

        if (Math.abs(targetX - currentX) > 0.002 || Math.abs(targetY - currentY) > 0.002) {
            rafId = requestAnimationFrame(tick);
        }
    };

    const scheduleTick = () => {
        if (!rafId && canParallax()) {
            rafId = requestAnimationFrame(tick);
        }
    };

    const onPointerMove = (e) => {
        if (e.pointerType === 'touch' || !canParallax()) {
            return;
        }
        const rect = heroEl.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) {
            return;
        }
        const relX = (e.clientX - rect.left) / rect.width - 0.5;
        const relY = (e.clientY - rect.top) / rect.height - 0.5;
        targetX = Math.max(-1, Math.min(1, relX * 2));
        targetY = Math.max(-1, Math.min(1, relY * 2));
        scheduleTick();
    };

    const onPointerLeave = () => {
        targetX = 0;
        targetY = 0;
        scheduleTick();
    };

    const onVisibilityChange = () => {
        if (document.hidden) {
            resetOffsets();
        }
    };

    const onReducedMotionChange = () => {
        if (reducedMotionQuery.matches) {
            resetOffsets();
        }
    };

    heroEl.addEventListener('pointermove', onPointerMove, { passive: true });
    heroEl.addEventListener('pointerleave', onPointerLeave, { passive: true });
    document.addEventListener('visibilitychange', onVisibilityChange);
    reducedMotionQuery.addEventListener?.('change', onReducedMotionChange);

    // Pause parallax when hero scrolls out of viewport
    let heroObserver = null;
    if ('IntersectionObserver' in window) {
        heroObserver = new IntersectionObserver(
            (entries) => {
                const entry = entries[0];
                isInView = Boolean(entry && entry.isIntersecting);
                if (!isInView) {
                    resetOffsets();
                }
            },
            { threshold: 0.02 }
        );
        heroObserver.observe(heroEl);
    }

    // Progressive scroll-reveal for landing sections (content stays visible without JS)
    let revealObserver = null;
    if ('IntersectionObserver' in window && !reducedMotionQuery.matches) {
        document.documentElement.classList.add('sb-js-reveal');
        const revealNodes = Array.from(document.querySelectorAll('[data-reveal]'));
        revealObserver = new IntersectionObserver(
            (entries) => {
                for (const entry of entries) {
                    if (entry.isIntersecting) {
                        entry.target.setAttribute('data-reveal', 'visible');
                        revealObserver.unobserve(entry.target);
                    }
                }
            },
            { threshold: 0.12, rootMargin: '0px 0px -32px 0px' }
        );

        for (const node of revealNodes) {
            const rect = node.getBoundingClientRect();
            if (rect.top > window.innerHeight * 0.92) {
                node.setAttribute('data-reveal', 'pending');
                revealObserver.observe(node);
            } else {
                node.setAttribute('data-reveal', 'visible');
            }
        }
    }

    const controller = {
        setPaused(paused) {
            isPaused = Boolean(paused);
            heroEl.classList.toggle('sb-motion-paused', isPaused);
            if (isPaused) {
                resetOffsets();
            }
        },
        dispose() {
            resetOffsets();
            heroEl.removeEventListener('pointermove', onPointerMove);
            heroEl.removeEventListener('pointerleave', onPointerLeave);
            document.removeEventListener('visibilitychange', onVisibilityChange);
            reducedMotionQuery.removeEventListener?.('change', onReducedMotionChange);
            heroObserver?.disconnect();
            revealObserver?.disconnect();
        }
    };

    instances.set(heroEl, controller);
}

export function setHeroMotionPaused(heroEl, paused) {
    const controller = instances.get(heroEl);
    if (controller) {
        controller.setPaused(paused);
    } else if (heroEl) {
        heroEl.classList.toggle('sb-motion-paused', Boolean(paused));
    }
}

export function disposeHeroEffects(heroEl) {
    const controller = instances.get(heroEl);
    if (controller) {
        controller.dispose();
        instances.delete(heroEl);
    }
}

