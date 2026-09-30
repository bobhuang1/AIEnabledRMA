---
id: kb-0013
title: Firmware update failed or unit will not boot after update
category: software
product_scope: ["*"]
keywords: [firmware, update, upgrade, failed update, bricked, will not boot, software, version]
related: [kb-0003, kb-0014]
---

# Firmware update failed or unit will not boot after update

## 1. Attempt a hard reset

Hold the power key for at least fifteen seconds. This is the only recovery available from
the customer side after a failed update.

## 2. Retry the update from a known-good state

If the unit reaches a bootloader or recovery menu after the reset, re-run the update from a
host with a stable connection and a full battery. A truncated transfer is the most common
cause of a failed update and it is fully recoverable.

## 3. Check whether the unit predates the update

Some published firmware revisions are withdrawn after a fault is found in the release
itself. Check the release notes for the revision the unit is running. If the unit is on a
withdrawn revision, a hard reset returns it to a working state and the update must not be
retried until a corrected release is published.

## 4. Record the firmware revision

Record the exact revision shown on the unit's information screen. A support specialist
needs it to determine whether a known software fault applies, and without it the request
will be escalated rather than resolved.

## What this does not cover

Application software faults inside a customer's own application are not covered here. Those
belong to the application vendor.
