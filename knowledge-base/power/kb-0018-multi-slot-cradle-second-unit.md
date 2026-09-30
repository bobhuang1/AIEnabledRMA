---
id: kb-0018
title: "Vertex Widget charging cradle will not charge a second unit"
category: power
product_scope: ["Vertex Widget"]
keywords: ["cradle", "charging", "second unit", "multi-slot", "queue", "docks"]
related: ["kb-0002", "kb-0001", "kb-0019"]
---

# Vertex Widget charging cradle will not charge a second unit

## Symptom

The cradle charges the first unit placed in it, but a second unit placed alongside it is not
charged. The second unit's indicator stays amber, or the cradle shows a single occupied slot.

## Check first

1. Confirm the cradle is the `VW-CRD-4` multi-slot model. Single-slot cradles do not support a
   second unit, and this is not a fault.
2. Confirm the power supply is rated for the number of slots. A 2-slot cradle with a 1-slot
   supply will charge one unit only.
3. Reseat both units. Contacts on the multi-slot cradle oxidise faster than on single-slot
   cradles and a light clean often restores charging.
4. On the second unit, open **Settings → Power → Dock** and confirm **Multi-slot dock** is on.
   It is off by default on units that shipped before multi-slot support was added.

## If the second unit still will not charge

Collect the cradle's status LED pattern and both units' firmware versions before continuing,
per [fault evidence](kb-0017). A cradle that charges one unit but not a second is normally a
dock-configuration issue on the unit rather than a cradle fault, and is covered by the
standard limited warranty as a configuration repair.

## Out of scope

- Cradle firmware corruption is a different fault; see [firmware update failed](kb-0013).
- A cradle that charges no unit at all is covered in [cradle not charging](kb-0002).
- A stalled charge on a different product's dock is a different fault; see
  [nimbus beacon stalled dock charge](kb-0019).
