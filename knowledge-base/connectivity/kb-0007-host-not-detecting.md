---
id: kb-0007
title: Host software does not detect the unit
category: connectivity
product_scope: ["*"]
keywords: [not detected, software, host, driver, discovery, invisible, computer, pc]
related: [kb-0005, kb-0008]
---

# Host software does not detect the unit

This is a host-side problem until proven otherwise. Do not treat it as a unit fault until
the host configuration has been ruled out.

## 1. Confirm the unit is paired and connected

Check the host's device list. A unit that is paired but not currently connected will not
appear in discovery. See [bluetooth pairing fails or drops](kb-0005.md).

## 2. Restart the host's Bluetooth stack

Disable and re-enable the host adapter, or restart the host. This clears a wedged stack
that stops discovering new or reconnected devices.

## 3. Check for adapter conflicts

An internal adapter and an external USB adapter in the same host will fight over the same
hardware address. Remove one. This is a common cause on laptops that have been docked and
undocked repeatedly.

## 4. Check the host's device list for a phantom entry

If the host shows the hardware address but reports the unit as offline, the unit is
reachable and the software is the problem. Re-pair the unit to clear the stale security
record.

## 5. Test on a second host

A unit that works on a second host is a host configuration problem. No return is needed.
A unit that fails to appear on any second host, after the steps above, has a host
interface fault on the unit itself.

## Note on the software application

Application-level discovery problems, such as an item not appearing in a list inside the
application despite being connected at the operating-system level, are not covered here.
Those belong to the application vendor's support channel.
