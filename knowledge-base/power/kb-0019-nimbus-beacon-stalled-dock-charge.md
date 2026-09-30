---
id: kb-0019
title: "Nimbus Beacon dock reports a stalled charge"
category: power
product_scope: ["Nimbus Beacon"]
keywords: ["dock", "charging", "stalled", "beacon", "queue", "contacts", "clean"]
related: ["kb-0002", "kb-0018"]
---

# Nimbus Beacon dock reports a stalled charge

## Symptom

The Nimbus Beacon is placed in its dock and the charging indicator turns amber but never
reaches green. The dock reports a stalled charge rather than no charge, and the unit cannot be
used while docked.

## Check first

1. Confirm the dock is the `NB-DK-2` model. Earlier docks do not report charge state and
   require a firmware update before this behaviour can be read at all.
2. Reseat the Beacon. The dock's pogo pins sit slightly proud of the case and a worn or
   dirty contact is the most common cause of a stalled-but-present charge.
3. Clean the pins with an isopropyl wipe. Do not use metal tools, which scratch the plating
   and turn a cleanable contact into a replacement.
4. Confirm the dock's firmware is current. A dock left on an early firmware revision can
   report a stalled charge for a unit that is in fact charging.

## If the charge still stalls

Collect the dock's firmware version and the Beacon's charging logs before continuing, per
[fault evidence](kb-0017). A stalled charge that follows a firmware update is a different
fault from a stalled charge on an idle dock, and only the first is covered automatically.

## Out of scope

- A dock that charges no unit at all is covered in [cradle not charging](kb-0002).
- Multi-slot cradle behaviour belongs to a different product; see
  [multi-slot cradle second unit](kb-0018).
