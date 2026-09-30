---
id: kb-0014
title: Unit reboots or freezes repeatedly
category: software
product_scope: ["*"]
keywords: [reboot, restart, freeze, frozen, crash, loop, unstable, restarts]
related: [kb-0013, kb-0004]
---

# Unit reboots or freezes repeatedly

## 1. Record the frequency and the trigger

A unit that reboots once after an update is expected. A unit that reboots repeatedly, or
that freezes under a specific action, is a fault. The trigger matters more than the
frequency, so record what the operator was doing when it happened.

## 2. Check storage and memory pressure

A unit with almost no free storage can freeze during application start. Check free space
and clear temporary files. A unit with more than roughly ninety percent storage in use will
misbehave, and clearing it resolves the issue without a return.

## 3. Isolate to a single application

If the freeze happens only inside one application, note the application and version. That
is an application fault, not a unit fault, and it is resolved through the application's
own support channel.

## 4. Update the firmware

A firmware revision with a known stability fault will produce exactly this pattern. Check
the unit's revision against the release notes and update if a fix exists.

## 5. Hard reset as a last software step

Hold the power key for at least fifteen seconds. This clears transient state. If the unit
returns to normal after a reset and stays normal, record that fact; it is useful evidence
but does not by itself close the fault, because a repeat is still possible.

## When to continue a return

Continue when the unit reboots or freezes with free storage available, with no single
application implicated, and on a current firmware revision.
