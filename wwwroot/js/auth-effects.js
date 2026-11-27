// Study Buddy — Static SSR Progressive Enhancement for Identity Account Pages
// Initializes cleanly on DOMContentLoaded and Blazor enhancedload; cleans up via AbortController.
// Never blocks native form submission or issues auth cookies in JS.

(() => {
    let activeController = null;

    const EYE_OPEN_SVG =
        '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/>' +
        '<circle cx="12" cy="12" r="3"/></svg>';

    const EYE_OFF_SVG =
        '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        '<path d="M9.88 9.88a3 3 0 1 0 4.24 4.24"/>' +
        '<path d="M10.73 5.08A10.43 10.43 0 0 1 12 5c7 0 10 7 10 7a13.16 13.16 0 0 1-1.67 2.68"/>' +
        '<path d="M6.61 6.61A13.526 13.526 0 0 0 2 12s3.5 7 10 7a9.74 9.74 0 0 0 5.39-1.61"/>' +
        '<line x1="2" y1="2" x2="22" y2="22"/></svg>';

    function initPasswordToggles(signal) {
        const buttons = document.querySelectorAll('[data-password-toggle]');
        for (const btn of buttons) {
            const targetId = btn.getAttribute('data-password-toggle');
            const input = targetId ? document.getElementById(targetId) : null;
            if (!input) {
                continue;
            }

            btn.addEventListener(
                'click',
                () => {
                    const reveal = input.type === 'password';
                    input.type = reveal ? 'text' : 'password';
                    btn.setAttribute('aria-pressed', reveal ? 'true' : 'false');
                    btn.setAttribute('aria-label', reveal ? 'Hide password' : 'Show password');
                    btn.innerHTML = reveal ? EYE_OFF_SVG : EYE_OPEN_SVG;
                },
                { signal }
            );
        }
    }

    function initPasswordStrength(signal) {
        const passwordInput = document.querySelector('[data-password-strength]');
        if (!passwordInput) {
            return;
        }

        const meter = document.querySelector('[data-strength-meter]');
        const bars = meter ? Array.from(meter.querySelectorAll('[data-strength-bar]')) : [];
        const label = meter ? meter.querySelector('[data-strength-label]') : null;
        const reqLength = document.querySelector('[data-req="length"]');
        const reqUpper = document.querySelector('[data-req="upper"]');
        const reqLower = document.querySelector('[data-req="lower"]');
        const reqDigit = document.querySelector('[data-req="digit"]');

        const confirmInput = document.querySelector('[data-password-confirm]');
        const matchHint = document.querySelector('[data-match-hint]');

        const update = () => {
            const val = passwordInput.value || '';
            const hasLen = val.length >= 8;
            const hasUpper = /[A-Z]/.test(val);
            const hasLower = /[a-z]/.test(val);
            const hasDigit = /\d/.test(val);

            reqLength?.classList.toggle('is-met', hasLen);
            reqUpper?.classList.toggle('is-met', hasUpper);
            reqLower?.classList.toggle('is-met', hasLower);
            reqDigit?.classList.toggle('is-met', hasDigit);

            const score = [hasLen, hasUpper, hasLower, hasDigit].filter(Boolean).length;
            const tier =
                val.length === 0
                    ? { cls: '', text: 'Enter a password' }
                    : score <= 1
                        ? { cls: 'is-weak', text: 'Weak' }
                        : score === 2
                            ? { cls: 'is-fair', text: 'Fair' }
                            : score === 3
                                ? { cls: 'is-good', text: 'Good' }
                                : { cls: 'is-strong', text: 'Strong' };

            bars.forEach((bar, idx) => {
                bar.classList.remove('is-weak', 'is-fair', 'is-good', 'is-strong');
                if (val.length > 0 && idx < score && tier.cls) {
                    bar.classList.add(tier.cls);
                }
            });

            if (label) {
                label.textContent = tier.text;
            }

            if (confirmInput && matchHint) {
                const confirmVal = confirmInput.value || '';
                if (!confirmVal) {
                    matchHint.textContent = '';
                    matchHint.classList.remove('is-match', 'is-mismatch');
                } else if (confirmVal === val) {
                    matchHint.textContent = 'Passwords match';
                    matchHint.classList.add('is-match');
                    matchHint.classList.remove('is-mismatch');
                } else {
                    matchHint.textContent = 'Passwords do not match yet';
                    matchHint.classList.add('is-mismatch');
                    matchHint.classList.remove('is-match');
                }
            }
        };

        passwordInput.addEventListener('input', update, { signal });
        confirmInput?.addEventListener('input', update, { signal });
        update();
    }

    function initSubmitFeedback(signal) {
        const forms = document.querySelectorAll('form[data-auth-form]');
        for (const form of forms) {
            form.addEventListener(
                'submit',
                () => {
                    if (typeof form.checkValidity === 'function' && !form.checkValidity()) {
                        return;
                    }
                    const btn = form.querySelector('button[type="submit"][data-loading-text]');
                    if (btn) {
                        btn.classList.add('is-submitting');
                        const loadingText = btn.getAttribute('data-loading-text');
                        if (loadingText) {
                            btn.textContent = loadingText;
                        }
                    }
                },
                { signal }
            );
        }
    }

    function initAuthPages() {
        if (activeController) {
            activeController.abort();
        }
        activeController = new AbortController();
        const { signal } = activeController;

        initPasswordToggles(signal);
        initPasswordStrength(signal);
        initSubmitFeedback(signal);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAuthPages, { once: true });
    } else {
        initAuthPages();
    }

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', initAuthPages);
    } else {
        document.addEventListener('DOMContentLoaded', () => {
            if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
                window.Blazor.addEventListener('enhancedload', initAuthPages);
            }
        });
    }
})();

