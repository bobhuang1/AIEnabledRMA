---
id: kb-0009
title: Barcode will not decode
category: scanning
product_scope: ["*"]
keywords: [barcode, decode, scan, read, laser, symbology, blurry, damaged label, not reading]
related: [kb-0008, kb-0010]
---

# Barcode will not decode

## 1. Confirm the trigger fires

If the trigger does not produce a scan event, this is a trigger problem, not a decode
problem. See [scan trigger does not fire](kb-0008.md).

## 2. Clean the scan window

Lint on the window is the most common cause of intermittent decode failures. Wipe the
window with a dry lint-free cloth. Do not use solvents; they craze the coating permanently.

## 3. Check the label condition and distance

- Hold the unit 100 to 200 millimetres from the label.
- A label that is wrinkled, torn, faded, or printed on a curved surface may not decode at
  any distance. That is a label problem, not a unit problem.
- Low-contrast labels such as faint grey on white need a longer distance than high-contrast
  black on white.

## 4. Confirm the symbology is enabled

Some symbologies are disabled by default. Test with a symbology that is known to be
enabled before concluding the imager is faulty.

## 5. Test a known-good label

Scan a freshly printed, high-contrast label of an enabled symbology.

- Decodes: the unit is healthy, the original label was the problem.
- Does not decode: imager or illuminator fault. Continue the return.

## Ambient light

Direct sunlight and very bright overhead lighting can both suppress decoding. Test under
normal indoor lighting before recording a hardware fault.
