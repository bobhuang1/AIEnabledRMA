---
id: kb-0005
title: Bluetooth pairing fails or drops
category: connectivity
product_scope: ["*"]
keywords: [bluetooth, pair, pairing, unpair, disconnect, drops, connection, host, radio]
related: [kb-0006, kb-0007]
---

# Bluetooth pairing fails or drops

Distinguish three situations before changing anything: the unit has never paired, the unit
paired once and now fails, or the unit drops an established link. They have different
causes.

## Never paired

1. Confirm the unit is fully charged. A unit below roughly ten percent will advertise but
   will refuse the pairing request.
2. Put the unit into pairing mode. Hold the power and scan keys together for five seconds
   until the indicator light blinks rapidly.
3. Forget the unit on the host, restart the host's Bluetooth stack, and pair again. A host
   that has a stale entry for the same hardware address will reject the pairing attempt
   silently.
4. Pair to a second host. If the unit pairs to the second host, the fault is in the first
   host, not the unit.

## Paired once, now failing

1. Forget the unit on the host and in the unit's own paired-device list.
2. Clear the host's Bluetooth pairing database, not just the visible entry. Some operating
   systems keep a separate trusted-device record.
3. Re-pair from a clean state on both sides.

## Drops an established link

1. Note the distance and the environment at the moment of the drop. Metal shelving and
   dense Wi-Fi environments both cause radio drops that are not unit faults.
2. Check whether the unit's firmware is behind the current release for that model. A
   firmware update resolves a known class of radio instability.
3. Test at very short range, under two metres with line of sight. Intermittent behaviour
   that becomes constant at short range points to a failing radio module, which is a
   hardware fault.

## When to record a hardware fault

Record a hardware fault only when the unit fails to pair with a second known-good host
after a clean forget, or when a short-range test is consistently unstable. Intermittent
drops attributable to the environment or to firmware are not unit faults.
