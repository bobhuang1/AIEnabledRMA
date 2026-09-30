# Knowledge base

Markdown articles with YAML front matter. This directory is the **only** knowledge source the
RAG server serves from, and it is loaded from disk at startup and on demand, so you can add
an article by dropping a file in and reloading — no code change and no database step.

## File format

```markdown
---
id: kb-0001
title: Short descriptive title
category: power | display | connectivity | scanning | physical | software | merchandise | process
product_scope: ["*"]        # or a list of model / category tokens, e.g. ["AX-200", "handheld-scanner"]
keywords: [word, word, ...]
related: [kb-0002, kb-0003]
---

Body in Markdown.
```

### Front matter fields

| Field | Required | Purpose |
| --- | --- | --- |
| `id` | yes | Stable identifier. This is what the AI must cite, and what gets stored on the RMA line as provenance. `kb-0018` and `kb-0019` are the only articles with a non-universal `product_scope`; they are scoped to two *different* product families and name each other in `related`, so they exercise both the filter and the scope re-check on expansion. |
| `title` | yes | Shown to the customer and used in relevance scoring. |
| `category` | yes | Business grouping. A `category` with no matching article for a session's product family is a corpus-scope signal. |
| `product_scope` | yes | `["*"]` means the article applies to every product family. Otherwise it lists the exact tokens that must match. |
| `keywords` | no | Extra terms for lexical scoring, for concepts the body does not literally contain. |
| `related` | no | Sibling article ids, used to expand a strong match with its neighbours. A sibling is still re-checked against the session's scope, so a cross-family link is followed for universal siblings only and never leaks another family's article. |

`id` must be unique. The loader fails closed on duplicates rather than silently dropping one,
because a duplicate makes citation verification ambiguous.

## Scoping

`product_scope` is what keeps the assistant from answering questions about products the
customer is not returning. An article is a retrieval candidate for a session only when:

- its `product_scope` is `["*"]`, **or**
- at least one of its `product_scope` entries matches one of the session's scope tokens
  (product model, SKU, product category, or product name).

A session with no recognised product tokens is treated as the risky case, not the
permissive one: only `["*"]` articles are candidates. Unknown product, generic advice only.

If no article survives the filter, retrieval returns nothing and the scope guard refuses
without ever calling the AI model. That is the first line of the lockdown, and it is why
adding a product family means adding scoped articles rather than editing a prompt.

### Product-family opt-in

A session's product family must also be enabled in `Rag:EnabledProductFamilies`, which is
**empty by default**. A scoped article is therefore inert until a deployment deliberately
names the family it applies to, so adding an article for a new product cannot by itself
activate product-specific guidance. `product_scope` decides *which* sessions an article may
serve; the opt-in list decides *whether that product is being answered about at all*.

## Adding an article

1. Create the file under the sub-folder for its `category`. Folder names are for humans; the
   `category` front matter value is what the code reads.
2. Give it a unique `id`. Follow the existing `kb-NNNN` convention.
3. Write the body as a checklist where possible. The system prompt asks for steps, and
   ordered steps with recorded outcomes are what confirm a genuine fault.
4. Set `product_scope` deliberately. `["*"]` for genuinely universal articles; a token list
   for anything specific to one product family.
5. Restart the RAG server, or call `POST /rag/reload` in development.

## Test data

Every article here is fictional. The products, models, part references, and symptoms
describe generic hardware and generic faults, and no article refers to a real vendor,
a real product line, or a real support policy. Replace this directory with your own content
before using the system for a real returns process.
