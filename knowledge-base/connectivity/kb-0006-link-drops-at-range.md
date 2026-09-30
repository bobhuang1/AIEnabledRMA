---
id: kb-0006
title: Wireless link drops at range
category: connectivity
product_scope: ["*"]
keywords: [range, distance, drops, intermittent, radio, wifi, interference, far away]
related: [kb-0005]
---

# Wireless link drops at range

## Establish whether this is a range problem or a unit fault

Move the unit progressively closer to the host and note where the link becomes stable.
Repeat the test in a different physical location. These two tests separate the two causes
decisively.

- Stable indoors, unstable in the same room near metal shelving: environmental
  interference, not a unit fault.
- Unstable at two metres with line of sight in two different locations: radio module fault.

## Environmental causes to rule out

- Metal shelving and racking reflect and absorb radio signals. Test in an open area.
- Dense Wi-Fi deployment on the 2.4 GHz band. Test with Wi-Fi disabled on the host.
- Other units in the same area on the same channel. Change the host's Bluetooth channel.
- Radio-absorbing materials such as foam packaging. Remove the unit from any packaging.

## Firmware check

Confirm the unit's firmware revision is at or above the minimum revision published for its
model. A radio-stack defect fixed in firmware is indistinguishable from a hardware fault
from the customer's side, and the update is the cheaper resolution.

## Recording the result

Record the distance at which the link fails, the environment, and the firmware revision.
Without these three facts, a returns decision has nothing to distinguish a genuine radio
fault from a coverage complaint, and the request will be escalated to a specialist rather
than resolved automatically.
