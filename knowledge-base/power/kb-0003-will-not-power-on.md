---
id: kb-0003
title: Unit will not power on
category: power
product_scope: ["*"]
keywords: [power on, wont turn on, dead, no power, boot, start up, shutdown, hard reset]
related: [kb-0001, kb-0004]
---

# Unit will not power on

## 1. Confirm the battery is not simply flat

A unit that has been off for several weeks can drain below the point where it will boot.
Put it on a known-good cradle with a known-good supply and leave it for at least thirty
minutes, then try the power key. See [reading the charge indicator light](kb-0001.md).

## 2. Perform a hard reset

Hold the power key for at least fifteen seconds, past the point where the unit would
normally respond. Release, wait five seconds, then press the power key briefly.

- Three flashes of the indicator light means the reset completed and the unit is healthy.
- No flash at all points to a power-delivery fault rather than a software fault.

## 3. Rule out a failed power key

Ask the customer to confirm the unit responds to a key press with a click or a haptic
pulse. A unit that haptics but does not illuminate has a display or backlight fault
rather than a power fault. A unit with no response at all has a power key, main board, or
battery pack fault.

## 4. Check for a failed update

If the unit was updated shortly before it stopped responding, the update may have left it
in a state where it will not boot. A hard reset is the only recovery; there is no partial
recovery from the customer side.

## Recording the result

Note which of the four steps produced a response. This is the evidence the returns system
needs to distinguish a software fault from a hardware fault, and it is the only basis on
which a return is confirmed as a genuine hardware failure.
