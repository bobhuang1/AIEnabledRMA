// Progressive enhancement only. Every form in the wizard posts and renders correctly with
// JavaScript disabled; this just removes a round trip from the multi-item forms.

(function () {
    'use strict';

    // One description field per item is tedious on a return of several units, so "copy the
    // first description to every item" is offered. It is a convenience, never a default: the
    // descriptions are per-device on purpose, because the repair bench triages each line.
    document.querySelectorAll('[data-copy-first]').forEach(function (button) {
        button.addEventListener('click', function () {
            var form = button.closest('form');
            var inputs = form.querySelectorAll('[data-problem-description]');
            if (inputs.length < 2) {
                return;
            }

            var first = inputs[0].value.trim();
            if (first.length === 0) {
                inputs[0].focus();
                return;
            }

            inputs.forEach(function (input, index) {
                if (index > 0 && input.value.trim().length === 0) {
                    input.value = first;
                }
            });
        });
    });
})();
