---
id: kb-0008
title: Scan trigger does not fire
category: scanning
product_scope: ["*"]
keywords: [trigger, scan, scan button, wont scan, laser, imager, read, barcode, dead trigger]
related: [kb-0009, kb-0010]
---

# Scan trigger does not fire

## 1. Test the trigger mechanically

With the unit powered on, press and release the trigger several times. A unit whose
trigger has lost its tactile click has a worn switch, which is a consumable wear item
rather than a unit fault.

## 2. Check the software trigger configuration

Some host applications map scanning to a software key instead of the hardware trigger.
Confirm the application still has a scan action bound before concluding the trigger is
dead. This is the single most common cause of "my trigger stopped working" and it is not
a fault.

## 3. Clean the scan window

A dirty or scratched scan window degrades reading performance but does not stop the
trigger firing. If the trigger fires and the laser or illuminator is visible but nothing
decodes, see [barcode will not decode](kb-0009.md) instead.

## 4. Test the trigger while charging and unplugged

Some units disable the trigger while on the cradle to prevent accidental scans in a dock.
If the trigger works unplugged but not on the cradle, that is intended behaviour.

## 5. Confirm the trigger event reaches the host

Run the host's diagnostic utility and observe whether a trigger press produces an event
with no barcode in front of the window.

- Event present, no barcode: the trigger and radio are fine, the issue is decoding.
- No event at all: trigger switch, main board, or firmware fault.

## Recording the result

Note the outcome of steps 1 through 5. A trigger fault confirmed at step 5, with a clean
window and a working software trigger, is the evidence needed to continue a return.
