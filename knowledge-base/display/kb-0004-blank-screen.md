---
id: kb-0004
title: Screen is blank but the unit has power
category: display
product_scope: ["*"]
keywords: [screen, display, blank, black, backlight, dim, no image, lcd, touch]
related: [kb-0003, kb-0011]
---

# Screen is blank but the unit has power

A unit that haptics on a key press but shows nothing is a display-side fault, not a
power fault. These are the checks that distinguish the two.

## 1. Confirm power is present

Press the power key briefly.

- Haptic pulse and audible click, blank screen: display-side fault, continue below.
- No response at all: this is a power fault, see
  [unit will not power on](kb-0003.md).

## 2. Check the backlight under direct light

Shine a bright light at a shallow angle onto the screen. A panel with a failed backlight
but a working digitiser still shows a faint image under direct light. A completely dark
panel under direct light has a failed LCD, which is a board-level fault.

## 3. Check whether touch still works

Ask the customer to tap a known control, such as the settings key, and watch for the
on-screen highlight.

- Highlight appears, no image: backlight or LCD fault.
- No highlight: digitiser or display connector fault.

Either outcome is a hardware fault. There is no customer-side repair for either.

## 4. Rule out an extreme display setting

Some units have a display timeout that can be set to an extremely short interval. If the
screen blanks faster than the operator can interact with it, press any key to wake it and
adjust the timeout in settings. This is a configuration issue, not a fault, and it does
not warrant a return.
