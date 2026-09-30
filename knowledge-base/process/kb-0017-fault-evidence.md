---
id: kb-0017
title: Evidence needed to confirm a genuine hardware fault
category: process
product_scope: ["*"]
keywords: [evidence, confirm, genuine, hardware fault, documentation, record, return criteria]
related: [kb-0016]
---

# Evidence needed to confirm a genuine hardware fault

A return is confirmed as a genuine hardware fault when the evidence below has been gathered.
Without it the request is routed to a specialist for a manual decision rather than being
resolved automatically.

## Required for every case

1. **The symptom, in the operator's words.** Not a part name, not a guess at the cause.
2. **The checks already performed**, and the result of each. A list of steps that were
   attempted with no outcomes is not evidence.
3. **The behaviour at the point of failure.** What was expected, what happened instead.
4. **The environment.** Location, host configuration, and anything unusual at the time.

## Required for specific faults

| Fault | Additional required evidence |
| --- | --- |
| Will not power on | Hard reset produced no indicator flash, and the unit charged on a known-good cradle. |
| No charge in cradle | Supply verified, contacts cleaned, unit reseated, second cradle tried. |
| Blank screen | Key press produces a haptic response, and no image is visible under direct light. |
| Will not pair | Unit fails to pair with a second known-good host after a clean forget on both sides. |
| Link drops | Instability reproduces at under two metres, in two different locations. |
| Not detected by host | Second host also fails to detect it, after the host stack has been restarted. |
| Trigger dead | No trigger event in the host diagnostic utility, with software trigger ruled out. |
| Will not decode | Fails to decode a freshly printed known-good label of an enabled symbology. |
| Reboots or freezes | Free storage available, no single application implicated, current firmware. |

## Recording it

State each check and its outcome in one sentence. "Charged 30 min on good cradle, still no
light" is usable. "It does not work" is not, and will be escalated.
